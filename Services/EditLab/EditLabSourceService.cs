using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using SLSKDONET.Models;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.Services.EditLab;

/// <summary>A library search hit for the Edit Lab's "Add track" picker.</summary>
public sealed record EditLibraryHit(string Hash, string Title, string Artist, string? Key, double? Bpm, int? DurationSeconds)
{
    public string Facts => string.Join("  ", new[]
    {
        Bpm is > 0 ? Bpm.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : null,
        string.IsNullOrWhiteSpace(Key) ? null : Key,
        DurationSeconds is > 0 ? TimeSpan.FromSeconds(DurationSeconds.Value).ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture) : null,
    }.Where(s => s != null));
}

/// <summary>
/// Everything the Edit Lab needs from the library and the stem pipeline: track search, loading a
/// track's analysis into an <see cref="EditSource"/>, stem cache lookups and separation, and turning
/// source files into exactly seekable playback files. Never throws from its constructor.
/// </summary>
public class EditLabSourceService
{
    private readonly ILibraryService? _library;
    private readonly DatabaseService? _database;
    private readonly CachedStemSeparator? _separator;
    private readonly ILogger<EditLabSourceService>? _logger;

    public EditLabSourceService(ILibraryService? library, DatabaseService? database, CachedStemSeparator? separator, ILogger<EditLabSourceService>? logger = null)
    {
        _library = library;
        _database = database;
        _separator = separator;
        _logger = logger;
    }

    /// <summary>True when the Demucs model is installed, so "Separate stems" can run.</summary>
    public virtual bool CanSeparate => _separator?.IsAvailable == true;

    public virtual string SeparatorUnavailableReason =>
        $"The stem separation model isn't installed ({DemucsModelManager.ModelFileName}). Put it in {DemucsModelManager.GetUserModelDirectory()} to enable stems.";

