using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SpotifyAPI.Web;

namespace SLSKDONET.Services;

/// <summary>
/// Centralized orchestrator for all import operations.
/// Handles the entire import pipeline from source parsing to library persistence.
/// </summary>
public class ImportOrchestrator
{
    private readonly ILogger<ImportOrchestrator> _logger;
    private readonly ImportPreviewViewModel _previewViewModel;
    private readonly DownloadManager _downloadManager;
    private readonly INavigationService _navigationService;
    private readonly Views.INotificationService _notificationService;
    private readonly ILibraryService _libraryService;
    private readonly SpotifyAuthService? _spotifyAuth;

    // CancellationTokenSource for the active streaming preview task
    private CancellationTokenSource? _streamCts;

    // The import currently on the preview page, so its error state can offer Retry / Reconnect.
    private IImportProvider? _activeProvider;
    private string? _activeInput;

    public ImportOrchestrator(
        ILogger<ImportOrchestrator> logger,
        ImportPreviewViewModel previewViewModel,
        DownloadManager downloadManager,
        INavigationService navigationService,
        Views.INotificationService notificationService,
        ILibraryService libraryService,
        SpotifyAuthService? spotifyAuth = null)
    {
        _logger = logger;
        _previewViewModel = previewViewModel;
        _downloadManager = downloadManager;
        _navigationService = navigationService;
        _notificationService = notificationService;
        _libraryService = libraryService;
        _spotifyAuth = spotifyAuth;
    }

