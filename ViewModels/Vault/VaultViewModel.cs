using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Utils;
using SLSKDONET.Services.Vault;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Vault;

/// <summary>One music folder in the Vault, as seen from this computer, on the restore card.</summary>
public sealed class VaultRootRowViewModel : ReactiveObject
{
    public VaultRootRowViewModel(VaultRootPlan plan, bool chosenHere, AsyncRelayCommand<VaultRootRowViewModel> chooseFolder)
    {
        Plan = plan;
        ChosenHere = chosenHere;
        ChooseFolderCommand = chooseFolder;
    }

    public VaultRootPlan Plan { get; }
    public string RootId => Plan.RootId;
    public string Name => Plan.Name;

    /// <summary>True when the folder shown was picked on this screen (not yet saved for this computer).</summary>
    public bool ChosenHere { get; }

    public bool IsUsable => Plan.LocalPathUsable;
    public bool NeedsFolder => !Plan.LocalPathUsable;

    public string FilesText =>
        $"{CountFormat.N(Plan.FilesInVault)} in the Vault"
        + (Plan.LocalPathUsable ? $" · {CountFormat.N(Plan.MissingHere)} missing here" : string.Empty);

    public string FolderText => Plan.LocalPathUsable
        ? (ChosenHere ? "Restore to: " : "On this computer: ") + Plan.LocalPath
        : Plan.LocalPath is { Length: > 0 } p
            ? $"Was {p}, which isn't on this computer. Choose where it goes here."
            : Plan.PathsOnOtherDevices.Count > 0
                ? $"Backed up from {Plan.PathsOnOtherDevices[0]} on another computer. Choose where it goes here."
                : "Choose where it goes on this computer.";

    public AsyncRelayCommand<VaultRootRowViewModel> ChooseFolderCommand { get; }
}

/// <summary>
/// The Backup page (epic #632, Vault phase 1): pick the backup drive once, back up with one button,
/// see when this computer last backed up, and restore — missing music files at once, the library
/// database at the next start after a preview of what changes.
/// </summary>
public sealed class VaultViewModel : ReactiveObject, IDisposable
{
    private readonly VaultService _vault;
    private readonly VaultLocalState _local;
    private readonly IDialogService _dialogs;
    private readonly INotificationService? _notifications;
    private readonly ILogger<VaultViewModel> _logger;
    private readonly Func<string> _libraryDbPath;
    private readonly AsyncRelayCommand _chooseVaultCommand;
    private readonly AsyncRelayCommand _backupCommand;
    private readonly RelayCommand _pauseCommand;
    private readonly AsyncRelayCommand _planRestoreCommand;
    private readonly AsyncRelayCommand _restoreFilesCommand;
    private readonly AsyncRelayCommand _restoreDatabaseCommand;
    private readonly RelayCommand _cancelScheduledRestoreCommand;
    private readonly AsyncRelayCommand _verifyCommand;
    private readonly AsyncRelayCommand<VaultRootRowViewModel> _chooseRootFolderCommand;
    private readonly Dictionary<string, string> _remap = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private string? _vaultRoot;
    private VaultStatus? _status;
    private VaultRestorePlan? _plan;
    private bool _isBusy;
    private double _progress;
    private string _progressText = string.Empty;
    private string _currentFile = string.Empty;
    private string? _resultText;
    private bool _resultIsWarning;
    private string? _errorText;
    private bool _loaded;
    private string _estimateText = string.Empty;
    private bool _estimateTooBig;

    /// <summary>Days without a backup after which the page warns.</summary>
    public const int OverdueDays = 7;

    public VaultViewModel(VaultService vault, VaultLocalState local, IDialogService dialogs, ILogger<VaultViewModel> logger,
        INotificationService? notifications = null)
        : this(vault, local, dialogs, logger, notifications, () => SLSKDONET.Data.OrbitPaths.LibraryDbPath) { }

