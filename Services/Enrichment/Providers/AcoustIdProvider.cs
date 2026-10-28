using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Enrichment.Providers;

/// <summary>
/// AcoustID fingerprint lookup for badly tagged files ("Unknown Artist", a file-name title): only
/// runs when identity is missing. Needs a free application key (acoustid.org/new-application) and
/// Chromaprint's <c>fpcalc</c> executable, which ORBIT does not ship: it is looked up next to
/// ORBIT.exe, in a Tools folder and on PATH, and the provider reports <see cref="ProviderAvailability.NeedsTool"/>
/// when it isn't found. At most 3 requests per second, per AcoustID's terms.
/// </summary>
public sealed class AcoustIdProvider : IMetadataProvider
{
    public const string ProviderName = "acoustid";
    public const string LookupUrl = "https://api.acoustid.org/v2/lookup";
    public static readonly RatePolicy Policy = new(3, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(340), TimeSpan.FromMilliseconds(30), 1);
    public const double MinScore = 0.85;

    private readonly ProviderHttp _http;
    private readonly MetadataCredentialStore _keys;
    private readonly ILogger _logger;
    private readonly Func<string?> _locateFpcalc;
    private string? _fpcalcPath;
    private bool _fpcalcSearched;

    public AcoustIdProvider(HttpClient http, IMetadataLookupCache cache, MetadataCredentialStore keys, ILogger<AcoustIdProvider> logger,
        ProviderRuntime? runtime = null, Func<string?>? locateFpcalc = null)
    {
        _keys = keys;
        _logger = logger;
        _locateFpcalc = locateFpcalc ?? FindFpcalc;
        Runtime = runtime ?? new ProviderRuntime(ProviderName, Policy);
        _http = new ProviderHttp(http, Runtime, cache, logger);
    }

    public string Name => ProviderName;
    public string DisplayName => "AcoustID";
    public string Description => "Identifies files with no usable artist/title by their audio fingerprint. Needs a free API key and fpcalc (Chromaprint); 3 requests per second.";
    public MetadataField Supplies => MetadataField.Identity | MetadataField.Album | MetadataField.ExternalIds | MetadataField.Duration;
    public ProviderRuntime Runtime { get; }

    public string? FpcalcPath
    {
        get
        {
            if (!_fpcalcSearched) { _fpcalcPath = _locateFpcalc(); _fpcalcSearched = true; }
            return _fpcalcPath;
        }
    }

    public ProviderAvailability GetAvailability()
    {
        if (Runtime.Breaker.IsDisabledForSession) return ProviderAvailability.Blocked;
        if (!_keys.Has(MetadataCredentialStore.AcoustIdKey)) return ProviderAvailability.NeedsKey;
        return FpcalcPath == null ? ProviderAvailability.NeedsTool : ProviderAvailability.Ready;
    }

    public async Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct)
    {
        if (!track.HasWeakIdentity) return ProviderLookup.NoMatch(); // only for unidentified files
        var key = _keys.Get(MetadataCredentialStore.AcoustIdKey);
        if (key == null) return ProviderLookup.Unavailable("add an AcoustID key in Settings");
        if (FpcalcPath is not { } fpcalc) return ProviderLookup.Unavailable("fpcalc (Chromaprint) not found");
        if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath)) return ProviderLookup.NoMatch();

        var fp = await FingerprintAsync(fpcalc, track.FilePath!, ct).ConfigureAwait(false);
        if (fp == null) return ProviderLookup.Fail("fpcalc could not fingerprint the file");

        var duration = ((int)Math.Round(fp.Value.Duration)).ToString(CultureInfo.InvariantCulture);
        var form = new Dictionary<string, string>
        {
            ["client"] = key,
            ["meta"] = "recordings releasegroups compress",
            ["duration"] = duration,
            ["fingerprint"] = fp.Value.Fingerprint,
            ["format"] = "json",
        };
        var fetch = await _http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, LookupUrl) { Content = new FormUrlEncodedContent(form) },
            "fp:" + Hash(fp.Value.Fingerprint), ct).ConfigureAwait(false);
        if (fetch.Kind == FetchKind.NotFound) return ProviderLookup.NoMatch();
        if (fetch.Kind != FetchKind.Ok) return fetch.ToLookupFailure();

        try
        {
            if (Parse(fetch.Body) is { } c)
            {
                Runtime.CountMatch();
                return ProviderLookup.Match(c);
            }
            return ProviderLookup.NoMatch();
        }
        catch (JsonException ex)
        {
            Runtime.LastError = "malformed response";
            return ProviderLookup.Fail("AcoustID returned malformed JSON: " + ex.Message);
        }
    }

    /// <summary>Best result above <see cref="MinScore"/>. Public for tests.</summary>
    public static MetadataCandidate? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (DeezerProvider.Str(root, "status") != "ok" || !root.TryGetProperty("results", out var results)) return null;

        foreach (var r in results.EnumerateArray().OrderByDescending(r => DeezerProvider.Num(r, "score") ?? 0))
        {
            var score = DeezerProvider.Num(r, "score") ?? 0;
            if (score < MinScore) break;
            if (!r.TryGetProperty("recordings", out var recs) || recs.ValueKind != JsonValueKind.Array) continue;
            foreach (var rec in recs.EnumerateArray())
            {
                var title = DeezerProvider.Str(rec, "title");
                var artists = rec.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", a.EnumerateArray().Select(x => DeezerProvider.Str(x, "name")).Where(n => !string.IsNullOrWhiteSpace(n)))
                    : null;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artists)) continue;
                var c = new MetadataCandidate
                {
                    Confidence = Math.Min(0.97, score),
                    Artist = artists,
                    Title = title,
                    MusicBrainzRecordingId = DeezerProvider.Str(rec, "id"),
                    DurationSeconds = DeezerProvider.Num(rec, "duration"),
                };
                if (rec.TryGetProperty("releasegroups", out var groups) && groups.ValueKind == JsonValueKind.Array && groups.GetArrayLength() > 0)
                    c.Album = DeezerProvider.Str(groups[0], "title");
                return c;
            }
        }
        return null;
    }

    private async Task<(double Duration, string Fingerprint)?> FingerprintAsync(string fpcalc, string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fpcalc)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-json");
        psi.ArgumentList.Add("-length");
        psi.ArgumentList.Add("120");
        psi.ArgumentList.Add(file);
        try
        {
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("[Enrichment] fpcalc exited {Code} for {File}", process.ExitCode, file);
                return null;
            }
            using var doc = JsonDocument.Parse(output);
            var duration = DeezerProvider.Num(doc.RootElement, "duration") ?? 0;
            var fingerprint = DeezerProvider.Str(doc.RootElement, "fingerprint");
            return string.IsNullOrWhiteSpace(fingerprint) ? null : (duration, fingerprint);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "[Enrichment] fpcalc failed for {File}", file);
            return null;
        }
    }

    private static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..32];

    /// <summary>fpcalc next to ORBIT.exe, in Tools/, or on PATH.</summary>
    public static string? FindFpcalc()
    {
        var exe = OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(AppContext.BaseDirectory, "Tools", exe),
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p.Trim(), exe)));
        return candidates.FirstOrDefault(File.Exists);
    }
}
