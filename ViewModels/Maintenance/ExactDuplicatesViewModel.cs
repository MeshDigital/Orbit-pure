using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Maintenance;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Maintenance;

/// <summary>
/// Exact-duplicate review (issue #238): the same audio stored as several library entries, found by
/// the tag-independent content hash. Starts the hash backfill on request (never at startup), lists
/// each group with a suggested keeper the DJ can change, and merges the selected groups. Removing
/// entries and moving files to the Recycle Bin are two separate choices; the second is opt-in and
/// asks for confirmation.
/// </summary>
public sealed class ExactDuplicatesViewModel : ReactiveObject, IDisposable
{
    private readonly LibraryContentHashBackfillService _backfill;
    private readonly ExactDuplicateResolver _resolver;
    private readonly ILogger<ExactDuplicatesViewModel> _logger;
    private readonly INotificationService? _notifications;
    private readonly Action<Action> _post;
    private readonly IDisposable? _progressSubscription;
    private readonly CancellationTokenSource _cts = new();
    private readonly RelayCommand _startHashingCommand;
    private readonly RelayCommand _cancelHashingCommand;
    private readonly AsyncRelayCommand _mergeCommand;
    private readonly AsyncRelayCommand _confirmRecycleMergeCommand;
    private bool _disposed;

    public ExactDuplicatesViewModel(
        LibraryContentHashBackfillService backfill,
        ExactDuplicateResolver resolver,
        IEventBus eventBus,
        ILogger<ExactDuplicatesViewModel> logger,
        INotificationService? notifications = null,
        Action<Action>? postToUi = null)
    {
        _backfill = backfill;
        _resolver = resolver;
        _logger = logger;
        _notifications = notifications;
        _post = postToUi ?? (a => Dispatcher.UIThread.Post(a));

        _startHashingCommand = new RelayCommand(StartHashing, () => !IsHashing && !IsMerging);
        _cancelHashingCommand = new RelayCommand(() => _backfill.Cancel(), () => IsHashing);
        _mergeCommand = new AsyncRelayCommand(MergeAsync, () => CanMerge);
        _confirmRecycleMergeCommand = new AsyncRelayCommand(() => ExecuteMergeAsync(moveFilesToRecycleBin: true), () => CanMerge);
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading && !IsMerging);
        CancelConfirmCommand = new RelayCommand(() => IsConfirmingRecycle = false);
        CloseCommand = new RelayCommand(Close);

        try
        {
            _progressSubscription = eventBus.GetEvent<ContentHashBackfillProgressEvent>()
                .Subscribe(e => _post(() => OnBackfillProgress(e)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exact duplicates: could not subscribe to hashing progress");
        }

        if (_backfill.LastProgress is { IsRunning: true } running) OnBackfillProgress(running);
    }

    public event EventHandler? Closed;

    public ObservableCollection<ExactDuplicateGroupViewModel> Groups { get; } = new();

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand StartHashingCommand => _startHashingCommand;
    public ICommand CancelHashingCommand => _cancelHashingCommand;
    public ICommand MergeCommand => _mergeCommand;
    public ICommand ConfirmRecycleMergeCommand => _confirmRecycleMergeCommand;
    public ICommand RefreshCommand { get; }
    public ICommand CancelConfirmCommand { get; }
    public ICommand CloseCommand { get; }

    // ── Loading / error ──────────────────────────────────────────────────────
    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set { this.RaiseAndSetIfChanged(ref _isLoading, value); RaiseState(); } }

    private string? _errorText;
    public string? ErrorText { get => _errorText; private set { this.RaiseAndSetIfChanged(ref _errorText, value); RaiseState(); } }
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    // ── Hashing (backfill) ───────────────────────────────────────────────────
    private int _unhashedCount;
    public int UnhashedCount { get => _unhashedCount; private set { this.RaiseAndSetIfChanged(ref _unhashedCount, value); RaiseState(); } }
    public bool HasUnhashed => UnhashedCount > 0 && !IsHashing;

    /// <summary>"1,204 tracks haven't been checked yet."</summary>
    public string UnhashedText => UnhashedCount == 1
        ? "1 track hasn't been checked yet."
        : $"{CountFormat.N(UnhashedCount)} tracks haven't been checked yet.";

    private bool _isHashing;
    public bool IsHashing { get => _isHashing; private set { this.RaiseAndSetIfChanged(ref _isHashing, value); RaiseState(); } }

    private double _hashProgress;
    /// <summary>0–100.</summary>
    public double HashProgress { get => _hashProgress; private set => this.RaiseAndSetIfChanged(ref _hashProgress, value); }

    private bool _isHashProgressIndeterminate = true;
    public bool IsHashProgressIndeterminate { get => _isHashProgressIndeterminate; private set => this.RaiseAndSetIfChanged(ref _isHashProgressIndeterminate, value); }

