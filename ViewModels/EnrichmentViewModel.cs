using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>One provider line on the Enrichment card ("Deezer · 1,204 matched · 3 rate-limit pauses").</summary>
public sealed class EnrichmentProviderRowViewModel : ReactiveObject
{
    private string _statusText = string.Empty;
    private string _countsText = string.Empty;
    private bool _isOk;
    private bool _isWarning;
    private bool _isError;

    public EnrichmentProviderRowViewModel(string name, string displayName)
    {
        Name = name;
        DisplayName = displayName;
    }

    public string Name { get; }
    public string DisplayName { get; }
    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    /// <summary>Mono numerals: "1,204 matched · 3 pauses".</summary>
    public string CountsText { get => _countsText; set => this.RaiseAndSetIfChanged(ref _countsText, value); }
    public bool IsOk { get => _isOk; set => this.RaiseAndSetIfChanged(ref _isOk, value); }
    public bool IsWarning { get => _isWarning; set => this.RaiseAndSetIfChanged(ref _isWarning, value); }
    public bool IsError { get => _isError; set => this.RaiseAndSetIfChanged(ref _isError, value); }

    public void Apply(ProviderStatusSnapshot s)
    {
        IsOk = s.Availability == ProviderAvailability.Ready && !s.IsCircuitOpen;
        IsWarning = s.IsCircuitOpen || s.Availability is ProviderAvailability.NeedsKey or ProviderAvailability.NeedsTool or ProviderAvailability.NotConnected;
        IsError = s.Availability == ProviderAvailability.Blocked;
        StatusText = Describe(s);
        CountsText = s.Availability == ProviderAvailability.Disabled
            ? string.Empty
            : s.RateLimitPauses > 0
                ? $"{N(s.Matched)} matched · {N(s.RateLimitPauses)} rate-limit {(s.RateLimitPauses == 1 ? "pause" : "pauses")}"
                : $"{N(s.Matched)} matched";
    }

    internal static string Describe(ProviderStatusSnapshot s) => s.Availability switch
    {
        ProviderAvailability.Disabled => "Off",
        ProviderAvailability.NeedsKey => "Disabled · add a key in Settings → Connection",
        ProviderAvailability.NeedsTool => "Disabled · needs fpcalc (Chromaprint)",
        ProviderAvailability.NotConnected => "Not connected",
        ProviderAvailability.Blocked => "Switched off this session · " + (s.LastError ?? "access denied"),
        _ when s.IsCircuitOpen && s.OpenUntilUtc is { } until => $"Waiting · resumes {until.ToLocalTime():HH:mm:ss}",
        _ => "Ready",
    };

    internal static string N(int v) => SLSKDONET.Utils.CountFormat.N(v);
}

/// <summary>
/// The "Enrichment" card (Analysis page; started from the Library tools menu and Settings too):
/// how many tracks still need metadata, live progress, one status row per provider, and
/// Pause / Resume / Cancel. The work runs in <see cref="MetadataEnrichmentOrchestrator"/>.
/// </summary>
public sealed class EnrichmentViewModel : ReactiveObject, IDisposable
{
    private readonly MetadataEnrichmentOrchestrator _orchestrator;
    private readonly IEventBus _eventBus;
    private readonly ILogger<EnrichmentViewModel> _logger;
    private readonly CompositeDisposable _disposables = new();

    private int _needsCount;
    private bool _isRunning;
    private bool _isPaused;
    private bool _isWaitingForAnalysis;
    private int _done;
    private int _total;
    private int _enriched;
    private int _pending;
    private string? _lastError;

    public EnrichmentViewModel(MetadataEnrichmentOrchestrator orchestrator, IEventBus eventBus, ILogger<EnrichmentViewModel> logger)
    {
        _orchestrator = orchestrator;
        _eventBus = eventBus;
        _logger = logger;

        foreach (var p in orchestrator.Providers)
            Providers.Add(new EnrichmentProviderRowViewModel(p.Name, p.DisplayName));

        var idle = this.WhenAnyValue(x => x.IsRunning).Select(r => !r);
        StartCommand = ReactiveCommand.CreateFromTask(StartAsync, idle);
        PauseCommand = ReactiveCommand.Create(_orchestrator.Pause, this.WhenAnyValue(x => x.IsRunning, x => x.IsPaused, (r, p) => r && !p));
        ResumeCommand = ReactiveCommand.Create(_orchestrator.Resume, this.WhenAnyValue(x => x.IsPaused));
        CancelCommand = ReactiveCommand.CreateFromTask(_orchestrator.CancelAsync, this.WhenAnyValue(x => x.IsRunning, x => x.IsPaused, x => x.Pending, (r, p, n) => r || p || n > 0));
        foreach (var cmd in new IHandleObservableErrors[] { StartCommand, CancelCommand })
            cmd.ThrownExceptions.Subscribe(OnCommandError).DisposeWith(_disposables);

        eventBus.GetEvent<EnrichmentProgressEvent>()
            .Sample(TimeSpan.FromMilliseconds(250))
            .Merge(eventBus.GetEvent<EnrichmentProgressEvent>().Where(e => !e.Progress.IsRunning))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e => Apply(e.Progress))
            .DisposeWith(_disposables);

