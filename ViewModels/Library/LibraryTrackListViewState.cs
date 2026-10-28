using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// The user's view of one Library playlist. This is presentation state only: no track, cue,
/// playlist-order, or download data is duplicated here. Title remains a flexible star column;
/// only the fixed-width columns are remembered so a saved width cannot make narrow layouts rigid.
/// </summary>
public sealed class LibraryTrackListViewState
{
    public string SearchText { get; set; } = string.Empty;
    public string FilterArtist { get; set; } = string.Empty;
    public string FilterTitle { get; set; } = string.Empty;

    public bool IsFilterAll { get; set; } = true;
    public bool IsFilterDownloaded { get; set; }
    public bool IsFilterPending { get; set; }
    public bool IsFilterNeedsReview { get; set; }
    public bool IsFilterLiked { get; set; }

    public string? CamelotKeyFilter { get; set; }
    public string? QualityTierFilter { get; set; }
    public bool IsBouncerActive { get; set; }
    public string? VibeFilter { get; set; }
    public bool IsFilterFlac { get; set; }
    public bool IsFilterMp3 { get; set; }
    public bool IsFilterWav { get; set; }
    public bool IsFilterLossless { get; set; }
    public bool IsFilterQualityGold { get; set; }
    public bool IsFilterQualityVerified { get; set; }
    public bool IsFilterQualityReview { get; set; }
    public List<string> SelectedStyles { get; set; } = new();
    public List<string> DuplicateHashes { get; set; } = new();

    public TrackSortColumn SortColumn { get; set; } = TrackSortColumn.Default;
    public bool SortDescending { get; set; }

    public bool IsColumnFilterStripVisible { get; set; }
    public bool IsFormatColumnVisible { get; set; } = true;
    public bool IsForensicsColumnVisible { get; set; } = true;
    public bool IsDurationColumnVisible { get; set; } = true;

    public double ArtistColumnWidth { get; set; } = TrackListColumnLayout.ArtistWidth;
    public double EnergyColumnWidth { get; set; } = TrackListColumnLayout.EnergyWidth;
    public double FormatColumnWidth { get; set; } = TrackListColumnLayout.FormatWidth;
    public double ForensicsColumnWidth { get; set; } = TrackListColumnLayout.ForensicsWidth;
    public double DurationColumnWidth { get; set; } = TrackListColumnLayout.DurationWidth;
    public double RatingColumnWidth { get; set; } = TrackListColumnLayout.RatingWidth;
    public double PreparationColumnWidth { get; set; } = TrackListColumnLayout.PreparationWidth;

    public double ScrollOffset { get; set; }

    /// <summary>Repairs partial/old/corrupt state at the persistence boundary.</summary>
    public void Normalize()
    {
        SearchText ??= string.Empty;
        FilterArtist ??= string.Empty;
        FilterTitle ??= string.Empty;
        SelectedStyles ??= new List<string>();
        DuplicateHashes ??= new List<string>();
        SelectedStyles = SelectedStyles.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        DuplicateHashes = DuplicateHashes.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();

        if (!Enum.IsDefined(typeof(TrackSortColumn), SortColumn)) SortColumn = TrackSortColumn.Default;

        ArtistColumnWidth = FiniteOr(ArtistColumnWidth, TrackListColumnLayout.ArtistWidth, 90, 320);
        EnergyColumnWidth = FiniteOr(EnergyColumnWidth, TrackListColumnLayout.EnergyWidth, 60, 160);
        FormatColumnWidth = FiniteOr(FormatColumnWidth, TrackListColumnLayout.FormatWidth, 80, 180);
        ForensicsColumnWidth = FiniteOr(ForensicsColumnWidth, TrackListColumnLayout.ForensicsWidth, 90, 220);
        DurationColumnWidth = FiniteOr(DurationColumnWidth, TrackListColumnLayout.DurationWidth, 64, 140);
        RatingColumnWidth = FiniteOr(RatingColumnWidth, TrackListColumnLayout.RatingWidth, 64, 140);
        PreparationColumnWidth = FiniteOr(PreparationColumnWidth, TrackListColumnLayout.PreparationWidth, 96, 180);
        ScrollOffset = double.IsFinite(ScrollOffset) ? Math.Max(0, ScrollOffset) : 0;
    }

    private static double FiniteOr(double value, double fallback, double min, double max) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(value, min, max) : fallback;
}
