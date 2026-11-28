using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Intake;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Intake;

/// <summary>One choice in a row's action menu.</summary>
public sealed record IntakeActionOption(IntakeAction Action, string Label)
{
    public static string LabelFor(IntakeAction action) => action switch
    {
        IntakeAction.Keep => "Add to library",
        IntakeAction.Replace => "Replace my copy",
        IntakeAction.Upgrade => "Find a better copy",
        IntakeAction.Skip => "Leave on drive",
        _ => "—",
    };
}

/// <summary>One lane in the filter strip.</summary>
public sealed class IntakeLaneFilter : ReactiveObject
{
    private int _count;
    private bool _isSelected;

    public IntakeLaneFilter(IntakeLane? lane, string label, bool failed = false)
    {
        Lane = lane;
        Label = label;
        IsFailedFilter = failed;
    }

    public IntakeLane? Lane { get; }
    public bool IsFailedFilter { get; }

    /// <summary>Shows only this lane (set by the page's view model).</summary>
    public System.Windows.Input.ICommand? SelectCommand { get; internal set; }
    public string Label { get; }
    public int Count { get => _count; set { this.RaiseAndSetIfChanged(ref _count, value); this.RaisePropertyChanged(nameof(Text)); } }
    public string Text => $"{Label} {Count.ToString("N0", CultureInfo.CurrentCulture)}";
    public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }
}

/// <summary>One file in the review list.</summary>
public sealed class IntakeRowViewModel : ReactiveObject
{
    private readonly Func<IntakeRowViewModel, IntakeAction, Task> _setAction;
    private IntakeActionOption? _selectedAction;
    private bool _suppress;

    public IntakeRowViewModel(IntakeItemEntity item, Func<IntakeRowViewModel, IntakeAction, Task> setAction)
    {
        _setAction = setAction;
        Item = item;
        ActionOptions = IntakeApplyService.AllowedActions(item)
            .Select(a => new IntakeActionOption(a, IntakeActionOption.LabelFor(a)))
            .ToList();
        _selectedAction = ActionOptions.FirstOrDefault(o => o.Action == item.Action);
    }

    public IntakeItemEntity Item { get; }
    public string Id => Item.Id;
    public IntakeLane Lane => Item.Lane;

    public string TitleText => Item.Artist != null && Item.Title != null
        ? $"{Item.Artist} – {Item.Title}"
        : System.IO.Path.GetFileNameWithoutExtension(Item.SourcePath);

    public string PathText => Item.RelativePath;

    public string QualityText => IntakeClassifier.Format(Item.Extension).ToUpperInvariant()
        + (Item.BitrateKbps > 0 ? $" {Item.BitrateKbps}" : string.Empty);

    public string DurationText => Item.DurationSeconds > 0
        ? TimeSpan.FromSeconds(Item.DurationSeconds).ToString(Item.DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)
        : "–";

    public string ReasonText => Item.Status == IntakeItemStatus.Failed ? Item.Error ?? "Couldn't be checked" : Item.Reason ?? string.Empty;

    public string LaneText => Item.Status == IntakeItemStatus.Failed ? "Failed" : Item.Lane switch
    {
        IntakeLane.BetterCopy => "Better copy",
        IntakeLane.Pending => "Waiting",
        var l => l.ToString(),
    };

    public bool IsDecided => Item.Status is IntakeItemStatus.Applied or IntakeItemStatus.Skipped;
    public bool CanChoose => Item.Status == IntakeItemStatus.Scanned;
    public string DoneText => Item.Status switch
    {
        IntakeItemStatus.Applied => Item.AppliedPath != null ? "Done · " + Item.AppliedPath : "Done",
        IntakeItemStatus.Skipped => "Left on the drive",
        _ => string.Empty,
    };

    public IReadOnlyList<IntakeActionOption> ActionOptions { get; }

