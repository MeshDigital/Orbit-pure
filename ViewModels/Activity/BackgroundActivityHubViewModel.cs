using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Activity;

public enum BackgroundActivityKind { Import, Analysis, Enrichment, Loudness }

/// <summary>Minimal INotifyPropertyChanged base (no dispatcher, so the hub is tested headless).</summary>
public abstract class ActivityObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>One row of the activity flyout: a long-running job with its progress and actions.</summary>
public sealed class BackgroundActivityItemViewModel : ActivityObservable
{
    private readonly RelayCommand _pauseCommand;
    private readonly RelayCommand _resumeCommand;
    private readonly RelayCommand _openCommand;
    private bool _isActive;
    private bool _isFinished;
    private bool _isPaused;
    private bool _isIndeterminate;
    private double _progress;
    private int _done;
    private int _total;
    private string _status = string.Empty;

    internal BackgroundActivityItemViewModel(BackgroundActivityKind kind, string title, string openLabel,
        Action? pause, Action? resume, Action? open)
    {
        Kind = kind;
        Title = title;
        OpenLabel = openLabel;
        SupportsPause = pause != null && resume != null;
        _pauseCommand = new RelayCommand(() => pause?.Invoke(), () => SupportsPause && IsActive && !IsPaused);
        _resumeCommand = new RelayCommand(() => resume?.Invoke(), () => SupportsPause && IsPaused);
        _openCommand = new RelayCommand(() => open?.Invoke(), () => open != null);
    }

    public BackgroundActivityKind Kind { get; }
    public string Title { get; }
    /// <summary>"Show", "Open Analysis".</summary>
    public string OpenLabel { get; }
    public bool SupportsPause { get; }

    public ICommand PauseCommand => _pauseCommand;
    public ICommand ResumeCommand => _resumeCommand;
    public ICommand OpenCommand => _openCommand;

    /// <summary>Running (or paused): shown in the chip and the flyout.</summary>
    public bool IsActive { get => _isActive; private set { if (Set(ref _isActive, value)) { Raise(nameof(IsVisible)); RaiseCommands(); } } }

    /// <summary>Finished but still worth showing (a background import's summary waits to be opened).</summary>
    public bool IsFinished { get => _isFinished; private set { if (Set(ref _isFinished, value)) Raise(nameof(IsVisible)); } }