    internal VaultViewModel(VaultService vault, VaultLocalState local, IDialogService dialogs, ILogger<VaultViewModel> logger,
        INotificationService? notifications, Func<string> libraryDbPath)
    {
        _vault = vault;
        _local = local;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        _libraryDbPath = libraryDbPath;
        _chooseVaultCommand = new AsyncRelayCommand(ChooseVaultAsync, () => !IsBusy);
        _backupCommand = new AsyncRelayCommand(BackupAsync, () => !IsBusy && HasVault);
        _pauseCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        _planRestoreCommand = new AsyncRelayCommand(PlanRestoreAsync, () => !IsBusy && HasSnapshot);
        _restoreFilesCommand = new AsyncRelayCommand(RestoreFilesAsync, () => !IsBusy && RootRows.Any(r => r.IsUsable && r.Plan.MissingHere > 0));
        _verifyCommand = new AsyncRelayCommand(VerifyAsync, () => !IsBusy && IsVaultConnected && _status is { FilesInVault: > 0 });
        _chooseRootFolderCommand = new AsyncRelayCommand<VaultRootRowViewModel>(ChooseRootFolderAsync, _ => !IsBusy);
        _restoreDatabaseCommand = new AsyncRelayCommand(RestoreDatabaseAsync, () => !IsBusy && _plan != null && !IsRestoreScheduled);
        _cancelScheduledRestoreCommand = new RelayCommand(CancelScheduledRestore, () => IsRestoreScheduled);

        if (VaultPendingRestore.LastOutcome is { } outcome)
        {
            _resultText = outcome.Message;
            _resultIsWarning = !outcome.Applied;
        }
    }

    public AsyncRelayCommand ChooseVaultCommand => _chooseVaultCommand;
    public AsyncRelayCommand BackupCommand => _backupCommand;
    public RelayCommand PauseCommand => _pauseCommand;
    public AsyncRelayCommand PlanRestoreCommand => _planRestoreCommand;
    public AsyncRelayCommand RestoreFilesCommand => _restoreFilesCommand;
    public AsyncRelayCommand RestoreDatabaseCommand => _restoreDatabaseCommand;
    public RelayCommand CancelScheduledRestoreCommand => _cancelScheduledRestoreCommand;
    public AsyncRelayCommand VerifyCommand => _verifyCommand;

    /// <summary>The Vault's music folders and where each goes on this computer (filled by the restore check).</summary>
    public ObservableCollection<VaultRootRowViewModel> RootRows { get; } = new();
    public bool HasRootRows => RootRows.Count > 0;

    public string LastVerifiedText => _status == null ? string.Empty
        : _status.LastVerifiedUtc is { } v ? $"Every copy last checked {Ago(v)}" : "Copies not fully checked yet";

    public bool HasVault => _vaultRoot != null;
    public bool HasNoVault => _vaultRoot == null && !IsBusy;
    public bool IsVaultConnected => _vaultRoot != null && Directory.Exists(_vaultRoot);
    public bool IsVaultDisconnected => _vaultRoot != null && !Directory.Exists(_vaultRoot);
    public bool HasSnapshot => _status?.SnapshotUtc != null;
    public string VaultPathText => _vaultRoot ?? string.Empty;

    public string LastBackupText => _status?.LastBackupUtc is { } t
        ? $"Last backup from this computer: {Ago(t)}"
        : HasVault ? "This computer hasn't backed up to this Vault yet." : string.Empty;

    public string VaultContentsText => _status == null ? string.Empty
        : $"{Files(_status.FilesInVault)} · {Size(_status.BytesInVault)}"
          + (_status.Devices.Count > 1 ? $" · {_status.Devices.Count} computers back up here" : string.Empty);

    /// <summary>"Next backup copies 1,203 files (41.2 GB) · 812 GB free on the drive".</summary>
    public string EstimateText { get => _estimateText; private set => this.RaiseAndSetIfChanged(ref _estimateText, value); }
    public bool EstimateTooBig { get => _estimateTooBig; private set => this.RaiseAndSetIfChanged(ref _estimateTooBig, value); }
    public bool HasEstimate => !string.IsNullOrEmpty(_estimateText);