    public virtual async Task<IReadOnlyList<EditLibraryHit>> SearchAsync(string query, int limit = 40)
    {
        if (_library == null || string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return Array.Empty<EditLibraryHit>();
        var entries = await _library.SearchLibraryEntriesWithStatusAsync(query.Trim(), limit).ConfigureAwait(false);
        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.FilePath))
            .Select(e => new EditLibraryHit(e.UniqueHash, e.Title, e.Artist, e.MusicalKey, e.ManualBPM ?? e.BPM, e.DurationSeconds))
            .ToList();
    }

    /// <summary>Builds an <see cref="EditSource"/> from the library: file, precise BPM, key, downbeat,
    /// phrases, sample rate and any cached stems. Throws with a user-readable message when the track
    /// has no local file.</summary>
    public virtual async Task<EditSource> LoadSourceAsync(string trackHash, string? title = null, string? artist = null, string? knownFilePath = null, CancellationToken ct = default)
    {
        LibraryEntry? entry = _library == null ? null : await _library.FindLibraryEntryAsync(trackHash).ConfigureAwait(false);
        string? path = _database == null ? null : await _database.GetLocalFilePathByHashAsync(trackHash).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(knownFilePath) && File.Exists(knownFilePath)) path = knownFilePath;
        if (string.IsNullOrEmpty(path) && entry?.FilePath is { Length: > 0 } ep && File.Exists(ep)) path = ep;
        if (string.IsNullOrEmpty(path))
            throw new FileNotFoundException($"\"{title ?? entry?.Title ?? "This track"}\" has no audio file on this computer, so it can't be edited.");

        var features = _library == null ? null : await _library.GetAudioFeaturesByHashAsync(trackHash).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var source = new EditSource
        {
            TrackHash = trackHash,
            Title = FirstNonEmpty(entry?.Title, title, Path.GetFileNameWithoutExtension(path)),
            Artist = FirstNonEmpty(entry?.Artist, artist, ""),
            FilePath = path,
            Bpm = entry?.ManualBPM is > 0 ? entry.ManualBPM.Value
                : features?.Bpm > 0 ? features.Bpm
                : entry?.TagBPM ?? entry?.BPM ?? 0,
            Key = EditKeyMath.Normalize(entry?.ManualKey) ?? EditKeyMath.Normalize(features?.CamelotKey) ?? EditKeyMath.Normalize(entry?.MusicalKey),
            DownbeatSeconds = features?.DownbeatOffsetSeconds > 0 ? features.DownbeatOffsetSeconds : 0,
            DurationSeconds = features?.TrackDuration > 0 ? features.TrackDuration : entry?.DurationSeconds ?? 0,
            Phrases = ParsePhrases(features?.PhraseSegmentsJson),
        };

        await Task.Run(() => ReadFileFacts(source), ct).ConfigureAwait(false);
        await RefreshStemsAsync(source, ct).ConfigureAwait(false);
        return source;
    }

    private void ReadFileFacts(EditSource source)
    {
        try
        {
            using var reader = PlayableAudio.Open(source.FilePath, out _);
            source.SampleRate = reader.WaveFormat.SampleRate;
            if (source.DurationSeconds <= 0) source.DurationSeconds = reader.TotalTime.TotalSeconds;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException or FormatException or TimeoutException)
        {
            _logger?.LogWarning(ex, "Edit Lab: couldn't read the audio format of {File}", source.FilePath);
            source.SampleRate = 44100;
        }
    }

    /// <summary>Fills <see cref="EditSource.StemPaths"/> from the stem cache (no separation).</summary>
    public virtual async Task<bool> RefreshStemsAsync(EditSource source, CancellationToken ct = default)
    {
        if (_separator == null || !_separator.IsAvailable || !File.Exists(source.FilePath)) return HasAllStems(source);
        try
        {
            var cached = await Task.Run(() => _separator.TryGetCachedStemsAsync(source.FilePath, ct), ct).ConfigureAwait(false);
            if (cached != null) Apply(source, cached);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Edit Lab: couldn't check the stem cache for {File}", source.FilePath);
        }
        return HasAllStems(source);
    }

    public static bool HasAllStems(EditSource source) =>
        new[] { EditStem.Vocals, EditStem.Drums, EditStem.Bass, EditStem.Other }
            .All(s => source.StemPaths.TryGetValue(s, out var p) && File.Exists(p));

    /// <summary>Separates the source into four stems (cache-through; a cache hit is instant).</summary>
    public virtual async Task<IReadOnlyDictionary<EditStem, string>> SeparateAsync(EditSource source, IProgress<float>? progress, CancellationToken ct)
    {
        if (_separator == null || !_separator.IsAvailable) throw new InvalidOperationException(SeparatorUnavailableReason);
        string outputDir = Path.Combine(Path.GetTempPath(), "orbit_stems", Path.GetFileNameWithoutExtension(source.FilePath));
        Directory.CreateDirectory(outputDir);
        var stems = await _separator.SeparateWithProgressAsync(source.FilePath, outputDir, progress, ct).ConfigureAwait(false);
        // Prefer the cache copies: the separator's own output folder is a temp folder.
        var cached = await _separator.TryGetCachedStemsAsync(source.FilePath, ct).ConfigureAwait(false) ?? stems;
        return Apply(source, cached);
    }

    private static Dictionary<EditStem, string> Apply(EditSource source, Dictionary<StemType, string> stems)
    {
        var mapped = new Dictionary<EditStem, string>();
        foreach (var (type, path) in stems)
        {
            EditStem? stem = type switch
            {
                StemType.Vocals => EditStem.Vocals,
                StemType.Drums => EditStem.Drums,
                StemType.Bass => EditStem.Bass,
                StemType.Other => EditStem.Other,
                _ => null,
            };
            if (stem is { } s && File.Exists(path)) mapped[s] = path;
        }
        foreach (var (s, p) in mapped) source.StemPaths[s] = p;
        return mapped;
    }

    /// <summary>
    /// The file a clip should actually read. WAV/AIFF/MP3 are read directly; FLAC, M4A, Opus…
    /// are decoded once by ffmpeg into the playback cache (see <see cref="PlayableAudio"/>) so every
    /// clip seek is exact and instant. Blocking — call off the UI thread.
    /// </summary>
    public virtual string? ResolvePlayablePath(EditSource source, EditStem stem)
    {
        var path = EditRenderPlan.DefaultAudioPath(source, stem);
        if (path == null) return null;
        if (!ExactSeek.NeedsDecodeForward(path)) return path;
        return PlayableAudio.EnsureDecodedCopy(path);
    }

    /// <summary>Compiles a plan off the UI thread with playable files resolved.</summary>
    public virtual Task<EditRenderPlan> CompilePlanAsync(EditProject project, CancellationToken ct = default) => Task.Run(() =>
    {
        int rate = project.BaseSource?.SampleRate is > 0 and var r ? r : 44100;
        var failures = new List<string>();
        var plan = EditRenderPlan.Compile(project, rate, (source, stem) =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return ResolvePlayablePath(source, stem);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Edit Lab: couldn't prepare {File}", source.FilePath);
                failures.Add($"\"{source.Title}\": {ex.Message}");
                return null;
            }
        });
        return failures.Count == 0 ? plan : new EditRenderPlan(plan.SampleRate, plan.Clips, plan.SecondsPerBar, plan.Problems.Concat(failures).Distinct().ToList());
    }, ct);

    private List<EditPhrase> ParsePhrases(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new();
        try
        {
            var segments = JsonSerializer.Deserialize<List<PhraseSegment>>(json) ?? new();
            return segments.Select(s => new EditPhrase { Label = s.Label, StartSeconds = s.Start, DurationSeconds = s.Duration, Bars = s.Bars }).ToList();
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Edit Lab: ignoring unreadable phrase data");
            return new();
        }
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