        eventBus.GetEvent<EnrichmentProgressEvent>()
            .Where(e => !e.Progress.IsRunning)
            .Throttle(TimeSpan.FromSeconds(1))
            .Subscribe(evt => { _ = RefreshCountAsync(); })
            .DisposeWith(_disposables);

        Apply(_orchestrator.CurrentProgress());
        _ = RefreshCountAsync();
    }

    public ObservableCollection<EnrichmentProviderRowViewModel> Providers { get; } = new();

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseCommand { get; }
    public ReactiveCommand<Unit, Unit> ResumeCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public int NeedsCount { get => _needsCount; private set { this.RaiseAndSetIfChanged(ref _needsCount, value); RaiseText(); } }
    public bool IsRunning { get => _isRunning; private set { this.RaiseAndSetIfChanged(ref _isRunning, value); this.RaisePropertyChanged(nameof(IsIdle)); RaiseText(); } }
    public bool IsIdle => !IsRunning;
    public bool IsPaused { get => _isPaused; private set { this.RaiseAndSetIfChanged(ref _isPaused, value); RaiseText(); } }
    public bool IsWaitingForAnalysis { get => _isWaitingForAnalysis; private set { this.RaiseAndSetIfChanged(ref _isWaitingForAnalysis, value); RaiseText(); } }
    public int Done { get => _done; private set => this.RaiseAndSetIfChanged(ref _done, value); }
    public int Total { get => _total; private set => this.RaiseAndSetIfChanged(ref _total, value); }
    public int Enriched { get => _enriched; private set => this.RaiseAndSetIfChanged(ref _enriched, value); }
    public int Pending { get => _pending; private set => this.RaiseAndSetIfChanged(ref _pending, value); }
    public bool ShowProgress => IsRunning || IsPaused;
    public bool IsComplete => !ShowProgress && NeedsCount == 0;
    public bool CanStart => IsIdle && !IsPaused && NeedsCount > 0;

    public string? LastError { get => _lastError; private set { this.RaiseAndSetIfChanged(ref _lastError, value); this.RaisePropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(LastError);

    public string NoticeText
    {
        get
        {
            if (IsPaused) return "Enrichment paused";
            if (IsRunning)
                return IsWaitingForAnalysis ? "Enriching metadata · one track at a time while analysis runs" : "Enriching metadata";
            if (NeedsCount == 0) return "Library metadata is complete";
            return NeedsCount == 1 ? "1 track needs metadata" : $"{EnrichmentProviderRowViewModel.N(NeedsCount)} tracks need metadata";
        }
    }

    public string DetailText => IsComplete
        ? "Every track has cover art, album, label, year and genre, or was looked up in the last 7 days."
        : "Cover art, album, label, year and genre from the file tags first, then Deezer, MusicBrainz and the other sources enabled in Settings. "
          + "Runs in the background at each source's rate limit; your edits and analysed BPM/key are never overwritten.";

    /// <summary>"123 / 3,267 · 98 enriched" (mono in the view).</summary>
    public string ProgressText =>
        $"{EnrichmentProviderRowViewModel.N(Done)} / {EnrichmentProviderRowViewModel.N(Math.Max(Total, Done))} · {EnrichmentProviderRowViewModel.N(Enriched)} enriched";

    private void RaiseText()
    {
        this.RaisePropertyChanged(nameof(NoticeText));
        this.RaisePropertyChanged(nameof(DetailText));
        this.RaisePropertyChanged(nameof(ShowProgress));
        this.RaisePropertyChanged(nameof(IsComplete));
        this.RaisePropertyChanged(nameof(CanStart));
    }

    /// <summary>Queues every track missing metadata and starts. Also used by the Library tools menu.</summary>
    public async Task StartAsync()
    {
        LastError = null;
        var queued = await _orchestrator.EnrichLibraryAsync().ConfigureAwait(true);
        IsRunning = _orchestrator.IsRunning || queued > 0;
        if (queued == 0)
            _eventBus.Publish(new ToastRequestedEvent("Enrich library", "Every track already has metadata (or was looked up in the last 7 days).", NotificationType.Information, null));
    }

    public async Task RefreshCountAsync()
    {
        try
        {
            var count = await Task.Run(() => _orchestrator.CountTracksNeedingMetadataAsync()).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => NeedsCount = count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Enrichment] Could not count tracks needing metadata");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => LastError = "Could not count tracks needing metadata: " + ex.Message);
        }
    }

    private void Apply(EnrichmentProgress p)
    {
        Done = p.Done;
        Total = p.Total;
        Enriched = p.Enriched;
        Pending = p.Pending;
        IsWaitingForAnalysis = p.IsSlowedForAnalysis;
        IsPaused = p.IsPaused;
        IsRunning = p.IsRunning;
        foreach (var snapshot in p.Providers)
            Providers.FirstOrDefault(r => r.Name == snapshot.Name)?.Apply(snapshot);
        this.RaisePropertyChanged(nameof(ProgressText));
    }

    private void OnCommandError(Exception ex)
    {
        _logger.LogError(ex, "[Enrichment] Command failed");
        LastError = ex.Message;
        _eventBus.Publish(new ToastRequestedEvent("Enrichment", ex.Message, NotificationType.Error, null));
    }

    public void Dispose() => _disposables.Dispose();
}