    public bool IsBackupOverdue => HasVault && (_status?.LastBackupUtc is not { } t || DateTime.UtcNow - t > TimeSpan.FromDays(OverdueDays));
    public string OverdueText => _status?.LastBackupUtc == null
        ? "Not backed up yet"
        : $"No backup for over {OverdueDays} days";

    public bool IsBusy { get => _isBusy; private set { this.RaiseAndSetIfChanged(ref _isBusy, value); RaiseAll(); } }
    public double Progress { get => _progress; private set => this.RaiseAndSetIfChanged(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => this.RaiseAndSetIfChanged(ref _progressText, value); }
    public string CurrentFile { get => _currentFile; private set => this.RaiseAndSetIfChanged(ref _currentFile, value); }
    public string? ResultText { get => _resultText; private set { this.RaiseAndSetIfChanged(ref _resultText, value); this.RaisePropertyChanged(nameof(HasResult)); this.RaisePropertyChanged(nameof(HasHealthyResult)); this.RaisePropertyChanged(nameof(HasWarningResult)); } }
    public bool HasResult => !string.IsNullOrEmpty(_resultText);
    public bool HasHealthyResult => HasResult && !_resultIsWarning;
    public bool HasWarningResult => HasResult && _resultIsWarning;
    public string? ErrorText { get => _errorText; private set { this.RaiseAndSetIfChanged(ref _errorText, value); this.RaisePropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool HasPlan => _plan != null;
    public string PlanText => _plan == null ? string.Empty : string.Join(Environment.NewLine, new[]
    {
        $"Snapshot from {_plan.SnapshotUtc?.ToLocalTime():g}: {CountFormat.N(_plan.SnapshotTracks)} tracks, {CountFormat.N(_plan.SnapshotPlaylists)} playlists, {CountFormat.N(_plan.SnapshotCues)} cues.",
        $"Your library now: {CountFormat.N(_plan.CurrentTracks)} tracks, {CountFormat.N(_plan.CurrentPlaylists)} playlists, {CountFormat.N(_plan.CurrentCues)} cues.",
        _plan.Roots.Any(r => !r.LocalPathUsable)
            ? "Some music folders aren't on this computer yet: choose where each goes below, then copy the music back."
            : _plan.FilesMissingLocally > 0
                ? $"{Files(_plan.FilesMissingLocally)} missing on this computer and can be copied back."
                : "Every backed-up music file is still on this computer.",
        _plan.FilesMissingInVault > 0 ? $"{Files(_plan.FilesMissingInVault)} listed in the Vault missing from it." : null,
        $"Before a library restore, your current library is saved to {_plan.CurrentBackupFolder}.",
    }.Where(l => l != null));

    public bool IsRestoreScheduled => VaultPendingRestore.IsScheduled(_libraryDbPath());

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var last = _local.LastVaultPath;
        if (last != null) await OpenVaultAsync(last);
        RaiseAll();
    }

    private async Task ChooseVaultAsync()
    {
        ErrorText = null;
        var folder = await _dialogs.OpenFolderDialogAsync("Choose the backup drive or folder");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var root = VaultService.ResolveVaultRoot(folder);
        _local.RememberVault(root);
        await OpenVaultAsync(root);
    }

    private async Task OpenVaultAsync(string root)
    {
        _vaultRoot = root;
        _plan = null;
        try
        {
            _status = Directory.Exists(root) ? await _vault.GetStatusAsync(root) : null;
            await RefreshEstimateAsync(root);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault: reading {Vault} failed", root);
            ErrorText = ex.Message;
            _status = null;
        }
        RaiseAll();
    }

    private async Task RefreshEstimateAsync(string root)
    {
        EstimateText = string.Empty;
        EstimateTooBig = false;
        if (!Directory.Exists(Path.GetDirectoryName(root) ?? root)) return;
        try
        {
            var (files, bytes, free) = await Task.Run(() => _vault.EstimateAsync(root));
            EstimateTooBig = free is long f && bytes > f;
            EstimateText = (files == 0 ? "Everything is backed up" : $"Next backup copies {Files(files)} ({Size(bytes)})")
                + (free is long fr ? $" · {Size(fr)} free on the drive" : string.Empty)
                + (EstimateTooBig ? " · not enough space" : string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vault: estimating the next backup failed");
        }
        this.RaisePropertyChanged(nameof(HasEstimate));
    }

    private static string Files(int n) => n == 1 ? "1 music file" : $"{CountFormat.N(n)} music files";

    private async Task BackupAsync()
    {
        if (_vaultRoot == null) return;
        if (!Directory.Exists(Path.GetDirectoryName(_vaultRoot) ?? _vaultRoot))
        {
            ErrorText = "The backup drive isn't connected. Plug it in and try again.";
            return;
        }
        ErrorText = null;
        ResultText = null;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        var root = _vaultRoot;
        var progress = new Progress<VaultProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = p.Total > 1 ? $"{p.Phase}: {CountFormat.N(p.Done)} of {CountFormat.N(p.Total)}" : p.Phase;
            CurrentFile = p.Current ?? string.Empty;
        });
        try
        {
            var result = await Task.Run(() => _vault.BackupAsync(root, progress, _cts.Token));
            _resultIsWarning = !result.IsHealthy;
            ResultText = result.Summary;
            _notifications?.Show(result.IsHealthy ? "Backup complete" : "Backup finished with problems", result.Summary,
                result.IsHealthy ? NotificationType.Success : NotificationType.Warning);
        }
        catch (OperationCanceledException)
        {
            _resultIsWarning = true;
            ResultText = "Backup paused. The database snapshot and the files copied so far are kept; Back up now carries on.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault: backup to {Vault} failed", root);
            ErrorText = "Backup failed: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            await OpenVaultAsync(root);
        }
    }

