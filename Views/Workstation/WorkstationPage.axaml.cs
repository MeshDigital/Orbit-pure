using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

public partial class WorkstationPage : UserControl
{
    // Used by WorkstationDeckRow to identify playlist track drag payloads
    internal const string WorkstationPlaylistTrackFormat = "ORBIT_WorkstationPlaylistTrack";

    private bool _isDraggingCueMarker;
    private CueMarkerViewModel? _activeTargetMarker;

    public WorkstationPage()
    {
        InitializeComponent();
    }

    private void OnWaveformPointerPressed(object sender, PointerPressedEventArgs e)
    {
        var pointerPos = e.GetCurrentPoint(WaveformTimelineCanvas);
        
        // Check if the click coordinates intersect with a cue point line envelope
        if (HitTestCueMarkers(pointerPos.Position.X, out var targetedMarker))
        {
            _isDraggingCueMarker = true;
            _activeTargetMarker = targetedMarker;
            e.Pointer.Capture(WaveformTimelineCanvas);
        }
    }

    private void OnWaveformPointerMoved(object sender, PointerEventArgs e)
    {
        if (!_isDraggingCueMarker || _activeTargetMarker == null || DataContext is not CurationWorkstationViewModel vm) return;

        var currentPoint = e.GetCurrentPoint(WaveformTimelineCanvas);
        double canvasWidth = WaveformTimelineCanvas.Bounds.Width;

        if (canvasWidth <= 0) return;

        // 1. Convert pixel x-coordinate to relative timeline track ratio
        double relativeRatio = Math.Clamp(currentPoint.Position.X / canvasWidth, 0.0, 1.0);

        // 2. Map relative track scale directly to raw track duration seconds
        double rawTargetSeconds = relativeRatio * vm.TrackTotalDurationSeconds;

        // 3. Update the temporary drag view position to keep user movement smooth
        _activeTargetMarker.UpdateTransientPosition(rawTargetSeconds);
    }

    private void OnWaveformPointerReleased(object sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingCueMarker && _activeTargetMarker != null && DataContext is CurationWorkstationViewModel vm)
        {
            // 4. Force passing through the mathematical snapping resolution logic
            double finalSnappedTime = vm.SnappingEngine.SnapRawTimeToPhraseLedger(
                _activeTargetMarker.TransientSeconds, 
                vm.TrackBpm, 
                vm.TrackDownbeatAnchorSeconds
            );

            // 5. Commit the final value to the entity layer database record
            vm.CommitCueTimeUpdate(_activeTargetMarker.Id, finalSnappedTime);
        }

        _isDraggingCueMarker = false;
        _activeTargetMarker = null;
        e.Pointer.Capture(null);
    }

    private bool HitTestCueMarkers(double pixelX, out CueMarkerViewModel? matchedMarker)
    {
        matchedMarker = null;
        if (DataContext is not CurationWorkstationViewModel vm) return false;

        double canvasWidth = WaveformTimelineCanvas.Bounds.Width;
        if (canvasWidth <= 0 || vm.TrackTotalDurationSeconds <= 0) return false;

        double hitBufferPixels = 15.0; // 15-pixel interaction envelope

        foreach (var cue in vm.CalculatedCues)
        {
            double cueRatio = cue.TimestampInSeconds / vm.TrackTotalDurationSeconds;
            double cuePixelX = cueRatio * canvasWidth;

            if (Math.Abs(cuePixelX - pixelX) <= hitBufferPixels)
            {
                matchedMarker = cue;
                return true;
            }
        }

        return false;
    }
}
