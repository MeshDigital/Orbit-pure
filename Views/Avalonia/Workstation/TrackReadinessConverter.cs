using System;
using System.Globalization;
using Avalonia.Data.Converters;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Workstation;

namespace SLSKDONET.Views.Avalonia.Workstation;

/// <summary>Compact, row-local readiness text and visibility for the Prep track table.</summary>
public sealed class TrackReadinessConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var mode = parameter as string ?? "Status";
        if (value is not PlaylistTrack track)
        {
            return mode is "IsReady" or "NeedsRepair" ? false : string.Empty;
        }

        var issue = WorkstationDeckViewModel.GetTrackEligibilityIssue(track);
        return mode switch
        {
            "IsReady" => issue == WorkstationTrackEligibilityIssue.None,
            "NeedsRepair" => issue != WorkstationTrackEligibilityIssue.None,
            "Action" => issue switch
            {
                WorkstationTrackEligibilityIssue.NotDownloaded => "Acquire",
                WorkstationTrackEligibilityIssue.MissingFile => "Locate",
                WorkstationTrackEligibilityIssue.MissingHash => "Import",
                WorkstationTrackEligibilityIssue.NoTrack => "Library",
                WorkstationTrackEligibilityIssue.None => "Load",
                _ => "Analyze",
            },
            "Hint" => WorkstationDeckViewModel.GetTrackLoadReadinessMessage(track) ?? "Ready to load into a deck.",
            _ => issue switch
            {
                WorkstationTrackEligibilityIssue.None => "Ready",
                WorkstationTrackEligibilityIssue.NotDownloaded => "Not downloaded",
                WorkstationTrackEligibilityIssue.MissingFile => "File missing",
                WorkstationTrackEligibilityIssue.MissingHash => "Needs import",
                WorkstationTrackEligibilityIssue.MissingWaveform => "Waveform missing",
                WorkstationTrackEligibilityIssue.MissingCues => "Cues missing",
                WorkstationTrackEligibilityIssue.MissingAnalysis => "Analysis missing",
                WorkstationTrackEligibilityIssue.NoTrack => "Unavailable",
                _ => "Needs attention",
            },
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
