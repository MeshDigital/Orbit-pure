using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services.UpdateCheck;
using SLSKDONET.Views;

namespace SLSKDONET.Services;

public interface IUpdateCheckService
{
    /// <summary>
    /// Checks GitHub Releases for a newer build and shows a toast when one is found. Both stable
    /// releases and alpha/beta/RC pre-releases are considered, so early builds are not invisible
    /// just because GitHub's "latest" endpoint ignores pre-releases. Throttled to at most once per
    /// <see cref="UpdateCheckService.CheckInterval"/>, and never re-notifies for the same build.
    /// Safe to call on every app start — all failures (network, parsing, rate limiting) are caught
    /// and logged, never thrown.
    /// </summary>
    Task CheckForUpdatesAsync(CancellationToken ct = default);
}

/// <summary>
/// A single unauthenticated GET to the public GitHub Releases API, then a channel-aware comparison
/// against this build's own version. No telemetry, no account, no PII — just "is there a newer
/// stable or early build". Opt-out via AppConfig.EnableUpdateCheck for consistency with this app's
/// other network-touching features (see AutoSearchService's own privacy-first framing).
///
/// Why the full releases list instead of <c>/releases/latest</c>: GitHub's "latest" endpoint hides
/// every pre-release, so alpha/beta/RC builds — and the fact that the user is running one — were
/// previously never surfaced. The full list also lets the notice explain the 0.10.0-alpha → 0.2.0
/// version-line reset by ordering releases by publication date rather than by SemVer alone.
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    internal const string ReleasesApiUrl = "https://api.github.com/repos/MeshDigital/Orbit-pure/releases?per_page=100";
    private const string UserAgent = "ORBIT-App/1.0.0 ( https://github.com/MeshDigital/Orbit-pure )";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly INotificationService _notificationService;
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly HttpClient _httpClient;

    public UpdateCheckService(
        AppConfig config,
        ConfigManager configManager,
        INotificationService notificationService,
        ILogger<UpdateCheckService> logger)
    {
        _config = config;
        _configManager = configManager;
        _notificationService = notificationService;
        _logger = logger;

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public async Task CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (!_config.EnableUpdateCheck)
            return;

        if (_config.LastUpdateCheckUtc.HasValue && DateTime.UtcNow - _config.LastUpdateCheckUtc.Value < CheckInterval)
            return;

        try
        {
            using var response = await _httpClient.GetAsync(ReleasesApiUrl, ct).ConfigureAwait(false);

            // Record the attempt regardless of outcome — a 404 (no releases yet) or a rate limit
            // shouldn't cause this to retry on every single app launch.
            _config.LastUpdateCheckUtc = DateTime.UtcNow;
            await _configManager.SaveAsync(_config).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Update check: GitHub returned {Status}", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var releases = ParseReleases(json);
            if (releases.Count == 0)
                return;

            var currentVersionText = GetCurrentVersionText();
            var notice = UpdatePlanner.Plan(currentVersionText, releases);
            if (notice is null)
                return;

            // Already told the user about this exact build/state — don't repeat it every launch.
            if (string.Equals(_config.LastSeenUpdateVersion, notice.Key, StringComparison.OrdinalIgnoreCase))
                return;

            _config.LastSeenUpdateVersion = notice.Key;
            await _configManager.SaveAsync(_config).ConfigureAwait(false);

            _logger.LogInformation(
                "Update check: showing {Kind} notice for {Key} (current {Current})",
                notice.Kind, notice.Key, currentVersionText);

            _notificationService.Show(
                notice.Title,
                notice.Message,
                NotificationType.Information,
                TimeSpan.FromSeconds(20),
                notice.Url);
        }
        catch (OperationCanceledException)
        {
            // App shutting down mid-check — nothing to log.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed (non-fatal)");
        }
    }

    /// <summary>Parses the GitHub releases array, skipping drafts and non-version tags.</summary>
    internal static IReadOnlyList<UpdateRelease> ParseReleases(string json)
    {
        var releases = new List<UpdateRelease>();

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return releases;

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            if (GetBoolean(element, "draft"))
                continue;

            var release = UpdateRelease.TryCreate(
                GetString(element, "tag_name"),
                GetString(element, "name"),
                GetString(element, "html_url"),
                GetBoolean(element, "prerelease"),
                GetDate(element, "published_at") ?? GetDate(element, "created_at"));

            if (release is not null)
                releases.Add(release);
        }

        return releases;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();

    private static DateTimeOffset? GetDate(JsonElement element, string propertyName)
    {
        var text = GetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }

    private static string GetCurrentVersionText()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    }
}