    public bool IsVisible => IsActive || IsFinished;
    public bool IsPaused { get => _isPaused; private set { if (Set(ref _isPaused, value)) { Raise(nameof(ShowPause)); Raise(nameof(ShowResume)); RaiseCommands(); } } }
    public bool ShowPause => SupportsPause && !IsPaused;
    public bool ShowResume => SupportsPause && IsPaused;
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }

    /// <summary>0–100.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public int Done { get => _done; private set => Set(ref _done, value); }
    public int Total { get => _total; private set => Set(ref _total, value); }

    /// <summary>"1,204 / 3,267" (mono in the view).</summary>
    public string CountsText => Total > 0 ? $"{CountFormat.N(Done)} / {CountFormat.N(Total)}" : Done > 0 ? CountFormat.N(Done) : string.Empty;

    /// <summary>"Reading tags", "Paused", "One at a time while analysis runs".</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    internal void Update(bool active, bool finished, bool paused, int done, int total, string status, bool indeterminate = false)
    {
        IsActive = active;
        IsFinished = !active && finished;
        IsPaused = active && paused;
        Done = Math.Max(0, done);
        Total = Math.Max(Done, total);
        IsIndeterminate = active && indeterminate;
        Progress = Total > 0 ? Math.Clamp(100.0 * Done / Total, 0, 100) : finished ? 100 : 0;
        Status = status;
        Raise(nameof(CountsText));
    }

    private void RaiseCommands()
    {
        _pauseCommand.RaiseCanExecuteChanged();
        _resumeCommand.RaiseCanExecuteChanged();
        _openCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>What the hub needs from the enrichment orchestrator (fakeable in tests).</summary>
public interface IEnrichmentActivityControl
{
    EnrichmentProgress CurrentProgress();
    void Pause();
    void Resume();
}

/// <summary>The orchestrator as an <see cref="IEnrichmentActivityControl"/>.</summary>
public sealed class OrchestratorEnrichmentActivityControl : IEnrichmentActivityControl
{
    private readonly MetadataEnrichmentOrchestrator _orchestrator;
    public OrchestratorEnrichmentActivityControl(MetadataEnrichmentOrchestrator orchestrator) => _orchestrator = orchestrator;
    public EnrichmentProgress CurrentProgress() => _orchestrator.CurrentProgress();
    public void Pause() => _orchestrator.Pause();
    public void Resume() => _orchestrator.Resume();
}

/// <summary>
/// The one background-activity indicator: a compact top-bar chip (also in the setup wizard's
/// header) whose flyout lists every long-running job, library import, analysis, metadata
/// enrichment and loudness backfill, with progress, Pause/Resume where the job supports it, and
/// Open. It replaces the import sheet's own "Run in background" chip, so jobs never stack up as
/// competing chips.
/// <para>
/// Sources publish from their own threads at their own pace; the hub only stores the latest
/// snapshot and applies it on the UI thread at most every <see cref="Throttle"/> (≈ 4 updates
/// per second), so a 3,000-track run never turns into thousands of UI posts.
/// </para>
/// </summary>
public sealed class BackgroundActivityHubViewModel : ActivityObservable, IDisposable
{
    private readonly ILogger? _logger;
    private readonly Action<Action> _post;
    private readonly LibraryImportViewModel? _import;
    private readonly IEnrichmentActivityControl? _enrichment;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly object _gate = new();

    private EnrichmentProgress? _enrichmentProgress;
    private AnalysisQueueStatusChangedEvent? _analysisStatus;
    private LoudnessBackfillProgressEvent? _loudness;
    private int _analysisBaseline = -1;
    private int _applyScheduled;
    private int _applyCount;
    private bool _disposed;

    public BackgroundActivityHubViewModel(
        IEventBus eventBus,
        LibraryImportViewModel? import,
        IEnrichmentActivityControl? enrichment,
        Action<string>? navigate,
        ILogger<BackgroundActivityHubViewModel>? logger = null,
        Action<Action>? postToUi = null,
        TimeSpan? throttle = null)
    {
        _logger = logger;
        _post = postToUi ?? (a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background));
        _import = import;
        _enrichment = enrichment;
        Throttle = throttle ?? TimeSpan.FromMilliseconds(250);

        Import = new BackgroundActivityItemViewModel(BackgroundActivityKind.Import, "Library import", "Show",
            null, null, import == null ? null : () => import.ShowSheetCommand.Execute(null));
        Analysis = new BackgroundActivityItemViewModel(BackgroundActivityKind.Analysis, "Analysis", "Open",
            null, null, navigate == null ? null : () => navigate("Analysis"));
        Enrichment = new BackgroundActivityItemViewModel(BackgroundActivityKind.Enrichment, "Metadata & artwork", "Open",
            enrichment == null ? null : enrichment.Pause, enrichment == null ? null : enrichment.Resume,
            navigate == null ? null : () => navigate("Analysis"));
        Loudness = new BackgroundActivityItemViewModel(BackgroundActivityKind.Loudness, "Loudness", "Open",
            null, null, navigate == null ? null : () => navigate("Analysis"));
        // Loudness is stopped from its Analysis-page card; the flyout only opens it.
        Items = new[] { Import, Enrichment, Analysis, Loudness };

        try
        {
            _subscriptions.Add(eventBus.GetEvent<EnrichmentProgressEvent>().Subscribe(e => { lock (_gate) _enrichmentProgress = e.Progress; ScheduleApply(); }));
            _subscriptions.Add(eventBus.GetEvent<AnalysisQueueStatusChangedEvent>().Subscribe(e => { lock (_gate) _analysisStatus = e; ScheduleApply(); }));
            _subscriptions.Add(eventBus.GetEvent<LoudnessBackfillProgressEvent>().Subscribe(e => { lock (_gate) _loudness = e; ScheduleApply(); }));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Activity hub: could not subscribe to job progress; the chip only shows imports");
        }

        if (import != null) import.PropertyChanged += OnImportChanged;
        if (enrichment != null)
        {
            try { _enrichmentProgress = enrichment.CurrentProgress(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Activity hub: reading the enrichment state failed"); }
        }
        ApplyNow();
    }

    public TimeSpan Throttle { get; }

    public BackgroundActivityItemViewModel Import { get; }
    public BackgroundActivityItemViewModel Analysis { get; }
    public BackgroundActivityItemViewModel Enrichment { get; }
    public BackgroundActivityItemViewModel Loudness { get; }

    /// <summary>Flyout rows in display order (each hides itself when idle).</summary>
    public IReadOnlyList<BackgroundActivityItemViewModel> Items { get; }

    /// <summary>UI applications so far (tests: bounded refreshes).</summary>
    internal int ApplyCount => Volatile.Read(ref _applyCount);

    private bool _isVisible;
    private bool _isAnyRunning;
    private string _chipText = string.Empty;
    private double _chipProgress;
    private int _activeCount;

    /// <summary>Something is running, or a finished background import waits to be opened.</summary>
    public bool IsVisible { get => _isVisible; private set => Set(ref _isVisible, value); }
    public bool IsAnyRunning { get => _isAnyRunning; private set { if (Set(ref _isAnyRunning, value)) Raise(nameof(IsNothingRunning)); } }
    public bool IsNothingRunning => !IsAnyRunning;
    public int ActiveCount { get => _activeCount; private set => Set(ref _activeCount, value); }

    /// <summary>"Importing 1,204 / 3,267", "3 jobs · 41%", "Import done · 3,267 added".</summary>
    public string ChipText { get => _chipText; private set => Set(ref _chipText, value); }

    /// <summary>Mean progress of the running jobs, 0–100.</summary>
    public double ChipProgress { get => _chipProgress; private set => Set(ref _chipProgress, value); }

    /// <summary>"Follow it later from the activity chip in the top bar or the Analysis page."</summary>
    public string HintText => "Follow these later from the activity chip in the top bar, or the Enrichment card on the Analysis page.";

    private void OnImportChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryImportViewModel.IsRunning) or nameof(LibraryImportViewModel.Processed)
            or nameof(LibraryImportViewModel.Total) or nameof(LibraryImportViewModel.Phase) or nameof(LibraryImportViewModel.IsCompleted)
            or nameof(LibraryImportViewModel.IsInBackground) or nameof(LibraryImportViewModel.FilesFound) or nameof(LibraryImportViewModel.Added)
            or nameof(LibraryImportViewModel.IsOpen))
            ScheduleApply();
    }

    private void ScheduleApply()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 1) return;
        if (Throttle <= TimeSpan.Zero)
        {
            _post(ApplyScheduled);
            return;
        }
        _ = Task.Delay(Throttle).ContinueWith(_ => _post(ApplyScheduled), TaskScheduler.Default);
    }

    private void ApplyScheduled()
    {
        Volatile.Write(ref _applyScheduled, 0);
        ApplyNow();
    }

    /// <summary>Applies the latest snapshots (UI thread; tests call it directly).</summary>
    internal void ApplyNow()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _applyCount);
        try
        {
            EnrichmentProgress? enrichment;
            AnalysisQueueStatusChangedEvent? analysis;
            LoudnessBackfillProgressEvent? loudness;
            lock (_gate)
            {
                enrichment = _enrichmentProgress;
                analysis = _analysisStatus;
                loudness = _loudness;
            }

            ApplyImport();
            ApplyEnrichment(enrichment);
            ApplyAnalysis(analysis);
            ApplyLoudness(loudness);

            var active = Items.Where(i => i.IsActive).ToList();
            ActiveCount = active.Count;
            IsAnyRunning = active.Count > 0;
            IsVisible = Items.Any(i => i.IsVisible);
            ChipProgress = active.Count == 0 ? 100 : active.Average(i => i.Progress);
            ChipText = active.Count switch
            {
                0 when Import.IsFinished => Import.Status,
                0 => string.Empty,
                1 => ShortLine(active[0]),
                _ => $"{active.Count} jobs · {ChipProgress:0}%",
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Activity hub: applying job progress failed");
        }
    }

    private static string ShortLine(BackgroundActivityItemViewModel item)
    {
        var verb = item.Kind switch
        {
            BackgroundActivityKind.Import => "Importing",
            BackgroundActivityKind.Analysis => "Analysing",
            BackgroundActivityKind.Enrichment => item.IsPaused ? "Enrichment paused" : "Enriching",
            _ => "Measuring loudness",
        };
        return string.IsNullOrEmpty(item.CountsText) ? verb : $"{verb} {item.CountsText}";
    }

    private void ApplyImport()
    {
        if (_import == null) return;
        var running = _import.IsRunning;
        // A finished import stays listed while its summary is parked in the background (the old chip's job).
        var finishedInBackground = !running && _import.IsCompleted && _import.IsInBackground;
        var scanning = running && _import.Phase == SLSKDONET.Services.LibraryImportPhase.Scanning;
        var status = running
            ? scanning ? $"Scanning folders · {CountFormat.N(_import.FilesFound)} found" : _import.PhaseTitle
            : finishedInBackground ? $"Import done · {CountFormat.N(_import.Added)} added" : string.Empty;
        // Scanning has no total; reading/adding count against what is being checked.
        Import.Update(running, finishedInBackground, false,
            scanning ? _import.FilesFound : _import.Processed, scanning ? 0 : _import.Total, status, indeterminate: scanning);
    }

    private void ApplyEnrichment(EnrichmentProgress? p)
    {
        if (p == null) { Enrichment.Update(false, false, false, 0, 0, string.Empty); return; }
        var active = p.IsRunning || p.IsPaused;
        var status = p.IsPaused ? "Paused"
            : p.IsSlowedForAnalysis ? "One track at a time while analysis runs"
            : WaitingProvider(p) is { } waiting ? $"{waiting} paused for its rate limit"
            : $"{CountFormat.N(p.Enriched)} got new metadata";
        Enrichment.Update(active, false, p.IsPaused, p.Done, Math.Max(p.Total, p.Done + p.Pending), status);
    }

    private static string? WaitingProvider(EnrichmentProgress p) =>
        p.Providers.FirstOrDefault(s => s.IsCircuitOpen && s.Availability == ProviderAvailability.Ready)?.DisplayName;

    private void ApplyAnalysis(AnalysisQueueStatusChangedEvent? s)
    {
        if (s == null || s.QueuedCount <= 0)
        {
            _analysisBaseline = -1;
            Analysis.Update(false, false, false, 0, 0, string.Empty);
            return;
        }
        // ProcessedCount is cumulative for the session: count this run from where it started.
        if (_analysisBaseline < 0 || _analysisBaseline > s.ProcessedCount) _analysisBaseline = s.ProcessedCount;
        var done = s.ProcessedCount - _analysisBaseline;
        Analysis.Update(true, false, false, done, done + s.QueuedCount,
            s.MaxConcurrency > 0 ? $"BPM, key and cues · {s.MaxConcurrency} at a time" : "BPM, key and cues");
    }

    private void ApplyLoudness(LoudnessBackfillProgressEvent? p)
    {
        if (p == null || !p.IsRunning) { Loudness.Update(false, false, false, 0, 0, string.Empty); return; }
        Loudness.Update(true, false, false, p.Done, p.Total, p.IsWaitingForAnalysis ? "Waiting while analysis runs" : "Measuring LUFS");
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        if (_import != null) _import.PropertyChanged -= OnImportChanged;
    }
}
