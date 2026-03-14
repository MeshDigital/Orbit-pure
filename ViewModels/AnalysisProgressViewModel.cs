using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

public class AnalysisProgressViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly DateTime _startTime;
    
    private string _trackId;
    private string _currentStep = "Initializing...";
    private int _progressPercent;
    private double _elapsedTime;

    public string TrackId
    {
        get => _trackId;
        set => this.RaiseAndSetIfChanged(ref _trackId, value);
    }

    public string CurrentStep
    {
        get => _currentStep;
        set => this.RaiseAndSetIfChanged(ref _currentStep, value);
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        set => this.RaiseAndSetIfChanged(ref _progressPercent, value);
    }

    public double ElapsedTime
    {
        get => _elapsedTime;
        private set => this.RaiseAndSetIfChanged(ref _elapsedTime, value);
    }

    public AnalysisProgressViewModel(string trackId, IEventBus eventBus)
    {
        _trackId = trackId;
        _startTime = DateTime.UtcNow;

        // Subscribe to progress events for this specific track
        eventBus.GetEvent<AnalysisProgressEvent>()
            .Where(e => e.TrackGlobalId == trackId)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                CurrentStep = e.CurrentStep;
                ProgressPercent = e.ProgressPercent;
                ElapsedTime = (DateTime.UtcNow - _startTime).TotalSeconds;
            })
            .DisposeWith(_disposables);

        // Update elapsed time every 100ms
        Observable.Interval(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                ElapsedTime = (DateTime.UtcNow - _startTime).TotalSeconds;
            })
            .DisposeWith(_disposables);
    }

    public void Dispose()
    {
        _disposables?.Dispose();
    }
}
