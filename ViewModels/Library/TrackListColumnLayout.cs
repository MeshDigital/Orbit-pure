namespace SLSKDONET.ViewModels.Library;

/// <summary>Which optional track-list columns are shown. <see cref="ArtistColumn"/> false means the
/// artist moves onto a second line under the title instead of disappearing.</summary>
public readonly record struct TrackListColumns(
    bool Energy, bool Format, bool Forensics, bool Duration, bool Rating,
    bool QueueAction = true, bool InsertAction = true, bool ArtistColumn = true, bool Preparation = true);

/// <summary>
/// Fits the track list's columns to the space it has. Hidden columns take no space. When the list
/// is too narrow, secondary information steps aside in this order: Forensics, Preparation, Rating,
/// Duration, Format, Energy, the two hover actions, and finally the separate Artist column (artist
/// moves under Title). Artwork, title (≥ 180 px) and BPM/key always stay. Preparation therefore uses
/// wide-screen space for actionable readiness without making narrow rows taller or clipped.
/// </summary>
public static class TrackListColumnLayout
{
    public const double ArtworkWidth = 60, QueueWidth = 44, InsertWidth = 32, ArtistWidth = 150,
        TitleMinWidth = 180, BpmKeyWidth = 110, ScrollbarAllowance = 16;

    public const double EnergyWidth = 90, FormatWidth = 100, ForensicsWidth = 120, DurationWidth = 80,
        RatingWidth = 84, PreparationWidth = 112;

    /// <summary>The narrowest layout: artwork, title (minimum), BPM/key and the scrollbar.</summary>
    public const double MinimumWidth = ArtworkWidth + TitleMinWidth + BpmKeyWidth + ScrollbarAllowance;

    /// <summary>Everything that shows before any optional data column drops: the minimum layout plus
    /// the queue and smart-insert hover actions and a separate artist column.</summary>
    public const double AlwaysShownWidth = MinimumWidth + QueueWidth + InsertWidth + ArtistWidth;

    /// <summary>The toolbar row above the list (search · status · quality chips · Mix · Columns) is
    /// ~850 px wide in full. Below <see cref="TierChipsMinWidth"/> the three quality chips move into
    /// the Filters menu; below <see cref="StatusSegmentMinWidth"/> the status segment follows, the
    /// search box narrows and the Mix label hides. Unknown width (0) shows the full toolbar.</summary>
    public const double TierChipsMinWidth = 880, StatusSegmentMinWidth = 560;

    public static (bool TierChips, bool StatusSegment) FitToolbar(double availableWidth) =>
        availableWidth <= 0 ? (true, true) : (availableWidth >= TierChipsMinWidth, availableWidth >= StatusSegmentMinWidth);

    public static TrackListColumns Fit(
        double availableWidth,
        bool wantFormat,
        bool wantForensics,
        bool wantDuration,
        double artistWidth = ArtistWidth,
        double energyWidth = EnergyWidth,
        double formatWidth = FormatWidth,
        double forensicsWidth = ForensicsWidth,
        double durationWidth = DurationWidth,
        double ratingWidth = RatingWidth,
        double preparationWidth = PreparationWidth)
    {
        bool energy = true, format = wantFormat, forensics = wantForensics, duration = wantDuration, rating = true;
        bool queue = true, insert = true, artist = true, preparation = true;
        if (availableWidth <= 0) return new(energy, format, forensics, duration, rating, queue, insert, artist, preparation);

        // Account for the user's actual fixed-column widths, not just their defaults. Title remains
        // flexible, so widening a column folds secondary columns instead of overflowing the row.
        double Needed() => MinimumWidth
            + (queue ? QueueWidth : 0) + (insert ? InsertWidth : 0) + (artist ? artistWidth : 0)
            + (energy ? energyWidth : 0) + (format ? formatWidth : 0) + (forensics ? forensicsWidth : 0)
            + (duration ? durationWidth : 0) + (rating ? ratingWidth : 0)
            + (preparation ? preparationWidth : 0);

        if (Needed() > availableWidth) forensics = false;
        if (Needed() > availableWidth) preparation = false;
        if (Needed() > availableWidth) rating = false;
        if (Needed() > availableWidth) duration = false;
        if (Needed() > availableWidth) format = false;
        if (Needed() > availableWidth) energy = false;
        if (Needed() > availableWidth) insert = false;
        if (Needed() > availableWidth) queue = false;
        if (Needed() > availableWidth) artist = false;
        return new(energy, format, forensics, duration, rating, queue, insert, artist, preparation);
    }
}
