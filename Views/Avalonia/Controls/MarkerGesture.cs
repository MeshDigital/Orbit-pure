using System;

namespace SLSKDONET.Views.Avalonia.Controls;

public enum MarkerGestureResult
{
    /// <summary>Nothing was pressed (or the gesture was cancelled).</summary>
    None,
    /// <summary>Pressed and released within the threshold: select + prelisten, the marker is untouched.</summary>
    Click,
    /// <summary>Moved past the threshold: the marker was dragged.</summary>
    Drag,
}

/// <summary>
/// Click-versus-drag on a cue marker, shared by Cue Forge's waveform and overview and the Flow
/// Builder deck waveforms: a press that moves less than <see cref="DragThresholdPixels"/> before
/// release is a click (play from the cue), anything further is a drag (move the cue). The marker is
/// only moved once the threshold is passed, so clicking can never nudge a cue.
/// </summary>
public sealed class MarkerGesture<T> where T : class
{
    /// <summary>Horizontal movement (device-independent pixels) that turns a press into a drag.</summary>
    public const double DragThresholdPixels = 4.0;

    private double _pressX;

    /// <summary>The marker that was pressed, until release.</summary>
    public T? Target { get; private set; }

    /// <summary>True once the press moved past the threshold.</summary>
    public bool IsDragging { get; private set; }

    public void Press(T target, double x)
    {
        Target = target;
        _pressX = x;
        IsDragging = false;
    }

    /// <summary>Pointer moved to <paramref name="x"/>. Returns true exactly once: when the press
    /// becomes a drag (the moment to snapshot for undo before moving the marker).</summary>
    public bool Move(double x)
    {
        if (Target == null || IsDragging) return false;
        if (Math.Abs(x - _pressX) < DragThresholdPixels) return false;
        IsDragging = true;
        return true;
    }

    /// <summary>Ends the gesture and says what it was.</summary>
    public (T? Target, MarkerGestureResult Result) Release()
    {
        var target = Target;
        var result = target == null ? MarkerGestureResult.None : IsDragging ? MarkerGestureResult.Drag : MarkerGestureResult.Click;
        Target = null;
        IsDragging = false;
        return (target, result);
    }

    /// <summary>Drops the gesture without a result (capture lost, control detached).</summary>
    public void Cancel()
    {
        Target = null;
        IsDragging = false;
    }
}