    private string _hashCountsText = string.Empty;
    /// <summary>"412 / 3,267" (mono in the view).</summary>
    public string HashCountsText { get => _hashCountsText; private set => this.RaiseAndSetIfChanged(ref _hashCountsText, value); }

    private string? _hashNote;
    /// <summary>The last run's outcome: cancelled, failed, or files that couldn't be read.</summary>
    public string? HashNote { get => _hashNote; private set { this.RaiseAndSetIfChanged(ref _hashNote, value); this.RaisePropertyChanged(nameof(HasHashNote)); } }
    public bool HasHashNote => !string.IsNullOrEmpty(HashNote);

    // ── Groups ───────────────────────────────────────────────────────────────
    public bool HasGroups => Groups.Count > 0;
    public bool IsEmpty => !IsLoading && !HasError && Groups.Count == 0;

    public string EmptyTitle => "No exact duplicates found";

    public string EmptyText => UnhashedCount > 0
        ? IsHashing
            ? "Checking your library. Groups appear here as soon as the check finishes."
            : "Run the check to compare every track's audio. Tracks that haven't been checked can't be matched yet."
        : "Every track in your library has its own audio. Re-tagged or renamed copies of the same file would show up here.";

    /// <summary>"3 of 5 groups selected · 4 entries will be removed".</summary>
    public string SelectionText
    {
        get
        {
            var included = Groups.Where(g => g.IsIncluded).ToList();
            var removed = included.Sum(g => g.Entries.Count - 1);
            return $"{CountFormat.N(included.Count)} of {CountFormat.N(Groups.Count)} group{(Groups.Count == 1 ? "" : "s")} selected · " +
                   $"{CountFormat.N(removed)} entr{(removed == 1 ? "y" : "ies")} will be removed";
        }
    }

    // ── Resolve ──────────────────────────────────────────────────────────────
    public bool CanRecycle => _resolver.CanRecycle;

    private bool _moveFilesToRecycleBin;
    /// <summary>Opt-in: also move the removed copies' files to the Recycle Bin.</summary>
    public bool MoveFilesToRecycleBin
    {
        get => _moveFilesToRecycleBin;
        set
        {
            this.RaiseAndSetIfChanged(ref _moveFilesToRecycleBin, value);
            if (!value) IsConfirmingRecycle = false;
        }
    }

    private bool _isConfirmingRecycle;
    public bool IsConfirmingRecycle
    {
        get => _isConfirmingRecycle;
        private set { this.RaiseAndSetIfChanged(ref _isConfirmingRecycle, value); this.RaisePropertyChanged(nameof(ConfirmRecycleText)); }
    }

    public string ConfirmRecycleText
    {
        get
        {
            var files = Groups.Where(g => g.IsIncluded).Sum(g => g.Entries.Count(e => !e.IsKeeper && e.FileExists));
            return $"Move {CountFormat.N(files)} duplicate file{(files == 1 ? "" : "s")} to the Recycle Bin? " +
                   "The kept copy's file is never touched, and you can restore files from the Recycle Bin.";
        }
    }

    private bool _isMerging;
    public bool IsMerging { get => _isMerging; private set { this.RaiseAndSetIfChanged(ref _isMerging, value); RaiseState(); } }

    private string? _resultText;
    public string? ResultText { get => _resultText; private set { this.RaiseAndSetIfChanged(ref _resultText, value); this.RaisePropertyChanged(nameof(HasResult)); } }
    public bool HasResult => !string.IsNullOrEmpty(ResultText);

    private bool _resultHasErrors;
    public bool ResultHasErrors { get => _resultHasErrors; private set => this.RaiseAndSetIfChanged(ref _resultHasErrors, value); }

    private bool CanMerge => !IsMerging && !IsHashing && !IsLoading && Groups.Any(g => g.IsIncluded);

    // ── Behaviour ────────────────────────────────────────────────────────────

