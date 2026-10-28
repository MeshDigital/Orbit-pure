namespace SLSKDONET.ViewModels.Library;

/// <summary>Which optional track-list columns are shown. <see cref="ArtistColumn"/> false means the
/// artist moves onto a second line under the title instead of disappearing.</summary>
public readonly record struct TrackListColumns(
    bool Energy, bool Format, bool Forensics, bool Duration, bool Rating,
    bool QueueAction = true, bool InsertAction = true, bool ArtistColumn = true);

/// <summary>
/// Fits the track list's columns to the space it has. The columns used to be fixed widths
/// (~1,020 px in total) with horizontal scrolling off, so opening the context panel on a laptop
/// screen simply cut off Format and everything right of it — and hiding a column via "Columns"
/// hid its content but kept its width. Hidden columns now take no space, and when the list is
/// narrower than its columns the least important ones step aside, in this order: Forensics,
/// Rating, Duration, Format, Energy, then the two hover actions (smart insert, queue), and finally
/// the artist column folds onto a second line under the title. Artwork, title (≥ 180 px) and
/// BPM/key always stay, so the title can never collapse to nothing again (it did at ~320 px with
/// both side panels open).
/// </summary>
public static class TrackListColumnLayout
{
    public const double ArtworkWidth = 60, QueueWidth = 44, InsertWidth = 32, ArtistWidth = 150,
        TitleMinWidth = 180, BpmKeyWidth = 110, ScrollbarAllowance = 16;

    public const double EnergyWidth = 90, FormatWidth = 100, ForensicsWidth = 120, DurationWidth = 80, RatingWidth = 84;

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

    public static TrackListColumns Fit(double availableWidth, bool wantFormat, bool wantForensics, bool wantDuration)
    {
        bool energy = true, format = wantFormat, forensics = wantForensics, duration = wantDuration, rating = true;
        bool queue = true, insert = true, artist = true;
        if (availableWidth <= 0) return new(energy, format, forensics, duration, rating, queue, insert, artist);

        double Needed() => MinimumWidth
            + (queue ? QueueWidth : 0) + (insert ? InsertWidth : 0) + (artist ? ArtistWidth : 0)
            + (energy ? EnergyWidth : 0) + (format ? FormatWidth : 0) + (forensics ? ForensicsWidth : 0)
            + (duration ? DurationWidth : 0) + (rating ? RatingWidth : 0);

        if (Needed() > availableWidth) forensics = false;
        if (Needed() > availableWidth) rating = false;
        if (Needed() > availableWidth) duration = false;
        if (Needed() > availableWidth) format = false;
        if (Needed() > availableWidth) energy = false;
        if (Needed() > availableWidth) insert = false;
        if (Needed() > availableWidth) queue = false;
        if (Needed() > availableWidth) artist = false;
        return new(energy, format, forensics, duration, rating, queue, insert, artist);
    }
}
