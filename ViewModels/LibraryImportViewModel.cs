using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public enum ImportStepState { Pending, Active, Done }

/// <summary>One row of the import sheet's phase list.</summary>
public sealed class LibraryImportStepViewModel : INotifyPropertyChanged
{
    private ImportStepState _state;
    private string _detail = string.Empty;

    public LibraryImportStepViewModel(int number, string label)
    {
        Number = number;
        Label = label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Number { get; }
    public string Label { get; }

    public ImportStepState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State));
            Raise(nameof(IsPending));
            Raise(nameof(IsActive));
            Raise(nameof(IsDone));
        }
    }

    public bool IsPending => State == ImportStepState.Pending;
    public bool IsActive => State == ImportStepState.Active;
    public bool IsDone => State == ImportStepState.Done;

    /// <summary>Short mono line next to the step ("2,650 files").</summary>
    public string Detail
    {
        get => _detail;
        set
        {
            if (_detail == value) return;
            _detail = value;
            Raise(nameof(Detail));
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The import sheet (and its top-bar chip). Adding a folder, "Scan all", Settings → Library and the
/// setup wizard's "Scan now" all open it: a modal sheet that locks the app while the import runs,
/// with phases (scanning, reading tags, adding, done), live mono counters, Cancel, and an optional
/// "Run in background" that turns it into a compact progress chip in the top bar.
/// </summary>
public sealed class LibraryImportViewModel : INotifyPropertyChanged, IDisposable, ILibraryImportLauncher
{
    /// <summary>Issues listed under "Show details"; the rest are summarised as "and N more".</summary>
    internal const int MaxIssuesShown = 300;

    public delegate Task<LibraryImportResult> ImportRunner(LibraryImportRequest request, IProgress<LibraryImportProgress> progress, CancellationToken ct);

    private readonly LibraryFolderScannerService? _scanner;
    private readonly IEventBus _eventBus;
    private readonly IFileInteractionService? _picker;
    private readonly ILogger<LibraryImportViewModel> _logger;
    private readonly Action<Action> _post;
    private readonly ImportRunner _runner;

    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _runInBackgroundCommand;
    private readonly RelayCommand _showSheetCommand;
    private readonly RelayCommand _closeCommand;
    private readonly RelayCommand _openInLibraryCommand;
    private readonly RelayCommand _analyseCommand;
    private readonly RelayCommand _toggleDetailsCommand;

    private CancellationTokenSource? _cts;
    private LibraryImportResult? _result;
    private readonly HashSet<Guid> _queuedFolderIds = new();
    private bool _queuedAll;
    private bool _disposed;

    private bool _isOpen;
    private bool _isInBackground;
    private bool _isRunning;
    private bool _isCompleted;
    private bool _isCancelling;
    private bool _isDetailsVisible;
    private bool _analysisQueued;
    private string _title = string.Empty;
    private string _folderLine = string.Empty;
    private LibraryImportPhase _phase;
    private int _filesFound, _processed, _total, _added, _alreadyInLibrary, _duplicates, _upgraded, _unsupported, _unreadable, _failed;
    private string _currentFile = string.Empty;

    public LibraryImportViewModel(
        LibraryFolderScannerService scanner,
        IEventBus eventBus,
        IFileInteractionService fileInteraction,
        ILogger<LibraryImportViewModel> logger)
        : this(eventBus, logger, scanner, fileInteraction, null, null)
    {
    }

    internal LibraryImportViewModel(
        IEventBus eventBus,
        ILogger<LibraryImportViewModel> logger,
        LibraryFolderScannerService? scanner,
        IFileInteractionService? picker,
        ImportRunner? runner,
        Action<Action>? postToUi)
    {
        _eventBus = eventBus;
        _logger = logger;
        _scanner = scanner;
        _picker = picker;
        _post = postToUi ?? (action => Dispatcher.UIThread.Post(action));
        _runner = runner ?? ((request, progress, ct) =>
            _scanner != null
                ? _scanner.ImportFoldersAsync(request, progress, ct)
                : Task.FromResult(new LibraryImportResult { Error = "The library scanner is unavailable." }));

        Steps = new ObservableCollection<LibraryImportStepViewModel>
        {
            new(1, "Scanning folders"),
            new(2, "Reading tags & checking files"),
            new(3, "Adding to library"),
            new(4, "Done"),
        };

        _cancelCommand = new RelayCommand(Cancel, () => IsRunning && !IsCancelling);
        _runInBackgroundCommand = new RelayCommand(RunInBackground, () => IsRunning && IsOpen);
        _showSheetCommand = new RelayCommand(ShowSheet, () => IsInBackground);
        _closeCommand = new RelayCommand(Close, () => IsCompleted);
        _openInLibraryCommand = new RelayCommand(OpenInLibrary, () => IsCompleted);
        _analyseCommand = new RelayCommand(QueueAnalysis, () => CanAnalyse);
        _toggleDetailsCommand = new RelayCommand(() => IsDetailsVisible = !IsDetailsVisible, () => HasIssues);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LibraryImportStepViewModel> Steps { get; }
    public ObservableCollection<LibraryImportIssue> Issues { get; } = new();

    public ICommand CancelCommand => _cancelCommand;
    public ICommand RunInBackgroundCommand => _runInBackgroundCommand;
    public ICommand ShowSheetCommand => _showSheetCommand;
    public ICommand CloseCommand => _closeCommand;
    public ICommand OpenInLibraryCommand => _openInLibraryCommand;
    public ICommand AnalyseCommand => _analyseCommand;
    public ICommand ToggleDetailsCommand => _toggleDetailsCommand;

    /// <summary>The sheet is on screen and the rest of the app is locked behind it.</summary>
    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value, nameof(IsOpen), nameof(IsChipVisible)); }

    /// <summary>The user chose "Run in background": the top-bar chip is shown instead of the sheet.</summary>
    public bool IsInBackground { get => _isInBackground; private set => Set(ref _isInBackground, value, nameof(IsInBackground), nameof(IsChipVisible)); }

    public bool IsChipVisible => IsInBackground && !IsOpen;

    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value, nameof(IsRunning), nameof(IsImportRunning), nameof(ChipText), nameof(ChipIsRunning)); }
    public bool IsImportRunning => IsRunning;
    public bool IsCompleted { get => _isCompleted; private set => Set(ref _isCompleted, value, nameof(IsCompleted), nameof(ChipText), nameof(IsCleanFinish), nameof(IsInterruptedFinish), nameof(ShowAnalyse)); }
    public bool IsCancelling { get => _isCancelling; private set => Set(ref _isCancelling, value, nameof(IsCancelling), nameof(PhaseTitle)); }
    public bool ChipIsRunning => IsRunning;

    public string Title { get => _title; private set => Set(ref _title, value, nameof(Title)); }

    /// <summary>The folder (or folders) being imported, shown under the title.</summary>
    public string FolderLine { get => _folderLine; private set => Set(ref _folderLine, value, nameof(FolderLine)); }

    public LibraryImportPhase Phase
    {
        get => _phase;
        private set => Set(ref _phase, value, nameof(Phase), nameof(PhaseTitle), nameof(IsIndeterminate), nameof(IsScanningPhase), nameof(IsDonePhase), nameof(IsProgressPhase));
    }

    public bool IsScanningPhase => Phase == LibraryImportPhase.Scanning && IsRunning;
    public bool IsProgressPhase => Phase is LibraryImportPhase.Reading or LibraryImportPhase.Adding;
    public bool IsDonePhase => Phase == LibraryImportPhase.Done;

    /// <summary>Scanning has no total yet, so its meter is indeterminate.</summary>
    public bool IsIndeterminate => Phase == LibraryImportPhase.Scanning;

    public string PhaseTitle => IsCancelling && IsRunning
        ? "Cancelling — keeping what's been checked"
        : Phase switch
        {
            LibraryImportPhase.Scanning => "Scanning folders",
            LibraryImportPhase.Reading => "Reading tags & checking files",
            LibraryImportPhase.Adding => "Adding to library",
            _ => SummaryTitle,
        };

    public int FilesFound { get => _filesFound; private set => Set(ref _filesFound, value, nameof(FilesFound), nameof(ChipText)); }
    public int Processed { get => _processed; private set => Set(ref _processed, value, nameof(Processed), nameof(ProgressValue), nameof(ChipText), nameof(ChipProgressValue)); }
    public int Total { get => _total; private set => Set(ref _total, value, nameof(Total), nameof(ProgressValue), nameof(ChipText), nameof(ChipProgressValue)); }
    public int Added { get => _added; private set => Set(ref _added, value, nameof(Added), nameof(AnalyseText), nameof(CanAnalyse), nameof(ChipText)); }
    public int AlreadyInLibrary { get => _alreadyInLibrary; private set => Set(ref _alreadyInLibrary, value, nameof(AlreadyInLibrary)); }
    public int Duplicates { get => _duplicates; private set => Set(ref _duplicates, value, nameof(Duplicates)); }
    public int Upgraded { get => _upgraded; private set => Set(ref _upgraded, value, nameof(Upgraded), nameof(HasUpgraded)); }
    public int Unsupported { get => _unsupported; private set => Set(ref _unsupported, value, nameof(Unsupported)); }
    public int Unreadable { get => _unreadable; private set => Set(ref _unreadable, value, nameof(Unreadable)); }
    public int Failed { get => _failed; private set => Set(ref _failed, value, nameof(Failed), nameof(HasFailed)); }
    public bool HasUpgraded => Upgraded > 0;
    public bool HasFailed => Failed > 0;

    public string CurrentFile { get => _currentFile; private set => Set(ref _currentFile, value, nameof(CurrentFile)); }

    /// <summary>0–100 for the design-system meter.</summary>
    public double ProgressValue => Total <= 0 ? 0 : Math.Clamp(100.0 * Processed / Total, 0, 100);

    /// <summary>The chip's meter: scanning counts as the first 10%, then reading/adding fill the rest.</summary>
    public double ChipProgressValue => Phase switch
    {
        LibraryImportPhase.Scanning => 2,
        LibraryImportPhase.Reading => 5 + ProgressValue * 0.75,
        LibraryImportPhase.Adding => 80 + ProgressValue * 0.2,
        _ => 100,
    };

    public string ChipText => IsRunning
        ? Phase == LibraryImportPhase.Scanning
            ? $"Importing · {SLSKDONET.Utils.CountFormat.N(FilesFound)} found"
            : $"Importing · {SLSKDONET.Utils.CountFormat.N(Processed)} / {SLSKDONET.Utils.CountFormat.N(Total)}"
        : IsCompleted
            ? $"Import done · {SLSKDONET.Utils.CountFormat.N(Added)} added"
            : string.Empty;

    public bool IsDetailsVisible { get => _isDetailsVisible; private set => Set(ref _isDetailsVisible, value, nameof(IsDetailsVisible), nameof(DetailsToggleText)); }
    public string DetailsToggleText => IsDetailsVisible ? "Hide details" : "Show details";
    public bool HasIssues => Issues.Count > 0;
    public int HiddenIssueCount => Math.Max(0, (_result?.Issues.Count ?? 0) - Issues.Count);
    public bool HasHiddenIssues => HiddenIssueCount > 0;

    /// <summary>Finished without cancel or error (success icon); otherwise the summary shows a warning icon.</summary>
    public bool IsCleanFinish => IsCompleted && _result is { Error: null, WasCancelled: false };
    public bool IsInterruptedFinish => IsCompleted && !IsCleanFinish;

    public string SummaryTitle => _result switch
    {
        null => "Done",
        { Error: not null } => "Import stopped",
        { WasCancelled: true } => "Import cancelled",
        _ => "Import complete",
    };

    /// <summary>One sentence for the summary ("2,650 tracks added to your library.").</summary>
    public string SummaryLine
    {
        get
        {
            if (_result == null) return string.Empty;
            if (_result.Error != null) return $"Something went wrong: {_result.Error} Tracks added before the error are kept.";
            var added = _result.Added == 1 ? "1 track" : $"{SLSKDONET.Utils.CountFormat.N(_result.Added)} tracks";
            if (_result.WasCancelled) return $"Stopped early. {added} added and kept in your library.";
            if (_result.FolderPaths.Count == 0) return "There are no library folders to scan yet. Add a music folder first.";
            if (_result.FilesFound == 0 && _result.Unsupported == 0)
                return _result.FolderPaths.Count == 1 ? "No music files were found in this folder." : "No music files were found in these folders.";
            if (_result.Added == 0) return "Nothing new: everything here is already in your library.";
            return $"{added} added to your library.";
        }
    }

    public bool AnalysisQueued { get => _analysisQueued; private set => Set(ref _analysisQueued, value, nameof(AnalysisQueued), nameof(CanAnalyse), nameof(AnalyseText)); }
    public bool CanAnalyse => IsCompleted && Added > 0 && !AnalysisQueued;
    public bool ShowAnalyse => IsCompleted && Added > 0;
    public string AnalyseText => AnalysisQueued
        ? "Analysis queued"
        : Added == 1 ? "Analyse 1 new track" : $"Analyse {SLSKDONET.Utils.CountFormat.N(Added)} new tracks";

    // ───────────────────────────── ILibraryImportLauncher ─────────────────────────────

    public async Task<bool> PickFolderAndStartImportAsync(bool watch = false)
    {
        if (_picker == null) return false;
        string? path;
        try
        {
            path = await _picker.OpenFolderDialogAsync("Add a music folder");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Folder picker failed");
            Toast("Couldn't open the folder picker", ex.Message, NotificationType.Error);
            return false;
        }
        if (string.IsNullOrWhiteSpace(path)) return false;
        return await AddFolderAndStartImportAsync(path, watch);
    }

    public async Task<bool> AddFolderAndStartImportAsync(string path, bool? watch = null)
    {
        if (_scanner == null) return false;
        if (!Directory.Exists(path))
        {
            Toast("Folder not found", path, NotificationType.Warning);
            return false;
        }

        Guid folderId;
        try
        {
            (folderId, _) = await Task.Run(() => _scanner.RegisterFolderAsync(path, watch));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add library folder {Path}", path);
            Toast("Couldn't add the folder", ex.Message, NotificationType.Error);
            return false;
        }

        _eventBus.Publish(new LibraryFoldersChangedEvent());
        return await StartImportAsync(new[] { folderId }, $"Importing {DisplayName(path)}");
    }

    public Task<bool> StartImportAsync(IReadOnlyList<Guid>? folderIds = null, string? title = null)
    {
        if (IsRunning)
        {
            Queue(folderIds);
            Toast("Import queued", "This folder is imported as soon as the current import finishes.", NotificationType.Information);
            if (!IsOpen && !IsInBackground) IsOpen = true;
            return Task.FromResult(false);
        }

        _ = RunImportAsync(folderIds, title);
        return Task.FromResult(true);
    }

    public async Task<LibraryImportResult?> RunImportAsync(IReadOnlyList<Guid>? folderIds = null, string? title = null)
    {
        if (_disposed) return null;
        if (IsRunning)
        {
            Toast("An import is already running", "Wait for it to finish, or cancel it first.", NotificationType.Information);
            if (!IsOpen && !IsInBackground) IsOpen = true;
            return null;
        }

        BeginRun(title ?? (folderIds == null ? "Scanning your library folders" : "Importing music"));
        var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new PostingProgress(this);
        var request = new LibraryImportRequest
        {
            FolderIds = folderIds,
            Trigger = LibraryImportTrigger.Manual,
            QueueAnalysis = false, // offered on the summary instead ("Analyse N new tracks")
        };

        LibraryImportResult result;
        try
        {
            result = await Task.Run(() => _runner(request, progress, cts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library import failed");
            result = new LibraryImportResult { Trigger = LibraryImportTrigger.Manual, Error = ex.Message };
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_cts, cts)) _cts = null;
        }

        var done = new TaskCompletionSource<LibraryImportResult>();
        _post(() =>
        {
            Complete(result);
            done.TrySetResult(result);
        });
        return await done.Task;
    }

    // ───────────────────────────── state ─────────────────────────────

    private void BeginRun(string title)
    {
        _result = null;
        Issues.Clear();
        Raise(nameof(HasIssues), nameof(HiddenIssueCount), nameof(HasHiddenIssues), nameof(SummaryTitle), nameof(SummaryLine));
        Title = title;
        FolderLine = string.Empty;
        FilesFound = Processed = Total = Added = AlreadyInLibrary = Duplicates = Upgraded = Unsupported = Unreadable = Failed = 0;
        CurrentFile = string.Empty;
        IsDetailsVisible = false;
        AnalysisQueued = false;
        IsCancelling = false;
        IsCompleted = false;
        IsRunning = true;
        Phase = LibraryImportPhase.Scanning;
        UpdateSteps();
        // Keep a background chip in the background; everything else opens the locking sheet.
        if (!IsInBackground) IsOpen = true;
        RaiseCommandStates();
        Raise(nameof(ShowAnalyse), nameof(ChipText), nameof(ChipProgressValue), nameof(IsScanningPhase));
    }

    internal void ApplyProgress(LibraryImportProgress p)
    {
        if (!IsRunning) return;
        Phase = p.Phase == LibraryImportPhase.Done ? Phase : p.Phase;
        FilesFound = p.FilesFound;
        Processed = p.Processed;
        Total = p.Total;
        Added = p.Added;
        AlreadyInLibrary = p.AlreadyInLibrary;
        Duplicates = p.Duplicates;
        Upgraded = p.Upgraded;
        Unsupported = p.Unsupported;
        Unreadable = p.Unreadable;
        Failed = p.Failed;
        if (!string.IsNullOrEmpty(p.CurrentFile)) CurrentFile = p.CurrentFile;
        UpdateSteps();
        Raise(nameof(ChipProgressValue), nameof(IsScanningPhase));
    }

    private void Complete(LibraryImportResult result)
    {
        _result = result;
        FilesFound = result.FilesFound;
        Added = result.Added;
        AlreadyInLibrary = result.AlreadyInLibrary;
        Duplicates = result.Duplicates;
        Upgraded = result.Upgraded;
        Unsupported = result.Unsupported;
        Unreadable = result.Unreadable;
        Failed = result.Failed;
        Processed = Total;
        CurrentFile = string.Empty;
        FolderLine = string.Join("  ·  ", result.FolderPaths);

        Issues.Clear();
        foreach (var issue in result.Issues.Take(MaxIssuesShown)) Issues.Add(issue);

        IsRunning = false;
        IsCancelling = false;
        IsCompleted = true;
        Phase = LibraryImportPhase.Done;
        UpdateSteps();
        RaiseCommandStates();
        Raise(nameof(HasIssues), nameof(HiddenIssueCount), nameof(HasHiddenIssues), nameof(SummaryTitle), nameof(SummaryLine),
              nameof(ShowAnalyse), nameof(CanAnalyse), nameof(AnalyseText), nameof(PhaseTitle), nameof(ChipText), nameof(ChipProgressValue), nameof(IsScanningPhase),
              nameof(IsCleanFinish), nameof(IsInterruptedFinish));

        if (IsInBackground)
        {
            Toast(SummaryTitle, SummaryLine + " Click the import chip in the top bar for the summary.",
                result.Error != null ? NotificationType.Error : NotificationType.Success);
        }

        if (_queuedAll || _queuedFolderIds.Count > 0)
        {
            var next = _queuedAll ? null : _queuedFolderIds.ToList();
            _queuedAll = false;
            _queuedFolderIds.Clear();
            if (!IsInBackground)
                Toast(SummaryTitle, SummaryLine, result.Error != null ? NotificationType.Error : NotificationType.Success);
            _ = RunImportAsync(next, next == null ? "Scanning your library folders" : "Importing music");
        }
    }

    private void UpdateSteps()
    {
        var current = IsCompleted ? 4 : (int)Phase + 1;
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            step.State = step.Number < current || (IsCompleted && step.Number == 4)
                ? ImportStepState.Done
                : step.Number == current ? ImportStepState.Active : ImportStepState.Pending;
        }

        Steps[0].Detail = FilesFound == 1 ? "1 file" : $"{SLSKDONET.Utils.CountFormat.N(FilesFound)} files";
        Steps[1].Detail = Phase == LibraryImportPhase.Reading && IsRunning
            ? $"{SLSKDONET.Utils.CountFormat.N(Processed)} / {SLSKDONET.Utils.CountFormat.N(Total)}"
            : Phase > LibraryImportPhase.Reading || IsCompleted ? (Unreadable > 0 ? $"{SLSKDONET.Utils.CountFormat.N(Unreadable)} unreadable" : "checked") : string.Empty;
        Steps[2].Detail = Phase >= LibraryImportPhase.Adding || IsCompleted ? $"{SLSKDONET.Utils.CountFormat.N(Added)} new" : string.Empty;
        Steps[3].Detail = string.Empty;
    }

    private void Queue(IReadOnlyList<Guid>? folderIds)
    {
        if (folderIds == null) _queuedAll = true;
        else foreach (var id in folderIds) _queuedFolderIds.Add(id);
    }

    // ───────────────────────────── commands ─────────────────────────────

    private void Cancel()
    {
        if (!IsRunning || _cts == null) return;
        IsCancelling = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // intentional: the import finished between the click and the cancel
        }
        RaiseCommandStates();
    }

    private void RunInBackground()
    {
        if (!IsRunning) return;
        IsInBackground = true;
        IsOpen = false;
        RaiseCommandStates();
    }

    private void ShowSheet()
    {
        IsInBackground = false;
        IsOpen = true;
        RaiseCommandStates();
    }

    private void Close()
    {
        if (!IsCompleted) return;
        IsOpen = false;
        IsInBackground = false;
        IsCompleted = false;
        RaiseCommandStates();
    }

    private void OpenInLibrary()
    {
        _eventBus.Publish(new OpenPlaylistInLibraryEvent(Guid.Empty));
        Close();
    }

    private void QueueAnalysis()
    {
        var hashes = _result?.AddedTrackHashes.ToList() ?? new List<string>();
        if (hashes.Count == 0) return;
        AnalysisQueued = true;
        RaiseCommandStates();
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var hash in hashes)
                    _eventBus.Publish(new TrackAnalysisRequestedEvent(hash));
                _logger.LogInformation("Queued analysis for {Count} newly imported tracks", hashes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to queue analysis for imported tracks");
                Toast("Couldn't queue analysis", ex.Message, NotificationType.Error);
            }
        });
        Toast("Analysis queued", $"{SLSKDONET.Utils.CountFormat.N(hashes.Count)} new tracks are queued. Follow progress on the Analysis page.", NotificationType.Information);
    }

    private void RaiseCommandStates()
    {
        _cancelCommand.RaiseCanExecuteChanged();
        _runInBackgroundCommand.RaiseCanExecuteChanged();
        _showSheetCommand.RaiseCanExecuteChanged();
        _closeCommand.RaiseCanExecuteChanged();
        _openInLibraryCommand.RaiseCanExecuteChanged();
        _analyseCommand.RaiseCanExecuteChanged();
        _toggleDetailsCommand.RaiseCanExecuteChanged();
    }

    private void Toast(string title, string message, NotificationType type) =>
        _eventBus.Publish(new ToastRequestedEvent(title, message, type, TimeSpan.FromSeconds(6)));

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(names);
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // intentional: already finished
        }
    }

    /// <summary>Marshals the (already throttled) pipeline reports onto the UI thread.</summary>
    private sealed class PostingProgress : IProgress<LibraryImportProgress>
    {
        private readonly LibraryImportViewModel _owner;
        public PostingProgress(LibraryImportViewModel owner) => _owner = owner;
        public void Report(LibraryImportProgress value) => _owner._post(() => _owner.ApplyProgress(value));
    }
}
