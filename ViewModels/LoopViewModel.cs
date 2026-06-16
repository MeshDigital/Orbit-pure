using System;
using ReactiveUI;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Represents a loop point entry in the curation active loops registry.
/// </summary>
public sealed class LoopViewModel : ReactiveObject
{
    private bool _isActiveLoop;
    private double _startSeconds;
    private double _endSeconds;
    private string _loopLengthString = "4 Bars";

    public Guid Id { get; init; } = Guid.NewGuid();

    public double StartSeconds
    {
        get => _startSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _startSeconds, value);
            this.RaisePropertyChanged(nameof(BoundaryRangeString));
        }
    }

    public double EndSeconds
    {
        get => _endSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _endSeconds, value);
            this.RaisePropertyChanged(nameof(BoundaryRangeString));
        }
    }

    public bool IsActiveLoop
    {
        get => _isActiveLoop;
        set => this.RaiseAndSetIfChanged(ref _isActiveLoop, value);
    }

    public string LoopLengthString
    {
        get => _loopLengthString;
        set => this.RaiseAndSetIfChanged(ref _loopLengthString, value);
    }

    public string BoundaryRangeString
    {
        get
        {
            var start = TimeSpan.FromSeconds(StartSeconds);
            var end = TimeSpan.FromSeconds(EndSeconds);
            return $@"{start:mm\:ss} - {end:mm\:ss}";
        }
    }
}
