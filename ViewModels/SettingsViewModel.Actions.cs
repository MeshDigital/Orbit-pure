using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Platform;
using SLSKDONET.Views; // For AsyncRelayCommand
using SLSKDONET.ViewModels.Settings;
using Avalonia.Threading;

using System.Collections.ObjectModel; // Added
using System.Linq;
using SLSKDONET.Models; // For SearchPolicy and Events
using SLSKDONET.Data.Entities;
using SLSKDONET.Data; // For AppDbContext
using Microsoft.EntityFrameworkCore;
namespace SLSKDONET.ViewModels;


/// <summary>
/// Spotify OAuth, settings persistence, library scan/reconcile/backup, folder management, and Soulseek controls (#307).
/// </summary>
public partial class SettingsViewModel
{
    public SLSKDONET.Services.Setup.SpotifyConnectOutcome LastSpotifyConnectOutcome { get; private set; } = SLSKDONET.Services.Setup.SpotifyConnectOutcome.Failed;

    /// <summary>Spotify OAuth sign-in (browser). Also used by the setup wizard's Connections step.</summary>
    public async Task ConnectSpotifyAsync()
    {
        // Cancel any previous attempts to free up the port
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();
        LastSpotifyConnectOutcome = SLSKDONET.Services.Setup.SpotifyConnectOutcome.Failed;

        try
        {
            IsAuthenticating = true;

            // Ensure config is saved first so the service uses the correct Client ID
            _configManager.Save(_config);

            var success = await _spotifyAuthService.StartAuthorizationAsync(_connectCts.Token);

            if (success)
            {
                // Update display based on new auth state
                SpotifyState = _spotifyAuthService.IsAuthenticated ? SpotifyAuthStatus.Connected : SpotifyAuthStatus.Disconnected;
                SpotifyDisplayName = IsSpotifyConnected ? "Connected" : "Not Connected";
                LastSpotifyConnectOutcome = IsSpotifyConnected
                    ? SLSKDONET.Services.Setup.SpotifyConnectOutcome.Connected
                    : SLSKDONET.Services.Setup.SpotifyConnectOutcome.Failed;
                SpotifyUseApi = true; // Auto-enable API usage on success
                _config.SpotifyUseApi = true; // Ensure backing field is also set
                _configManager.Save(_config); // Save the enabled state
            }
        }

        catch (SLSKDONET.Services.ImportProviders.SpotifyAccountNotAllowlistedException)
        {
            // Already logged with the full explanation by SpotifyAuthService.
            LastSpotifyConnectOutcome = SLSKDONET.Services.Setup.SpotifyConnectOutcome.NotAllowlisted;
            SpotifyDisplayName = "This Spotify account isn't allowlisted for this Client ID";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Spotify connection flow cancelled");
            LastSpotifyConnectOutcome = SLSKDONET.Services.Setup.SpotifyConnectOutcome.Cancelled;
            SpotifyDisplayName = "Cancelled";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "Spotify connection timed out");
            LastSpotifyConnectOutcome = SLSKDONET.Services.Setup.SpotifyConnectOutcome.Failed;
            SpotifyDisplayName = "Timeout - Try again";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Port") || ex.Message.Contains("port"))
        {
            _logger.LogError(ex, "Port conflict during Spotify connection");
            LastSpotifyConnectOutcome = SLSKDONET.Services.Setup.SpotifyConnectOutcome.Failed;
            SpotifyDisplayName = "Port conflict - Restart app";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify connection failed");
            LastSpotifyConnectOutcome = SLSKDONET.Services.Setup.SpotifyConnectOutcome.Failed;
            SpotifyDisplayName = $"Error: {ex.Message.Substring(0, Math.Min(30, ex.Message.Length))}...";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        finally
        {
            IsAuthenticating = false;
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    private async Task DisconnectSpotifyAsync()
    {
        await _spotifyAuthService.SignOutAsync();
        SpotifyState = SpotifyAuthStatus.Disconnected;
        SpotifyDisplayName = "Not Connected";
        SpotifyUseApi = false; // Optional: Auto-disable? Maybe let user decide.
    }

    private async Task TestSpotifyConnectionAsync()
    {
        try
        {
            IsAuthenticating = true;
            _logger.LogInformation("Testing Spotify connection...");

            await _spotifyAuthService.VerifyConnectionAsync();
            var stillAuthenticated = _spotifyAuthService.IsAuthenticated;

            SpotifyState = stillAuthenticated ? SpotifyAuthStatus.Connected : SpotifyAuthStatus.Disconnected;
            SpotifyDisplayName = stillAuthenticated ? "Connected" : "Not Connected";

            if (!stillAuthenticated)
            {
                _logger.LogWarning("Spotify test failed; clearing cached credentials for a clean retry");
                await _spotifyAuthService.ClearCachedCredentialsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify connection test failed");
        }
        finally
        {
            IsAuthenticating = false;
            (TestSpotifyConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Restores every setting on this page to its compiled default and saves immediately. Only
    /// narrowly-scoped resets existed before this (DB schema reset, Spotify full re-auth) — there
    /// was no single action to undo general customization.
    /// Mutates the existing AppConfig instance in place (reflection over its public settable
    /// properties) rather than swapping the reference, since other services/ViewModels hold the
    /// same shared instance and should see the reset immediately too.
    /// </summary>
    private async Task ResetToDefaultsAsync()
    {
        var confirmed = await _dialogService!.ConfirmAsync(
            "Reset to Defaults",
            "This resets every setting on this page back to its default value and saves immediately. This cannot be undone. Continue?",
            confirmLabel: "Reset",
            cancelLabel: "Cancel");

        if (!confirmed) return;

        var defaults = new AppConfig();
        foreach (var prop in typeof(AppConfig).GetProperties())
        {
            if (prop.CanWrite && prop.CanRead)
            {
                prop.SetValue(_config, prop.GetValue(defaults));
            }
        }

        SaveSettings();
        OnPropertyChanged(string.Empty); // Refresh every binding on the page — not just one property.
        _logger.LogInformation("Settings reset to defaults");
    }

    // Every setting auto-saves on change (60+ call sites), so only the explicit Save button confirms
    // with a toast; a failure is reported from any path, since a silently unsaved setting is worse.
    private bool SaveSettings()
    {
        try
        {
            _configManager.Save(_config);
            _ = ApplySoulseekRuntimeConfigurationAsync();
            _logger.LogInformation("Settings saved");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            _eventBus.Publish(new ToastRequestedEvent(
                "Settings not saved", $"Could not write the settings file: {ex.Message}", NotificationType.Error, TimeSpan.FromSeconds(8)));
            return false;
        }
    }

    private void SaveSettingsWithConfirmation()
    {
        if (SaveSettings())
            _eventBus.Publish(new ToastRequestedEvent("Settings saved", "Your settings were saved.", NotificationType.Success, TimeSpan.FromSeconds(3)));
    }

    private async Task ApplySoulseekRuntimeConfigurationAsync()
    {
        try
        {
            await _soulseek.ApplyRuntimeNetworkConfigurationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply Soulseek runtime network configuration after settings save.");
        }
    }

    public void OpenFfmpegDownloadPage()
    {
        const string url = "https://ffmpeg.org/download.html";
        if (!SLSKDONET.Utils.ExternalUrlHelper.TryOpen(url))
        {
            _logger.LogWarning("Could not open the FFmpeg download page");
            _eventBus.Publish(new ToastRequestedEvent("Couldn't open browser", $"Open {url} manually to download FFmpeg.", NotificationType.Warning, TimeSpan.FromSeconds(6)));
        }
    }

    private async Task BrowseDownloadPathAsync()
    {
        var path = await _fileInteractionService.OpenFolderDialogAsync("Select Download Folder");
        if (!string.IsNullOrEmpty(path))
        {
            DownloadPath = path; // Setter triggers SaveSettings
        }
    }

    private async Task BrowseSharedFolderAsync()
    {
        var path = await _fileInteractionService.OpenFolderDialogAsync("Select Shared Folder");
        if (!string.IsNullOrEmpty(path))
        {
            SharedFolderPath = path; // Setter triggers SaveSettings
        }
    }

    private async Task BrowseFrequentSourcesStagingPathAsync()
    {
        var path = await _fileInteractionService.OpenFolderDialogAsync("Select Frequent Sources Staging Folder");
        if (!string.IsNullOrWhiteSpace(path))
        {
            FrequentSourcesStagingPath = path; // Setter triggers SaveSettings
        }
    }

    private async Task ClearSpotifyCacheAsync()
    {
        try
        {
            await _spotifyMetadataService.ClearCacheAsync();
            // Optional: NotificationService usage here if available, for now just log
            _logger.LogInformation("Cache cleared via Settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache via Settings");
        }
    }

    /// <summary>
    /// "Enrich library metadata": queues every LibraryEntries and download track still missing cover
    /// art / album / label / year / genre into the background enrichment pipeline (file tags,
    /// Deezer, MusicBrainz, … per Settings → Connection → Metadata sources). Replaces the old
    /// 100-PlaylistTracks-per-click Spotify loop that never touched folder-imported tracks.
    /// </summary>
    private async Task EnrichMissingMetadataAsync()
    {
        if (_enrichment == null)
        {
            EnrichStatus = "Library enrichment is unavailable.";
            _eventBus.Publish(new ToastRequestedEvent("Enrich library", EnrichStatus, NotificationType.Error, null));
            return;
        }
        try
        {
            await _enrichment.StartAsync();
            EnrichStatus = _enrichment.IsRunning ? "Running in the background · progress on the Analysis page" : _enrichment.NoticeText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start library enrichment");
            EnrichStatus = "Could not start: " + ex.Message;
            _eventBus.Publish(new ToastRequestedEvent("Enrich library", EnrichStatus, NotificationType.Error, null));
        }
        finally
        {
            OnPropertyChanged(nameof(CanEnrich));
            ((AsyncRelayCommand)EnrichMissingMetadataCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// "Scan library": registers the download/shared folders, then runs the import sheet over every
    /// Library Source (phases, counters and a summary live there, not in this status line).
    /// </summary>
    private async Task ScanLibraryAsync()
    {
        if (IsScanning) return;
        try
        {
            IsScanning = true;
            (ScanLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            ScanStatus = "Scanning — see the import sheet";

            await EnsureConfiguredFoldersRegisteredAsync();
            var result = await RunImportSheetAsync("Scanning your library folders");
            ScanStatus = DescribeImport(result);
            await LoadRemovalCandidatesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual scan failed");
            ScanStatus = "Scan failed — see logs.";
        }
        finally
        {
            IsScanning = false;
            (ScanLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The download directory and Soulseek shared folder are always Library Sources.</summary>
    private async Task EnsureConfiguredFoldersRegisteredAsync()
    {
        if (!string.IsNullOrEmpty(_config.DownloadDirectory))
            await _libraryFolderScannerService.EnsureDefaultFolderAsync(_config.DownloadDirectory);
        else
            _logger.LogWarning("No download directory configured for scanning.");

        if (!string.IsNullOrEmpty(_config.SharedFolderPath) &&
            !string.Equals(_config.SharedFolderPath, _config.DownloadDirectory, StringComparison.OrdinalIgnoreCase))
            await _libraryFolderScannerService.EnsureDefaultFolderAsync(_config.SharedFolderPath);
    }

    /// <summary>Runs the import sheet when the shell provides it; tests and headless hosts import directly.</summary>
    private async Task<LibraryImportResult?> RunImportSheetAsync(string title)
    {
        if (_importLauncher != null)
            return await _importLauncher.RunImportAsync(null, title);
        return await _libraryFolderScannerService.ImportFoldersAsync(new LibraryImportRequest { Trigger = LibraryImportTrigger.Maintenance });
    }

    internal static string DescribeImport(LibraryImportResult? result) => result switch
    {
        null => "Another import is running. Its progress is in the import sheet.",
        { Error: not null } => $"Scan stopped: {result.Error}",
        { WasCancelled: true } => $"Scan cancelled. {SLSKDONET.Utils.CountFormat.N(result.Added)} new tracks kept.",
        _ => $"Scan complete: {SLSKDONET.Utils.CountFormat.N(result.Added)} new, {SLSKDONET.Utils.CountFormat.N(result.AlreadyInLibrary + result.Duplicates)} already known, {SLSKDONET.Utils.CountFormat.N(result.ProblemCount)} not added.",
    };

    private async Task ReconcileLibraryAsync()
    {
        if (IsReconciling || _libraryService == null) return;
        try
        {
            IsReconciling = true;
            ReconcileStatus = "Checking...";
            (ReconcileLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            var (reset, checked_, relinked) = await _libraryService.ReconcileLibraryAsync();

            ReconcileStatus = (reset, relinked) switch
            {
                (0, 0) => $"All {checked_} files verified present.",
                (0, > 0) => $"Relinked {relinked} moved/renamed file(s) (checked {checked_}).",
                (> 0, 0) => $"Reset {reset} missing file(s) to re-download queue (checked {checked_}).",
                _ => $"Relinked {relinked} moved file(s), reset {reset} truly missing to re-download queue (checked {checked_})."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library reconciliation failed");
            ReconcileStatus = "Reconciliation failed — see logs.";
        }
        finally
        {
            IsReconciling = false;
            (ReconcileLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task FullLibrarySyncAsync()
    {
        if (IsFullSyncing || IsScanning || IsReconciling || _libraryService == null) return;
        try
        {
            IsFullSyncing = true;
            FullSyncStatus = "Step 1/2: Scanning all folders...";
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ScanLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ReconcileLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            await EnsureConfiguredFoldersRegisteredAsync();
            var import = await RunImportSheetAsync("Full library sync: scanning folders");
            if (import == null)
            {
                FullSyncStatus = "Another import is running. Try again when it finishes.";
                return;
            }
            int totalImported = import.Added;
            int totalUpgraded = import.Upgraded;

            FullSyncStatus = "Step 2/2: Reconciling file paths...";
            var (reset, checked_, relinked) = await _libraryService.ReconcileLibraryAsync();

            await LoadRemovalCandidatesAsync();

            var reconcileSummary = (reset, relinked) switch
            {
                (0, 0) => $"All {checked_} files verified.",
                (0, > 0) => $"Relinked {relinked} moved file(s).",
                (> 0, 0) => $"Reset {reset} missing file(s).",
                _ => $"Relinked {relinked}, reset {reset} missing file(s)."
            };
            FullSyncStatus = $"Done. Imported {totalImported} | Upgraded {totalUpgraded} | {reconcileSummary}";

            _logger.LogInformation("Full library sync complete. Imported: {Imported}, Upgraded: {Upgraded}, Reset: {Reset}", totalImported, totalUpgraded, reset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Full library sync failed");
            FullSyncStatus = "Full sync failed — see logs.";
        }
        finally
        {
            IsFullSyncing = false;
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ScanLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ReconcileLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task LoadRemovalCandidatesAsync()
    {
        try
        {
            await using var context = _dbFactory != null ? _dbFactory.CreateDbContext() : new AppDbContext();
            var logs = await context.LibraryActionLogs
                .AsNoTracking()
                .Where(l => l.ActionType == LibraryActionType.Consolidate)
                .OrderByDescending(l => l.Timestamp)
                .Take(500)
                .ToListAsync();

            var latestBySource = new Dictionary<string, LibraryActionLogEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var log in logs)
            {
                if (string.IsNullOrWhiteSpace(log.SourcePath) || string.IsNullOrWhiteSpace(log.DestinationPath))
                {
                    continue;
                }

                if (!latestBySource.ContainsKey(log.SourcePath))
                {
                    latestBySource[log.SourcePath] = log;
                }
            }

            var items = latestBySource.Values
                .OrderByDescending(v => v.Timestamp)
                .Take(200)
                .Select(v => new RemovalCandidateItemViewModel(
                    sourcePath: v.SourcePath,
                    preferredPath: v.DestinationPath,
                    trackLabel: BuildTrackLabel(v.TrackArtist, v.TrackTitle),
                    timestampUtc: v.Timestamp,
                    openPathAction: OpenPathInExplorer))
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RemovalCandidates.Clear();
                foreach (var item in items)
                {
                    RemovalCandidates.Add(item);
                }

                RemovalCandidatesStatus = items.Count == 0
                    ? "No removal candidates yet. Run a scan to detect lower-quality duplicates."
                    : $"{items.Count} removal candidate(s) ready for review.";

                OnPropertyChanged(nameof(HasRemovalCandidates));
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load removal candidates");
            RemovalCandidatesStatus = "Failed to load removal candidates.";
        }
    }

    private static string BuildTrackLabel(string? artist, string? title)
    {
        var artistPart = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist.Trim();
        var titlePart = string.IsNullOrWhiteSpace(title) ? "Unknown Title" : title.Trim();
        return $"{artistPart} - {titlePart}";
    }

    private void OpenPathInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (System.IO.File.Exists(path))
            {
                SLSKDONET.Services.Platform.PlatformShell.Current.RevealInFolder(path);
                return;
            }

            if (System.IO.Directory.Exists(path))
            {
                SLSKDONET.Services.Platform.PlatformShell.Current.OpenFolder(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open path in explorer: {Path}", path);
        }
    }

    /// <summary>
    /// Allows restarting a stuck authentication flow while UI shows "Authentication Active".
    /// Enabled only when IsAuthenticating is true.
    /// </summary>
    private async Task RestartSpotifyAuthAsync()
    {
        try
        {
            _logger.LogInformation("Restarting Spotify authentication flow...");

            // Forcefully cancel any ongoing attempt
            _connectCts?.Cancel();

            // Clear the authenticating flag to re-enable connect logic (triggers cancellation logic in setter too)
            IsAuthenticating = false;

            // Immediately start a fresh connect attempt
            await ConnectSpotifyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart Spotify authentication");
            IsAuthenticating = false;
        }
        finally
        {
            (RestartSpotifyAuthCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    // Phase E3: Diagnostics snapshot
    private async Task CopyDiagnosticsSnapshotAsync()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== ORBIT Diagnostics Snapshot — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();

            // Search pressure
            if (_lastSearchPressure is { } p)
            {
                sb.AppendLine("[Search Pressure]");
                sb.AppendLine($"  Level         : {p.PressureLevel}");
                sb.AppendLine($"  Response cap  : {p.ResponseLimit}");
                sb.AppendLine($"  File cap      : {p.FileLimit}");
                sb.AppendLine($"  Variation cap : {p.VariationCap}");
                sb.AppendLine($"  Extra delay   : {p.AdditionalDelayMs} ms");
            }
            else
            {
                sb.AppendLine("[Search Pressure]");
                sb.AppendLine("  No pressure data recorded yet (no search performed).");
            }
            sb.AppendLine();

            // Adaptive lane decisions
            sb.AppendLine("[Adaptive Lane Decisions (newest first)]");
            if (AdaptiveLaneDecisionHistory.Count == 0)
            {
                sb.AppendLine("  None recorded.");
            }
            else
            {
                foreach (var entry in AdaptiveLaneDecisionHistory)
                    sb.AppendLine($"  {entry.TimeLabel}  {entry.LaneLabel,-12}  {entry.Reason}");
            }
            sb.AppendLine();

            // Share health
            sb.AppendLine("[Share Health]");
            sb.AppendLine($"  {ShareStatusSummary}");
            sb.AppendLine();

            // FFmpeg
            sb.AppendLine("[FFmpeg]");
            sb.AppendLine($"  {FfmpegStatus}{(!string.IsNullOrWhiteSpace(FfmpegVersion) ? " — " + FfmpegVersion : "")}");

            var snapshot = sb.ToString();

            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(snapshot);
                DiagnosticsSnapshotStatus = "✅ Copied to clipboard";
            }
            else
            {
                DiagnosticsSnapshotStatus = "⚠️ Clipboard unavailable";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy diagnostics snapshot.");
            DiagnosticsSnapshotStatus = "❌ Copy failed";
        }

        // Auto-clear status after 3 s
        await Task.Delay(3000);
        DiagnosticsSnapshotStatus = "";
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }


    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Diagnostic method: Clears cached credentials and re-authenticates.
    /// Useful for testing if the app has a "poisoned" token cache.
    /// </summary>
    private async Task RevokeAndReAuthAsync()

    {
        // Cancel any previous attempts
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        try
        {
            IsAuthenticating = true;
            _logger.LogInformation("Revoking cached credentials and re-authenticating...");

            await _spotifyAuthService.ClearCachedCredentialsAsync();
            SpotifyState = SpotifyAuthStatus.Disconnected;
            SpotifyDisplayName = "Not Connected";

            _logger.LogInformation("Credentials cleared. Starting fresh authentication...");

            // Step 2: Start fresh authentication (WITH CANCELLATION TOKEN)
            var success = await _spotifyAuthService.StartAuthorizationAsync(_connectCts.Token);

            if (success)
            {
                // Update display based on new auth state
                SpotifyState = _spotifyAuthService.IsAuthenticated ? SpotifyAuthStatus.Connected : SpotifyAuthStatus.Disconnected;
                SpotifyDisplayName = IsSpotifyConnected ? "Connected" : "Not Connected";
                SpotifyUseApi = true;
                _configManager.Save(_config);
                _logger.LogInformation("✓ Revoke & Re-auth completed successfully");
            }
            else
            {
                _logger.LogWarning("Revoke & Re-auth failed - user cancelled or error occurred");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Revoke & Re-auth cancelled");
            SpotifyDisplayName = "Cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Revoke & Re-auth failed");
            SpotifyDisplayName = "Error during re-auth";
        }
        finally
        {
            IsAuthenticating = false;
            _connectCts?.Dispose();
            _connectCts = null;
            (RevokeAndReAuthCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    /// <summary>
    /// "Back up now" (backlog B6): a consistent snapshot of library.db into the Backups folder.
    /// Manual backups use their own prefix, so the startup rotation (newest 5) never prunes them.
    /// </summary>
    private async Task BackupLibraryNowAsync()
    {
        try
        {
            var path = await Task.Run(() => SLSKDONET.Services.LibraryDatabaseMaintenance.CreateBackupAsync());
            LastLibraryBackupText = $"Last backup: {System.IO.Path.GetFileName(path)}";
            _eventBus.Publish(new ToastRequestedEvent("Library backed up", $"Saved {System.IO.Path.GetFileName(path)} to the Backups folder.", NotificationType.Success, TimeSpan.FromSeconds(5)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual library backup failed");
            _eventBus.Publish(new ToastRequestedEvent("Backup failed", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(8)));
        }
    }

    private void OpenBackupsFolder()
    {
        var dir = SLSKDONET.Services.LibraryDatabaseMaintenance.GetBackupDirectory();
        try { System.IO.Directory.CreateDirectory(dir); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not create backups folder {Dir}", dir); }
        OpenPathInExplorer(dir);
    }

    private string _lastLibraryBackupText = "Backups are taken automatically every launch (newest 5 kept).";
    public string LastLibraryBackupText
    {
        get => _lastLibraryBackupText;
        private set => SetProperty(ref _lastLibraryBackupText, value);
    }

    private async Task ResetDatabaseAsync()
    {
        // This deletes library.db on the next launch and rebuilds it from the newest startup
        // backup. It used to fire on a single click with no confirmation; anything changed since
        // the last launch would be gone.
        if (_dialogService != null)
        {
            var confirmed = await _dialogService.ConfirmAsync(
                "Refresh Database Schema",
                "ORBIT will restart, delete the library database and rebuild it from the most recent startup backup. " +
                "Changes made since ORBIT last started may be lost. A safety backup of the current database is taken first. Continue?",
                confirmLabel: "Restart and Rebuild",
                cancelLabel: "Cancel");
            if (!confirmed) return;
        }

        try
        {
            // Safety net first: if this fails, don't delete anything.
            var safety = await Task.Run(() => SLSKDONET.Services.LibraryDatabaseMaintenance.CreateBackupAsync());
            _logger.LogWarning("Pre-reset safety backup written to {Path}", safety);

            var markerPath = SchemaMigratorService.ForceResetMarkerPath();

            await System.IO.File.WriteAllTextAsync(markerPath, DateTime.Now.ToString());
            _logger.LogWarning("Force Reset Marker created at {Path}", markerPath);

            // Restart Application
            var processPath = Environment.ProcessPath;
            _logger.LogInformation("Attempting to restart application from: {Path}", processPath);

            if (!string.IsNullOrEmpty(processPath))
            {
                System.Diagnostics.Process.Start(processPath);
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate database reset");
            _eventBus.Publish(new ToastRequestedEvent("Schema refresh not started", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(8)));
        }
    }

    private async Task LoadLibraryFoldersAsync()
    {
        try
        {
            await using var context = _dbFactory != null ? _dbFactory.CreateDbContext() : new AppDbContext();
            var folders = await context.LibraryFolders.OrderBy(f => f.FolderPath).ToListAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LibraryFolders.Clear();
                foreach (var folder in folders)
                {
                    LibraryFolders.Add(new LibraryFolderViewModel(folder));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load library folders in settings");
        }
    }

    /// <summary>Settings → Library "Add folder": the same picker + import sheet as Library folders.</summary>
    private async Task AddLibraryFolderAsync()
    {
        try
        {
            if (_importLauncher != null)
            {
                await _importLauncher.PickFolderAndStartImportAsync();
                return;
            }

            var path = await _fileInteractionService.OpenFolderDialogAsync("Select Music Library Folder");
            if (string.IsNullOrEmpty(path)) return;
            await _libraryFolderScannerService.RegisterFolderAsync(path);
            _eventBus.Publish(new LibraryFoldersChangedEvent());
            _logger.LogInformation("Added library folder: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add library folder");
            _eventBus.Publish(new ToastRequestedEvent("Couldn't add the folder", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(6)));
        }
    }

    private async Task RemoveLibraryFolderAsync()
    {
        if (SelectedLibraryFolder == null) return;

        try
        {
            await using var context = _dbFactory != null ? _dbFactory.CreateDbContext() : new AppDbContext();
            var folder = await context.LibraryFolders.FindAsync(SelectedLibraryFolder.Id);

            if (folder != null)
            {
                context.LibraryFolders.Remove(folder);
                await context.SaveChangesAsync();

                _eventBus.Publish(new LibraryFoldersChangedEvent());
                _logger.LogInformation("Removed library folder: {Path}", folder.FolderPath);

                // Clear selection
                SelectedLibraryFolder = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove library folder");
        }
    }

    private void OnSoulseekLifecycleChanged(ConnectionLifecycleStateChangedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            static string ToFriendlyFailureMessage(string reason)
            {
                if (reason.StartsWith("login rejected:", StringComparison.OrdinalIgnoreCase))
                    return $"Sign-in failed: {reason["login rejected:".Length..].Trim()}";

                if (reason.StartsWith("connect failed:", StringComparison.OrdinalIgnoreCase))
                    return $"Connection failed: {reason["connect failed:".Length..].Trim()}";

                return "Disconnected";
            }

            SoulseekIsConnected = evt.Current == "LoggedIn";
            SoulseekConnectionStatusText = evt.Current switch
            {
                "LoggedIn"     => $"Connected as {SoulseekUsername}",
                "Connecting"   => "Connecting…",
                "LoggingIn"    => "Logging in…",
                "CoolingDown"  => "Cooling down before reconnect…",
                "Disconnecting" => "Disconnecting…",
                "Disconnected" => ToFriendlyFailureMessage(evt.Reason),
                _              => evt.Current
            };

            // Re-evaluate button enabled state
            ((AsyncRelayCommand)SoulseekConnectCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SoulseekDisconnectCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)SoulseekReconnectCommand).RaiseCanExecuteChanged();
        });
    }

    // Soulseek Connection Methods
    private async Task SoulseekConnectAsync()
    {
        try
        {
            // Load stored credentials if available
            var creds = await _credentialService.LoadCredentialsAsync();
            if (!string.IsNullOrEmpty(creds.Password) && !string.IsNullOrEmpty(creds.Username))
            {
                SoulseekConnectionStatusText = "Connecting…";
                await _lifecycle.RequestConnectAsync(creds.Password);
            }
            else
            {
                SoulseekConnectionStatusText = "No stored credentials — use the Sign In overlay to connect first.";
                _logger.LogWarning("No stored credentials available for Soulseek connection");
            }
        }
        catch (Exception ex)
        {
            SoulseekConnectionStatusText = $"Connection failed: {ex.Message}";
            _logger.LogError(ex, "Failed to connect to Soulseek from settings");
        }
    }

    private void SoulseekDisconnect()
    {
        try
        {
            _lifecycle.NotifyManualDisconnect();
            _ = _lifecycle.RequestDisconnectAsync("manual settings disconnect");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect from Soulseek");
        }
    }

    private async Task SoulseekReconnectAsync()
    {
        try
        {
            SoulseekDisconnect();
            await Task.Delay(1000); // Brief pause before reconnecting
            await SoulseekConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconnect to Soulseek");
        }
    }
}