    /// <summary>
    /// Unified import: opens the preview immediately, streams the provider's batches into it,
    /// and hands the user's confirmed selection to DownloadManager. Detects a previous import of
    /// the same source (deterministic id, URL, Spotify playlist id) so the preview can offer to
    /// update that playlist instead of creating a duplicate.
    /// </summary>
    public async Task StartImportWithPreviewAsync(IImportProvider provider, string input)
    {
        if (provider == null)
        {
            _logger.LogError("Failed to start import: provider was null");
            _notificationService.Show("Import error", "Import provider is unavailable.", Views.NotificationType.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            _logger.LogWarning("Failed to start import from {Provider}: input was empty", provider.Name);
            _notificationService.Show("Import error", "Import input is empty.", Views.NotificationType.Error);
            return;
        }

        var providerName = string.IsNullOrWhiteSpace(provider.Name) ? provider.GetType().Name : provider.Name;

        if (provider is not IStreamingImportProvider streamProvider)
        {
            _logger.LogError("Provider {Provider} must implement IStreamingImportProvider", providerName);
            _notificationService.Show("Import error", $"{providerName} can't be previewed.", Views.NotificationType.Error);
            return;
        }

        try
        {
            _logger.LogInformation("Starting unified import from {Provider}", providerName);

            // Deterministic id: Hash(normalized input) gives the same GUID for the same source,
            // so a re-import finds the earlier playlist. Fallbacks cover legacy rows imported
            // under a different id (URL match, then Spotify playlist id).
            var canonicalInput = CanonicalizeUrl(input);
            var normalizedInput = canonicalInput.ToLowerInvariant();
            var newJobId = Utils.GuidGenerator.CreateFromUrl(normalizedInput);
            var existingJob = await FindExistingImportAsync(providerName, newJobId, normalizedInput);
            if (existingJob != null)
                _logger.LogInformation("Found earlier import of this source: {Title} ({Id})", existingJob.SourceTitle, existingJob.Id);

            _activeProvider = provider;
            _activeInput = input;

            // The case-preserved canonical form becomes PlaylistJob.SourceUrl: a Spotify id is
            // case-sensitive base62, and normalizedInput is lowercased for hashing only. A pasted
            // tracklist has no URL of its own (the provider may still report a backlink later),
            // so don't store the whole pasted text as the playlist's "source link".
            var sourceUrl = IsPastedText(input) ? null : canonicalInput;
            _previewViewModel.InitializeStreamingPreview(providerName, providerName, newJobId, sourceUrl, existingJob);

            SetupPreviewCallbacks();
            _navigationService.NavigateTo("ImportPreview");

            // Start streaming: cancel any prior stream and issue a fresh token
            _streamCts?.Cancel();
            _streamCts?.Dispose();
            var cts = new CancellationTokenSource();
            _streamCts = cts;
            _ = Task.Run(async () =>
            {
                try { await StreamPreviewAsync(streamProvider, input, cts.Token); }
                catch (OperationCanceledException) { _logger.LogInformation("Import preview stream cancelled"); }
                catch (Exception ex) { _logger.LogError(ex, "Background stream preview failed"); }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start import from {Provider}", providerName);
            _notificationService.Show("Import error", $"Couldn't start the import: {ex.Message}", Views.NotificationType.Error);
        }
    }

    private static bool IsPastedText(string input) => input.Contains('\n') || input.Contains('\r');

    private async Task<PlaylistJob?> FindExistingImportAsync(string providerName, Guid newJobId, string normalizedInput)
    {
        var existingJob = await _libraryService.FindPlaylistJobAsync(newJobId);
        existingJob ??= await _libraryService.FindPlaylistJobBySourceUrlAsync(normalizedInput);
        if (existingJob == null && providerName.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
            existingJob = await FindExistingSpotifyJobByPlaylistIdAsync(normalizedInput);
        return existingJob is { IsDeleted: false } ? existingJob : null;
    }

    /// <summary>
    /// Import directly to library/downloader without preview UI.
    /// Useful for background syncing or "Import All" scenarios.
    /// </summary>
    public async Task SilentImportAsync(IImportProvider provider, string input)
    {
        if (provider == null)
        {
            _logger.LogError("Silent import skipped: provider was null");
            _notificationService.Show("Sync failed", "Import provider is unavailable.", Views.NotificationType.Error);
            return;
        }

        try
        {
            _logger.LogInformation("Starting silent/background import from {Provider}: {Input}", provider.Name, input);

            if (provider is not IStreamingImportProvider streamProvider)
            {
                _notificationService.Show("Sync failed", $"{provider.Name} can't sync in the background.", Views.NotificationType.Error);
                return;
            }

            var canonicalInput = CanonicalizeUrl(input);
            var normalizedInput = canonicalInput.ToLowerInvariant();
            var newJobId = Utils.GuidGenerator.CreateFromUrl(normalizedInput);
            var existingJob = await FindExistingImportAsync(provider.Name, newJobId, normalizedInput);

            string sourceTitle = provider.Name;
            string sourceType = provider.Name;
            var incoming = new List<Track>();

            await foreach (var batch in streamProvider.ImportStreamAsync(input))
            {
                if (!string.IsNullOrEmpty(batch.SourceTitle) && sourceTitle == provider.Name)
                    sourceTitle = batch.SourceTitle;
                if (!string.IsNullOrEmpty(batch.SourceType) && sourceType == provider.Name)
                    sourceType = batch.SourceType;
                incoming.AddRange(batch.Tracks.Select(Import.ImportTrackMapper.ToTrack));
            }

            if (incoming.Count == 0)
            {
                _logger.LogWarning("Silent import for {Input} yielded 0 tracks", input);
                _notificationService.Show("Sync", $"'{existingJob?.SourceTitle ?? sourceTitle}' has no tracks to sync.", Views.NotificationType.Warning);
                return;
            }

            // Same path as a confirmed preview: QueueProject's gap analysis skips rows already in
            // the playlist (matched by hash, linked library file, Spotify id or artist|title),
            // retries failed ones and links owned files. The old hand-rolled merge here matched by
            // hash only, wrote to newJobId even when the earlier import lived under another id,
            // and renamed the playlist back to the source's title on every sync.
            var job = new PlaylistJob
            {
                Id = existingJob?.Id ?? newJobId,
                SourceUrl = IsPastedText(input) ? existingJob?.SourceUrl : canonicalInput,
                SourceTitle = existingJob?.SourceTitle ?? sourceTitle,
                SourceType = existingJob?.SourceType ?? sourceType,
                DestinationFolder = existingJob?.DestinationFolder ?? string.Empty,
                CreatedAt = existingJob?.CreatedAt ?? DateTime.UtcNow,
                DateUpdated = DateTime.UtcNow
            };
            foreach (var t in incoming) job.OriginalTracks.Add(t);

            var r = await _downloadManager.QueueProject(job, userInitiated: false);

            var message = existingJob != null
                ? r.Added + r.Retried == 0
                    ? $"'{job.SourceTitle}' is up to date ({r.AlreadyInPlaylist} tracks already there)."
                    : $"Synced '{job.SourceTitle}': {r.Added} new, {r.Retried} retried, {r.AlreadyInPlaylist} already there."
                : $"Imported {r.Added} tracks from '{job.SourceTitle}'.";
            _notificationService.Show("Sync complete", message, Views.NotificationType.Success);
        }
        catch (Exception ex)
        {
            var translated = ex as ImportSourceException ?? ImportProviders.SpotifyImportErrors.Translate(ex);
            _logger.LogError(ex, "Silent import failed for {Input}", input);
            _notificationService.Show("Sync failed", translated.Message, Views.NotificationType.Error);
        }
    }

    /// <summary>
    /// Strips tracking query params and a trailing slash, WITHOUT touching case — this is what
    /// must be stored as <see cref="PlaylistJob.SourceUrl"/> and re-sent to a streaming provider,
    /// since a Spotify playlist/album/track id is case-sensitive base62 (e.g. mixed upper/lower
    /// like "37i9dQZF1DXcBWIGoYBM5M"). <see cref="NormalizeImportInput"/> exists separately for
    /// job-id hashing/lookup only, where case-folding is fine because that comparison was already
    /// case-insensitive anyway — but it used to ALSO be what got stored as SourceUrl, silently
    /// lowercasing every synced Spotify playlist/album id into something Spotify's API 404s on
    /// forever after. Confirmed against this environment's real database: both stored Spotify
    /// SourceUrls were fully-lowercase 22-char ids, which is not a real Spotify id — every "Sync"
    /// on those two playlists was refetching a URL that could never have existed.
    /// </summary>
    private static string CanonicalizeUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var value = input.Trim();
        if (value.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        return value;
    }

    /// <summary>Case-folded form of <see cref="CanonicalizeUrl"/>, for job-id hashing and
    /// case-insensitive lookups only — never store this as a URL to be re-fetched later.</summary>
    private static string NormalizeImportInput(string input) => CanonicalizeUrl(input).ToLowerInvariant();

    private async Task<PlaylistJob?> FindExistingSpotifyJobByPlaylistIdAsync(string input)
    {
        var playlistId = ExtractSpotifyPlaylistId(input);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        var allJobs = await _libraryService.LoadAllPlaylistJobsAsync();
        foreach (var job in allJobs)
        {
            if (!job.SourceType.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingId = ExtractSpotifyPlaylistId(job.SourceUrl);
            if (!string.IsNullOrWhiteSpace(existingId) && existingId.Equals(playlistId, StringComparison.OrdinalIgnoreCase))
            {
                return job;
            }
        }

        return null;
    }

    private static string ExtractSpotifyPlaylistId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var input = value.Trim();

        if (input.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = input.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 && parts[1].Equals("playlist", StringComparison.OrdinalIgnoreCase))
            {
                return parts[2].Trim();
            }
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Equals("playlist", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[i + 1].Trim();
                }
            }
        }

        return string.Empty;
    }

    private async Task StreamPreviewAsync(IStreamingImportProvider provider, string input, CancellationToken ct = default)
    {
        try
        {
            await foreach (var batch in provider.ImportStreamAsync(input).WithCancellation(ct))
            {
                if (ct.IsCancellationRequested) break;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Take the first title more specific than the provider's own name
                    if (!string.IsNullOrEmpty(batch.SourceTitle) && _previewViewModel.SourceTitle == provider.Name)
                        _previewViewModel.SourceTitle = batch.SourceTitle;
                    if (!string.IsNullOrEmpty(batch.SourceType) && _previewViewModel.SourceType == provider.Name)
                        _previewViewModel.SourceType = batch.SourceType;
                    if (!string.IsNullOrEmpty(batch.SourceUrl))
                        _previewViewModel.UpdateSourceUrl(batch.SourceUrl!);
                    if (!string.IsNullOrEmpty(batch.SourceOwner) && string.IsNullOrEmpty(_previewViewModel.SourceOwner))
                        _previewViewModel.SourceOwner = batch.SourceOwner;
                    _previewViewModel.ReportExpectedTotal(batch.TotalEstimated);
                });

                await _previewViewModel.AddTracksToPreviewAsync(batch.Tracks);
            }

            // Only once the whole stream is exhausted: clearing the loading state per batch let
            // the user commit a large playlist after its first page.
            if (!ct.IsCancellationRequested)
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(_previewViewModel.CompleteLoading);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Streaming preview cancelled by user");
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            var error = ex as ImportSourceException ?? ImportProviders.SpotifyImportErrors.Translate(ex);
            _logger.LogError(ex, "Import preview stream failed: {Message}", error.Message);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                _previewViewModel.ShowError(error.Message, error.RequiresSignIn));
        }
    }

    private void SetupPreviewCallbacks()
    {
        CleanupCallbacks(); // never double-subscribe
        _previewViewModel.AddedToLibrary += OnPreviewConfirmed;
        _previewViewModel.Cancelled += OnPreviewCancelled;
        _previewViewModel.RetryRequested += OnRetryRequested;
        _previewViewModel.ReconnectRequested += OnReconnectRequested;
    }

    /// <summary>The commit succeeded: report what actually happened, then open the playlist.</summary>
    private void OnPreviewConfirmed(object? sender, PlaylistJob job)
    {
        try
        {
            _logger.LogInformation("Preview confirmed: {Title} with {Count} tracks", job.SourceTitle, job.OriginalTracks.Count);

            var summary = string.IsNullOrWhiteSpace(_previewViewModel.LastCommitSummary)
                ? $"Imported into '{job.SourceTitle}'."
                : _previewViewModel.LastCommitSummary;
            _notificationService.Show(
                _previewViewModel.LastCommitWasNoOp ? "Already up to date" : "Import complete",
                summary,
                _previewViewModel.LastCommitWasNoOp ? Views.NotificationType.Information : Views.NotificationType.Success);

            // LibraryService publishes ProjectAddedEvent on save; LibraryViewModel selects it.
            _navigationService.NavigateTo("Library");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle preview confirmation");
        }
        finally
        {
            CleanupCallbacks();
        }
    }

    private void OnPreviewCancelled(object? sender, EventArgs e)
    {
        _logger.LogInformation("Import preview cancelled");
        _streamCts?.Cancel(); // Stop the background streaming task immediately
        _navigationService.GoBack();
        CleanupCallbacks();
    }

    private async void OnRetryRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_activeProvider != null && _activeInput != null)
                await StartImportWithPreviewAsync(_activeProvider, _activeInput);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retrying the import failed");
            _previewViewModel.ShowError($"Retry failed: {ex.Message}");
        }
    }

    private async void OnReconnectRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_spotifyAuth == null)
            {
                _previewViewModel.ShowError("Spotify sign-in isn't available. Connect Spotify from the Import page.", requiresSignIn: false);
                return;
            }

            await _spotifyAuth.StartAuthorizationAsync();
            if (await _spotifyAuth.IsAuthenticatedAsync() && _activeProvider != null && _activeInput != null)
                await StartImportWithPreviewAsync(_activeProvider, _activeInput);
            else
                _previewViewModel.ShowError("Spotify isn't connected yet. Finish signing in, then retry.", requiresSignIn: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnecting Spotify from the import preview failed");
            _previewViewModel.ShowError($"Couldn't connect Spotify: {ex.Message}", requiresSignIn: true);
        }
    }

    private void CleanupCallbacks()
    {
        _previewViewModel.AddedToLibrary -= OnPreviewConfirmed;
        _previewViewModel.Cancelled -= OnPreviewCancelled;
        _previewViewModel.RetryRequested -= OnRetryRequested;
        _previewViewModel.ReconnectRequested -= OnReconnectRequested;
    }
}
