using System;
using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.ViewModels;

/// <summary>
/// The Analysis page's "N tracks need a loudness measurement" card: count, start, live progress,
/// cancel. The run itself lives in <see cref="LoudnessBackfillService"/> (a singleton), so a run
/// started from the Library tools menu shows up here too, and the service posts the final toast.
/// </summary>
public sealed class LoudnessBackfillViewModel : ReactiveObject, IDisposable
{
    private readonly LoudnessBackfillService _service;
    private readonly ILogger<LoudnessBackfillViewModel> _logger;
    private readonly CompositeDisposable _disposables = new();

    private int _candidateCount;
    private bool _isCounting;
    private bool _isRunning;
    private bool _isWaitingForAnalysis;
    private int _done;
    private int _total;
    private int _failed;

    public LoudnessBackfillViewModel(LoudnessBackfillService service, IEventBus eventBus, ILogger<LoudnessBackfillViewModel> logger)
    {
        _service = service;
        _logger = logger;

        var canStart = this.WhenAnyValue(x => x.IsRunning, x => x.CandidateCount, (running, count) => !running && count > 0);
        StartCommand = ReactiveCommand.Create(Start, canStart);
        CancelCommand = ReactiveCommand.Create(() => _service.Cancel(), this.WhenAnyValue(x => x.IsRunning));

        eventBus.GetEvent<LoudnessBackfillProgressEvent>()
            .Sample(TimeSpan.FromMilliseconds(200))
            .Merge(eventBus.GetEvent<LoudnessBackfillProgressEvent>().Where(p => !p.IsRunning)) // never drop the final one
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(Apply)
            .DisposeWith(_disposables);

        // A full analysis also fixes legacy loudness; keep the count honest without polling.
        eventBus.GetEvent<TrackAnalysisUpdatedEvent>()
            .Throttle(TimeSpan.FromSeconds(5))
            .Where(_ => !_service.IsRunning)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt => _ = RefreshCountAsync())
            .DisposeWith(_disposables);

        Apply(_service.Progress);
        _ = RefreshCountAsync();
    }

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Tracks with no usable loudness whose file is on disk.</summary>
    public int CandidateCount
    {
        get => _candidateCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _candidateCount, value);
            this.RaisePropertyChanged(nameof(IsVisible));
            this.RaisePropertyChanged(nameof(NoticeText));
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(IsVisible));
            this.RaisePropertyChanged(nameof(IsIdle));
            this.RaisePropertyChanged(nameof(NoticeText));
        }
    }

    public bool IsIdle => !IsRunning;

    public bool IsWaitingForAnalysis
    {
        get => _isWaitingForAnalysis;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isWaitingForAnalysis, value);
            this.RaisePropertyChanged(nameof(NoticeText));
        }
    }

    /// <summary>The card is hidden once nothing needs measuring (and no run is in progress).</summary>
    public bool IsVisible => IsRunning || CandidateCount > 0;

    public int Done { get => _done; private set => this.RaiseAndSetIfChanged(ref _done, value); }
    public int Total { get => _total; private set => this.RaiseAndSetIfChanged(ref _total, value); }
    public int Failed { get => _failed; private set => this.RaiseAndSetIfChanged(ref _failed, value); }

    public string NoticeText
    {
        get
        {
            if (IsRunning)
                return IsWaitingForAnalysis
                    ? "Measuring loudness · paused while the analysis queue runs"
                    : "Measuring loudness";
            return CandidateCount == 1
                ? "1 track needs a loudness measurement"
                : $"{N(CandidateCount)} tracks need a loudness measurement";
        }
    }

    public string DetailText =>
        "Analysed before real loudness metering existed, so it shows \"—\" and plays without loudness "
        + "normalisation. Measuring reads each file once (about a second per track) instead of a full re-analysis.";

    /// <summary>"123 / 2,400 · 3 failed" (mono in the view).</summary>
    public string ProgressText => Failed > 0
        ? $"{N(Done)} / {N(Total)} · {N(Failed)} failed"
        : $"{N(Done)} / {N(Total)}";

    /// <summary>Starts the shared job. Also used by the Library tools menu.</summary>
    public void Start()
    {
        if (!_service.TryStart())
            _logger.LogInformation("[LoudnessBackfill] Start ignored: a run is already in progress");
        IsRunning = true;
    }

    private void Apply(LoudnessBackfillProgressEvent p)
    {
        bool finished = IsRunning && !p.IsRunning;
        Done = p.Done;
        Total = p.Total;
        Failed = p.Failed;
        IsWaitingForAnalysis = p.IsWaitingForAnalysis;
        IsRunning = p.IsRunning;
        this.RaisePropertyChanged(nameof(ProgressText));
        if (finished) _ = RefreshCountAsync();
    }

    public async Task RefreshCountAsync()
    {
        if (_isCounting) return;
        _isCounting = true;
        try
        {
            var count = await Task.Run(() => _service.CountCandidatesAsync()).ConfigureAwait(true);
            CandidateCount = count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LoudnessBackfill] Counting tracks without loudness failed; the card stays hidden.");
            CandidateCount = 0;
        }
        finally
        {
            _isCounting = false;
        }
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public void Dispose() => _disposables.Dispose();
}
