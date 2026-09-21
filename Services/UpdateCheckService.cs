using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Views;

namespace SLSKDONET.Services;

public interface IUpdateCheckService
{
    /// <summary>
    /// Checks GitHub Releases for a newer tagged version and shows a toast if one is found.
    /// Throttled to at most once per <see cref="UpdateCheckService.CheckInterval"/>, and never
    /// re-notifies for a version already shown. Safe to call on every app start — all failures
    /// (network, parsing, rate limiting) are caught and logged, never thrown.
    /// </summary>
    Task CheckForUpdatesAsync(CancellationToken ct = default);
}

/// <summary>
/// Single unauthenticated GET to the public GitHub Releases API, comparing the latest tag against
/// this build's own version. No telemetry, no account, no PII — just "is there a newer tag".
/// Opt-out via AppConfig.EnableUpdateCheck for consistency with this app's other network-touching
/// features (see AutoSearchService's own privacy-first framing).
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private const string ApiUrl = "https://api.github.com/repos/MeshDigital/Orbit-pure/releases/latest";
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
            using var response = await _httpClient.GetAsync(ApiUrl, ct).ConfigureAwait(false);

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
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tag_name", out var tagProp))
                return;

            var tagName = tagProp.GetString();
            if (string.IsNullOrWhiteSpace(tagName))
                return;

            var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp)
                ? urlProp.GetString() ?? ApiUrl
                : "https://github.com/MeshDigital/Orbit-pure/releases/latest";

            var currentVersionText = GetCurrentVersionText();
            var latestVersion = NormalizeToVersion(tagName);
            var currentVersion = NormalizeToVersion(currentVersionText);

            if (latestVersion == null || currentVersion == null || latestVersion <= currentVersion)
                return;

            // Already told the user about this exact tag — don't repeat it every launch.
            if (string.Equals(_config.LastSeenUpdateVersion, tagName, StringComparison.OrdinalIgnoreCase))
                return;

            _config.LastSeenUpdateVersion = tagName;
            await _configManager.SaveAsync(_config).ConfigureAwait(false);

            _notificationService.Show(
                "Update Available",
                $"ORBIT {tagName} is available — you're on {currentVersionText}. {releaseUrl}",
                NotificationType.Information,
                TimeSpan.FromSeconds(15));
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

    private static string GetCurrentVersionText()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
    }

    /// <summary>Strips a leading "v", any "-prerelease" tag, and any "+build" metadata, keeping
    /// just the Major.Minor.Patch numeric core for ordering comparison.</summary>
    private static Version? NormalizeToVersion(string raw)
    {
        var trimmed = raw.Trim().TrimStart('v', 'V');

        var plusIndex = trimmed.IndexOf('+');
        if (plusIndex >= 0) trimmed = trimmed[..plusIndex];

        var dashIndex = trimmed.IndexOf('-');
        if (dashIndex >= 0) trimmed = trimmed[..dashIndex];

        return Version.TryParse(trimmed, out var version) ? version : null;
    }
}