    public IntakeActionOption? SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (value == null || Equals(value, _selectedAction)) return;
            this.RaiseAndSetIfChanged(ref _selectedAction, value);
            if (!_suppress) _ = _setAction(this, value.Action);
        }
    }

    /// <summary>Shows an action the service saved, without saving it again.</summary>
    internal void ShowAction(IntakeAction action)
    {
        Item.Action = action;
        _suppress = true;
        try { SelectedAction = ActionOptions.FirstOrDefault(o => o.Action == action); }
        finally { _suppress = false; }
    }
}

/// <summary>
/// The Intake page (epic #632): pick a drive or folder, watch it being checked, review each file's
/// lane and pre-ticked action, change what you disagree with, and Apply. One session is open at a
/// time; an unfinished one (scan cancelled, ORBIT closed, some files failed) is offered again.
/// </summary>
public sealed class IntakeViewModel : ReactiveObject, IDisposable
{
    private readonly IntakeScanService _scan;
    private readonly IntakeApplyService _apply;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IDialogService _dialogs;
    private readonly INotificationService? _notifications;
    private readonly ILogger<IntakeViewModel> _logger;
    private readonly Action<Action> _post;
    private readonly AsyncRelayCommand _chooseSourceCommand;
    private readonly AsyncRelayCommand _resumeScanCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly AsyncRelayCommand _applyCommand;
    private readonly AsyncRelayCommand _closeSessionCommand;
    private CancellationTokenSource? _workCts;
    private List<IntakeRowViewModel> _allRows = new();
    private IntakeSessionEntity? _session;
    private bool _isBusy;
    private bool _isScanning;
    private bool _isApplying;
    private bool _isLoading;
    private double _progress;
    private string _progressText = string.Empty;
    private string _currentFile = string.Empty;
    private string? _errorText;
    private string? _resultText;
    private bool _deepDecodeCheck = true;
    private IReadOnlyList<IntakeRowViewModel> _rows = Array.Empty<IntakeRowViewModel>();
    private bool _loaded;
    private bool _disposed;

    public IntakeViewModel(
        IntakeScanService scan,
        IntakeApplyService apply,
        IDbContextFactory<AppDbContext> dbFactory,
        IDialogService dialogs,
        ILogger<IntakeViewModel> logger,
        INotificationService? notifications = null,
        Action<Action>? postToUi = null)
    {
        _scan = scan;
        _apply = apply;
        _dbFactory = dbFactory;
        _dialogs = dialogs;
        _logger = logger;
        _notifications = notifications;
        _post = postToUi ?? (a => Dispatcher.UIThread.Post(a));

        _chooseSourceCommand = new AsyncRelayCommand(ChooseSourceAsync, () => !IsBusy);
        _resumeScanCommand = new AsyncRelayCommand(() => RunScanAsync(_session!.Id), () => !IsBusy && HasUnfinishedScan);
        _cancelCommand = new RelayCommand(() => _workCts?.Cancel(), () => IsBusy);
        _applyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && IsReviewing && DecisionsCount > 0);
        _closeSessionCommand = new AsyncRelayCommand(CloseSessionAsync, () => !IsBusy && HasSession);