    /// <summary>Counts unchecked tracks and loads the duplicate groups.</summary>
    public async Task LoadAsync()
    {
        if (_disposed) return;
        IsLoading = true;
        ErrorText = null;
        try
        {
            // Off the UI thread: SQLite's async calls complete synchronously, and the group load
            // reads every hashed entry plus a File.Exists per copy.
            var ct = _cts.Token;
            UnhashedCount = await Task.Run(() => _backfill.CountUnhashedAsync(ct), ct);
            var groups = await Task.Run(() => _backfill.LoadGroupDetailsAsync(ct), ct);

            foreach (var g in Groups) g.SelectionChanged -= OnGroupChanged;
            Groups.Clear();
            foreach (var details in groups)
            {
                var vm = new ExactDuplicateGroupViewModel(details);
                vm.SelectionChanged += OnGroupChanged;
                Groups.Add(vm);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // intentional: the review was closed, which cancels in-flight work
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exact duplicates: loading groups failed");
            ErrorText = $"Couldn't load duplicates: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void StartHashing()
    {
        HashNote = null;
        if (!_backfill.TryStart())
            HashNote = "A check is already running.";
    }

    private void OnBackfillProgress(ContentHashBackfillProgressEvent e)
    {
        if (_disposed) return;
        var wasHashing = IsHashing;
        IsHashing = e.IsRunning;
        IsHashProgressIndeterminate = e.Total == 0;
        HashProgress = e.Total > 0 ? Math.Clamp(100.0 * e.Done / e.Total, 0, 100) : 0;
        HashCountsText = e.Total > 0 ? $"{CountFormat.N(e.Done)} / {CountFormat.N(e.Total)}" : string.Empty;

        if (e.IsRunning) return;

        var notes = new List<string>();
        if (e.Error != null) notes.Add($"The check stopped with an error: {e.Error}");
        else if (e.WasCancelled) notes.Add($"Check cancelled after {CountFormat.N(e.Done)} tracks. Run it again to continue where it stopped.");
        if (e.Unreadable > 0) notes.Add($"{CountFormat.N(e.Unreadable)} file{(e.Unreadable == 1 ? "" : "s")} couldn't be read.");
        if (e.Missing > 0) notes.Add($"{CountFormat.N(e.Missing)} file{(e.Missing == 1 ? " is" : "s are")} missing on disk.");
        HashNote = notes.Count > 0 ? string.Join(" ", notes) : null;

        if (wasHashing) _ = LoadAsync();
    }

    private async Task MergeAsync()
    {
        if (MoveFilesToRecycleBin && CanRecycle)
        {
            IsConfirmingRecycle = true; // second step: ConfirmRecycleMergeCommand
            return;
        }
        await ExecuteMergeAsync(moveFilesToRecycleBin: false);
    }

    /// <summary>Merges every included group into its keeper. Files move only when asked and confirmed.</summary>
    public async Task ExecuteMergeAsync(bool moveFilesToRecycleBin)
    {
        var plans = Groups.Where(g => g.IsIncluded).Select(g => g.ToPlan()).ToList();
        if (plans.Count == 0) return;

        IsConfirmingRecycle = false;
        IsMerging = true;
        ResultText = null;
        try
        {
            var recycle = moveFilesToRecycleBin && MoveFilesToRecycleBin && CanRecycle;
            var resolution = await _resolver.ResolveAsync(plans, recycle, _cts.Token);
            ResultText = resolution.Summary(recycle);
            ResultHasErrors = resolution.Merge.Aborted || resolution.ErrorCount > 0;

            var detail = resolution.Merge.Errors.Concat(resolution.RecycleErrors).Take(3).ToList();
            _notifications?.Show(
                "Exact duplicates",
                detail.Count > 0 ? $"{ResultText}. {string.Join("; ", detail)}" : ResultText,
                resolution.Merge.Aborted ? NotificationType.Error
                    : ResultHasErrors ? NotificationType.Warning
                    : NotificationType.Success);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // intentional: the review was closed, which cancels in-flight work
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exact duplicates: merge failed");
            ResultText = $"Merge failed: {ex.Message}";
            ResultHasErrors = true;
            _notifications?.Show("Exact duplicates", ResultText, NotificationType.Error);
        }
        finally
        {
            IsMerging = false;
        }

        await LoadAsync();
    }

    private void OnGroupChanged(object? sender, EventArgs e)
    {
        this.RaisePropertyChanged(nameof(SelectionText));
        this.RaisePropertyChanged(nameof(ConfirmRecycleText));
        RaiseCommands();
    }

    private void RaiseState()
    {
        this.RaisePropertyChanged(nameof(HasError));
        this.RaisePropertyChanged(nameof(HasGroups));
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(EmptyText));
        this.RaisePropertyChanged(nameof(HasUnhashed));
        this.RaisePropertyChanged(nameof(UnhashedText));
        this.RaisePropertyChanged(nameof(SelectionText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        _startHashingCommand?.RaiseCanExecuteChanged();
        _cancelHashingCommand?.RaiseCanExecuteChanged();
        _mergeCommand?.RaiseCanExecuteChanged();
        _confirmRecycleMergeCommand?.RaiseCanExecuteChanged();
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Closes the review; the hashing keeps running in the background (activity chip).</summary>
    public void Close() => Closed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _progressSubscription?.Dispose();
        foreach (var g in Groups) g.SelectionChanged -= OnGroupChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>One exact-duplicate group: the copies of one recording and which one is kept.</summary>
public sealed class ExactDuplicateGroupViewModel : ReactiveObject
{
    public ExactDuplicateGroupViewModel(ExactDuplicateGroupDetails details)
    {
        AudioContentHash = details.AudioContentHash;
        SuggestedKeeperHash = details.SuggestedKeeperHash;
        _keeperHash = details.SuggestedKeeperHash; // before Entries: IsKeeper reads it
        Entries = details.Entries
            .Select(e => new ExactDuplicateEntryViewModel(this, e, e.UniqueHash == details.SuggestedKeeperHash))
            .OrderByDescending(e => e.IsKeeper)
            .ToList();
    }

    internal event EventHandler? SelectionChanged;

    public string AudioContentHash { get; }
    public string SuggestedKeeperHash { get; }
    public IReadOnlyList<ExactDuplicateEntryViewModel> Entries { get; }

    private string _keeperHash;
    public string KeeperHash => _keeperHash;

    private bool _isIncluded = true;
    public bool IsIncluded
    {
        get => _isIncluded;
        set { this.RaiseAndSetIfChanged(ref _isIncluded, value); SelectionChanged?.Invoke(this, EventArgs.Empty); }
    }

    public ExactDuplicateEntryViewModel Keeper => Entries.First(e => e.IsKeeper);

    /// <summary>"Artist – Title" of the kept copy.</summary>
    public string Title => Keeper.DisplayName;

    /// <summary>"3 copies".</summary>
    public string CopiesText => $"{CountFormat.N(Entries.Count)} copies";

    internal void SetKeeper(ExactDuplicateEntryViewModel keeper)
    {
        if (keeper.UniqueHash == _keeperHash) return;
        _keeperHash = keeper.UniqueHash;
        foreach (var e in Entries) e.RaiseKeeperChanged();
        this.RaisePropertyChanged(nameof(KeeperHash));
        this.RaisePropertyChanged(nameof(Keeper));
        this.RaisePropertyChanged(nameof(Title));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public ExactDuplicateMergePlan ToPlan() =>
        new(_keeperHash, Entries.Where(e => !e.IsKeeper).Select(e => e.UniqueHash).ToList());
}

/// <summary>One copy inside a duplicate group.</summary>
public sealed class ExactDuplicateEntryViewModel : ReactiveObject
{
    private readonly ExactDuplicateGroupViewModel _group;
    private readonly ExactDuplicateEntry _entry;

    public ExactDuplicateEntryViewModel(ExactDuplicateGroupViewModel group, ExactDuplicateEntry entry, bool isSuggested)
    {
        _group = group;
        _entry = entry;
        IsSuggested = isSuggested;
        KeepCommand = new RelayCommand(() => _group.SetKeeper(this));
    }

    public string UniqueHash => _entry.UniqueHash;
    public bool IsKeeper => _group.KeeperHash == _entry.UniqueHash;
    public bool IsNotKeeper => !IsKeeper;
    public bool IsSuggested { get; }
    public bool FileExists => _entry.FileExists;
    public bool IsMissing => !_entry.FileExists;

    public ICommand KeepCommand { get; }

    public string DisplayName
    {
        get
        {
            var artist = string.IsNullOrWhiteSpace(_entry.Artist) ? "Unknown artist" : _entry.Artist;
            var title = string.IsNullOrWhiteSpace(_entry.Title) ? Path.GetFileNameWithoutExtension(_entry.FilePath) : _entry.Title;
            return $"{artist} – {title}";
        }
    }

    /// <summary>Tags as stored on this copy: "Artist – Title · Album".</summary>
    public string TagsText => string.IsNullOrWhiteSpace(_entry.Album) ? DisplayName : $"{DisplayName} · {_entry.Album}";

    public string FilePath => _entry.FilePath;

    /// <summary>"Lossless" / "High" / "Low" / "Unknown".</summary>
    public string QualityText => ExactDuplicateKeeper.QualityLabel(_entry.Format, _entry.Bitrate);

    /// <summary>"FLAC" or "MP3 320".</summary>
    public string FormatText
    {
        get
        {
            var format = string.IsNullOrWhiteSpace(_entry.Format) ? Path.GetExtension(_entry.FilePath).TrimStart('.') : _entry.Format;
            format = format.ToUpperInvariant();
            return ExactDuplicateKeeper.QualityRank(_entry.Format, _entry.Bitrate) < 3 && _entry.Bitrate > 0
                ? $"{format} {_entry.Bitrate.ToString(CultureInfo.InvariantCulture)}"
                : format;
        }
    }

    public string CuesText => CountFormat.N(_entry.CueCount);
    public string PlaylistsText => CountFormat.N(_entry.PlaylistCount);
    public string AnalysedText => _entry.IsAnalyzed ? "Analysed" : "Not analysed";
    public string AddedText => _entry.AddedAt == default ? "—" : _entry.AddedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal void RaiseKeeperChanged()
    {
        this.RaisePropertyChanged(nameof(IsKeeper));
        this.RaisePropertyChanged(nameof(IsNotKeeper));
    }
}