    private async Task PlanRestoreAsync()
    {
        if (_vaultRoot == null) return;
        ErrorText = null;
        try
        {
            var root = _vaultRoot;
            _plan = await Task.Run(() => _vault.PlanRestoreAsync(root));
            await RefreshRootRowsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault: planning a restore from {Vault} failed", _vaultRoot);
            ErrorText = ex.Message;
            _plan = null;
            RootRows.Clear();
        }
        RaiseAll();
    }

    private async Task RefreshRootRowsAsync()
    {
        if (_vaultRoot == null) return;
        var root = _vaultRoot;
        var remap = new Dictionary<string, string>(_remap, StringComparer.Ordinal);
        var plans = await Task.Run(() => _vault.PlanRootsAsync(root, remap));
        RootRows.Clear();
        foreach (var plan in plans)
            RootRows.Add(new VaultRootRowViewModel(plan, _remap.ContainsKey(plan.RootId), _chooseRootFolderCommand));
        this.RaisePropertyChanged(nameof(HasRootRows));
        _restoreFilesCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Points a Vault music folder at a folder on this computer (another drive letter, a Mac).</summary>
    private async Task ChooseRootFolderAsync(VaultRootRowViewModel? row)
    {
        if (row == null) return;
        var folder = await _dialogs.OpenFolderDialogAsync($"Where should \"{row.Name}\" go on this computer?");
        if (string.IsNullOrWhiteSpace(folder)) return;
        _remap[row.RootId] = folder;
        try
        {
            await RefreshRootRowsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault: re-checking folders failed");
            ErrorText = ex.Message;
        }
    }

    private async Task VerifyAsync()
    {
        if (_vaultRoot == null) return;
        ErrorText = null;
        ResultText = null;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        var root = _vaultRoot;
        var progress = new Progress<VaultProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = $"{p.Phase}: {CountFormat.N(p.Done)} of {CountFormat.N(p.Total)}";
            CurrentFile = p.Current ?? string.Empty;
        });
        try
        {
            var result = await Task.Run(() => _vault.VerifyVaultAsync(root, progress, _cts.Token));
            _resultIsWarning = !result.IsHealthy;
            ResultText = result.Summary;
            _notifications?.Show(result.IsHealthy ? "Vault verified" : "Vault has damaged or missing copies", result.Summary,
                result.IsHealthy ? NotificationType.Success : NotificationType.Warning);
        }
        catch (OperationCanceledException)
        {
            _resultIsWarning = true;
            ResultText = "Verify stopped. Nothing was changed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault: verifying {Vault} failed", root);
            ErrorText = "Verify failed: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            await OpenVaultAsync(root);
        }
    }

    private async Task RestoreFilesAsync()
    {
        if (_vaultRoot == null) return;
        ErrorText = null;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        var root = _vaultRoot;
        var progress = new Progress<VaultProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = $"{p.Phase}: {CountFormat.N(p.Done)} of {CountFormat.N(p.Total)}";
            CurrentFile = p.Current ?? string.Empty;
        });
        try
        {
            var remap = new Dictionary<string, string>(_remap, StringComparer.Ordinal);
            var result = await Task.Run(() => _vault.RestoreMissingFilesAsync(root, remap, progress, _cts.Token));
            _resultIsWarning = result.Failed > 0 || result.RootsSkipped > 0;
            ResultText = result.Summary;
            _remap.Clear(); // the choices are now saved for this computer in the Vault
        }
        catch (OperationCanceledException)
        {
            _resultIsWarning = true;
            ResultText = "Stopped. Files already copied back stay.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault: restoring files from {Vault} failed", root);
            ErrorText = "Restoring files failed: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            await PlanRestoreAsync();
        }
    }

    private async Task RestoreDatabaseAsync()
    {
        if (_vaultRoot == null || _plan == null) return;
        var ok = await _dialogs.ConfirmAsync(
            "Restore your library from the Vault?",
            $"At the next start, ORBIT replaces your library ({CountFormat.N(_plan.CurrentTracks)} tracks, {CountFormat.N(_plan.CurrentCues)} cues) "
            + $"with the Vault's snapshot ({CountFormat.N(_plan.SnapshotTracks)} tracks, {CountFormat.N(_plan.SnapshotCues)} cues). "
            + $"Your current library is saved to {_plan.CurrentBackupFolder} first. Keep the backup drive connected.",
            "Restore at next start", "Cancel");
        if (!ok) return;
        try
        {
            _vault.ScheduleDatabaseRestore(_vaultRoot);
            _resultIsWarning = false;
            ResultText = "Library restore staged. Restart ORBIT to apply it.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault: staging a restore from {Vault} failed", _vaultRoot);
            ErrorText = ex.Message;
        }
        RaiseAll();
    }

    private void CancelScheduledRestore()
    {
        try
        {
            VaultPendingRestore.Cancel(_libraryDbPath());
            _resultIsWarning = false;
            ResultText = "Library restore cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Vault: cancelling the staged restore failed");
            ErrorText = ex.Message;
        }
        RaiseAll();
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} days ago ({utc.ToLocalTime():d})";
    }

    private static string Size(long bytes) => NumFormat.Size(bytes);

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(HasVault), nameof(HasNoVault), nameof(IsVaultConnected), nameof(IsVaultDisconnected), nameof(HasSnapshot),
                     nameof(VaultPathText), nameof(LastBackupText), nameof(VaultContentsText), nameof(HasPlan), nameof(PlanText),
                     nameof(IsRestoreScheduled), nameof(IsBackupOverdue), nameof(OverdueText), nameof(HasEstimate),
                     nameof(LastVerifiedText), nameof(HasRootRows),
                 })
            this.RaisePropertyChanged(name);
        _chooseVaultCommand.RaiseCanExecuteChanged();
        _backupCommand.RaiseCanExecuteChanged();
        _pauseCommand.RaiseCanExecuteChanged();
        _planRestoreCommand.RaiseCanExecuteChanged();
        _restoreFilesCommand.RaiseCanExecuteChanged();
        _restoreDatabaseCommand.RaiseCanExecuteChanged();
        _cancelScheduledRestoreCommand.RaiseCanExecuteChanged();
        _verifyCommand.RaiseCanExecuteChanged();
        _chooseRootFolderCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