        LaneFilters = new ObservableCollection<IntakeLaneFilter>
        {
            new(null, "All"),
            new(IntakeLane.Good, "Good"),
            new(IntakeLane.BetterCopy, "Better copy"),
            new(IntakeLane.Duplicate, "Duplicate"),
            new(IntakeLane.Suspicious, "Suspicious"),
            new(IntakeLane.Reject, "Reject"),
            new(null, "Failed", failed: true),
        };
        LaneFilters[0].IsSelected = true;
        foreach (var f in LaneFilters)
        {
            var filter = f;
            filter.SelectCommand = new RelayCommand(() => SelectLane(filter));
        }
    }

    public AsyncRelayCommand ChooseSourceCommand => _chooseSourceCommand;
    public AsyncRelayCommand ResumeScanCommand => _resumeScanCommand;
    public RelayCommand CancelCommand => _cancelCommand;
    public AsyncRelayCommand ApplyCommand => _applyCommand;
    public AsyncRelayCommand CloseSessionCommand => _closeSessionCommand;

    public ObservableCollection<IntakeLaneFilter> LaneFilters { get; }

    public IReadOnlyList<IntakeRowViewModel> Rows { get => _rows; private set { this.RaiseAndSetIfChanged(ref _rows, value); this.RaisePropertyChanged(nameof(HasRows)); this.RaisePropertyChanged(nameof(IsFilterEmpty)); } }
    public bool HasRows => _rows.Count > 0;
    public bool IsFilterEmpty => IsReviewing && _allRows.Count > 0 && _rows.Count == 0;

    public bool DeepDecodeCheck { get => _deepDecodeCheck; set => this.RaiseAndSetIfChanged(ref _deepDecodeCheck, value); }

    public bool IsBusy { get => _isBusy; private set { this.RaiseAndSetIfChanged(ref _isBusy, value); RaiseAll(); } }
    public bool IsScanning { get => _isScanning; private set { this.RaiseAndSetIfChanged(ref _isScanning, value); RaiseAll(); } }
    public bool IsApplying { get => _isApplying; private set { this.RaiseAndSetIfChanged(ref _isApplying, value); RaiseAll(); } }
    public bool IsLoading { get => _isLoading; private set { this.RaiseAndSetIfChanged(ref _isLoading, value); RaiseAll(); } }
    public double Progress { get => _progress; private set => this.RaiseAndSetIfChanged(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => this.RaiseAndSetIfChanged(ref _progressText, value); }
    public string CurrentFile { get => _currentFile; private set => this.RaiseAndSetIfChanged(ref _currentFile, value); }
    public string? ErrorText { get => _errorText; private set { this.RaiseAndSetIfChanged(ref _errorText, value); this.RaisePropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_errorText);
    public string? ResultText { get => _resultText; private set { this.RaiseAndSetIfChanged(ref _resultText, value); this.RaisePropertyChanged(nameof(HasResult)); } }
    public bool HasResult => !string.IsNullOrEmpty(_resultText);

    public bool HasSession => _session != null;
    public bool HasUnfinishedScan => _session is { Status: IntakeSessionStatus.Scanning } && !IsBusy;
    public bool IsReviewing => _session is { Status: IntakeSessionStatus.Ready or IntakeSessionStatus.Applying or IntakeSessionStatus.Done } && !IsScanning && !IsLoading;
    public bool IsStart => _session == null && !IsBusy && !IsLoading;
    public string SessionTitle => _session == null ? "Intake" : $"Intake · {_session.Label}";
    public string SessionSubtitle => _session == null
        ? "Check music from a drive or folder before any of it joins your library."
        : $"{_session.SourceRoot} · {_allRows.Count.ToString("N0", CultureInfo.CurrentCulture)} audio files"
          + (_session.JunkCount > 0 ? $" · {_session.JunkCount.ToString("N0", CultureInfo.CurrentCulture)} other files skipped" : string.Empty);

    public int DecisionsCount => _allRows.Count(r => r.CanChoose);
    public string ApplyText => $"Apply {DecisionsCount.ToString("N0", CultureInfo.CurrentCulture)} decisions";
    public string DecisionsSummary
    {
        get
        {
            var open = _allRows.Where(r => r.CanChoose).ToList();
            int Count(IntakeAction a) => open.Count(r => r.Item.Action == a);
            var parts = new[]
            {
                (Count(IntakeAction.Keep), "to add"),
                (Count(IntakeAction.Replace), "to replace"),
                (Count(IntakeAction.Upgrade), "to search for"),
                (Count(IntakeAction.Skip), "to leave"),
            }.Where(p => p.Item1 > 0).Select(p => $"{p.Item1.ToString("N0", CultureInfo.CurrentCulture)} {p.Item2}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Called when the page is shown: reopens the newest unfinished session.</summary>
    public async Task LoadAsync()
    {
        if (_loaded || IsBusy) return;
        _loaded = true;
        IsLoading = true;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var open = await db.IntakeSessions.AsNoTracking()
                .Where(s => s.Status != IntakeSessionStatus.Done)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync();
            _post(() => _session = open);
            if (open != null) await ReloadRowsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intake: loading the open session failed");
            _post(() => ErrorText = "Couldn't load your Intake session: " + ex.Message);
        }
        finally
        {
            _post(() => { IsLoading = false; RaiseAll(); });
        }
    }

    private async Task ChooseSourceAsync()
    {
        ErrorText = null;
        ResultText = null;
        var folder = await _dialogs.OpenFolderDialogAsync("Choose the drive or folder to take in");
        if (string.IsNullOrWhiteSpace(folder)) return;

        IsBusy = true;
        IsScanning = true;
        ProgressText = "Listing files…";
        Progress = 0;
        try
        {
            var session = await Task.Run(() => _scan.CreateSessionAsync(folder, DeepDecodeCheck));
            _session = session;
            await ReloadRowsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intake: starting a session for {Folder} failed", folder);
            ErrorText = ex.Message;
            IsScanning = false;
            IsBusy = false;
            return;
        }
        IsBusy = false;
        await RunScanAsync(_session!.Id);
    }

    private async Task RunScanAsync(string sessionId)
    {
        ErrorText = null;
        _workCts = new CancellationTokenSource();
        IsBusy = true;
        IsScanning = true;
        var progress = new Progress<IntakeScanProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = $"Checked {p.Done.ToString("N0", CultureInfo.CurrentCulture)} of {p.Total.ToString("N0", CultureInfo.CurrentCulture)}"
                + (p.Failed > 0 ? $" · {p.Failed} couldn't be checked" : string.Empty);
            CurrentFile = p.CurrentFile ?? string.Empty;
        });
        try
        {
            await Task.Run(() => _scan.ScanAsync(sessionId, progress, _workCts.Token));
        }
        catch (OperationCanceledException)
        {
            _notifications?.Show("Intake paused", "Files already checked are kept. Resume any time.", NotificationType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intake: scan {Id} failed", sessionId);
            ErrorText = ex.Message;
        }
        finally
        {
            _workCts.Dispose();
            _workCts = null;
            await RefreshSessionAsync(sessionId);
            await ReloadRowsAsync();
            IsScanning = false;
            IsBusy = false;
        }
    }

    private async Task ApplyAsync()
    {
        if (_session == null) return;
        ErrorText = null;
        ResultText = null;
        _workCts = new CancellationTokenSource();
        IsBusy = true;
        IsApplying = true;
        var progress = new Progress<IntakeApplyProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = $"Applying {p.Done.ToString("N0", CultureInfo.CurrentCulture)} of {p.Total.ToString("N0", CultureInfo.CurrentCulture)}";
            CurrentFile = p.CurrentFile ?? string.Empty;
        });
        var sessionId = _session.Id;
        try
        {
            var result = await Task.Run(() => _apply.ApplyAsync(sessionId, progress, _workCts.Token));
            ResultText = result.Summary + ".";
            _notifications?.Show("Intake applied", result.Summary,
                result.Failed > 0 ? NotificationType.Warning : NotificationType.Success);
        }
        catch (OperationCanceledException)
        {
            ResultText = "Stopped. What was already applied stays; Apply again to carry on.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intake: apply {Id} failed", sessionId);
            ErrorText = ex.Message;
        }
        finally
        {
            _workCts.Dispose();
            _workCts = null;
            await RefreshSessionAsync(sessionId);
            await ReloadRowsAsync();
            IsApplying = false;
            IsBusy = false;
        }
    }

    /// <summary>Ends the session (files already applied stay); the page goes back to picking a drive.</summary>
    private async Task CloseSessionAsync()
    {
        if (_session == null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var s = await db.IntakeSessions.FirstOrDefaultAsync(x => x.Id == _session.Id);
            if (s != null)
            {
                s.Status = IntakeSessionStatus.Done;
                s.ClosedAt ??= DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            _session = null;
            _allRows = new List<IntakeRowViewModel>();
            Rows = Array.Empty<IntakeRowViewModel>();
            ResultText = null;
            ErrorText = null;
            RaiseAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intake: closing the session failed");
            ErrorText = "Couldn't close the session: " + ex.Message;
        }
    }

    private async Task SetActionAsync(IntakeRowViewModel row, IntakeAction action)
    {
        try
        {
            if (await _apply.SetActionAsync(new[] { row.Id }, action) > 0)
                row.Item.Action = action;
            else
                row.ShowAction(row.Item.Action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intake: saving the action for {Path} failed", row.Item.SourcePath);
            ErrorText = "Couldn't save that choice: " + ex.Message;
            row.ShowAction(row.Item.Action);
        }
        this.RaisePropertyChanged(nameof(DecisionsSummary));
    }

    private void SelectLane(IntakeLaneFilter filter)
    {
        foreach (var f in LaneFilters) f.IsSelected = ReferenceEquals(f, filter);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var f = LaneFilters.First(x => x.IsSelected);
        Rows = f.IsFailedFilter
            ? _allRows.Where(r => r.Item.Status == IntakeItemStatus.Failed).ToList()
            : f.Lane is { } lane
                ? _allRows.Where(r => r.Item.Status != IntakeItemStatus.Failed && r.Lane == lane).ToList()
                : _allRows;
    }

    private async Task RefreshSessionAsync(string sessionId)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            _session = await db.IntakeSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intake: refreshing session {Id} failed", sessionId);
        }
    }

    private async Task ReloadRowsAsync()
    {
        var session = _session;
        if (session == null) return;
        List<IntakeItemEntity> items;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            items = await db.IntakeItems.AsNoTracking()
                .Where(i => i.SessionId == session.Id)
                .OrderBy(i => i.RelativePath)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intake: loading files for session {Id} failed", session.Id);
            _post(() => ErrorText = "Couldn't load the files: " + ex.Message);
            return;
        }
        var rows = items.Select(i => new IntakeRowViewModel(i, SetActionAsync)).ToList();
        _post(() =>
        {
            _allRows = rows;
            foreach (var f in LaneFilters)
                f.Count = f.IsFailedFilter
                    ? rows.Count(r => r.Item.Status == IntakeItemStatus.Failed)
                    : f.Lane is { } lane ? rows.Count(r => r.Item.Status != IntakeItemStatus.Failed && r.Lane == lane) : rows.Count;
            ApplyFilter();
            RaiseAll();
        });
    }

    private void RaiseAll()
    {
        this.RaisePropertyChanged(nameof(HasSession));
        this.RaisePropertyChanged(nameof(HasUnfinishedScan));
        this.RaisePropertyChanged(nameof(IsReviewing));
        this.RaisePropertyChanged(nameof(IsStart));
        this.RaisePropertyChanged(nameof(SessionTitle));
        this.RaisePropertyChanged(nameof(SessionSubtitle));
        this.RaisePropertyChanged(nameof(DecisionsCount));
        this.RaisePropertyChanged(nameof(ApplyText));
        this.RaisePropertyChanged(nameof(DecisionsSummary));
        this.RaisePropertyChanged(nameof(IsFilterEmpty));
        _chooseSourceCommand.RaiseCanExecuteChanged();
        _resumeScanCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        _applyCommand.RaiseCanExecuteChanged();
        _closeSessionCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workCts?.Cancel();
        _workCts?.Dispose();
    }
}
