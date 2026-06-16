using System;
using ReactiveUI;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Represents a visual cue marker on the workstation timeline.
/// Supports smooth drag-and-drop feedback via transient seconds tracking.
/// </summary>
public sealed class CueMarkerViewModel : ReactiveObject
{
    private double _timestampInSeconds;
    private double _transientSeconds;
    private string _label = string.Empty;
    private string _color = "#FFFFFF";

    public Guid Id { get; init; } = Guid.NewGuid();

    public double TimestampInSeconds
    {
        get => _timestampInSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _timestampInSeconds, value);
            TransientSeconds = value; // Sync transient seconds by default
        }
    }

    public double TransientSeconds
    {
        get => _transientSeconds;
        private set => this.RaiseAndSetIfChanged(ref _transientSeconds, value);
    }

    public string Label
    {
        get => _label;
        set => this.RaiseAndSetIfChanged(ref _label, value);
    }

    public string Color
    {
        get => _color;
        set => this.RaiseAndSetIfChanged(ref _color, value);
    }

    public string StrategyHexColor => Color; // Alias for UI list binding

    public string DisplayTimestamp
    {
        get
        {
            var time = TimeSpan.FromSeconds(TimestampInSeconds);
            return time.Hours > 0 
                ? time.ToString(@"hh\:mm\:ss\.fff") 
                : time.ToString(@"mm\:ss\.fff");
        }
    }

    public string IntentLabel => Label; // Alias for UI list binding

    /// <summary>
    /// Updates the transient position during drag operations before committing to the DB.
    /// </summary>
    public void UpdateTransientPosition(double seconds)
    {
        TransientSeconds = Math.Max(0.0, seconds);
    }
}
