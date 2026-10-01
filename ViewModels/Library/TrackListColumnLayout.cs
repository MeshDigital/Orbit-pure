namespace SLSKDONET.ViewModels.Library;

/// <summary>Which optional track-list columns are shown.</summary>
public readonly record struct TrackListColumns(bool Energy, bool Format, bool Forensics, bool Duration, bool Rating);

/// <summary>
/// Fits the track list's columns to the space it has. The columns used to be fixed widths
/// (~1,020 px in total) with horizontal scrolling off, so opening the context panel on a laptop
/// screen simply cut off Format and everything right of it — and hiding a column via "Columns"
/// hid its content but kept its width. Now hidden columns take no space, and when the list is
/// narrower than its columns the least important ones step aside, in this order: Forensics,
/// Rating, Duration, Format, Energy. Artwork, queue, artist, title (≥150 px) and BPM/key always stay.
/// </summary>
public static class TrackListColumnLayout
{
    public const double EnergyWidth = 90, FormatWidth = 100, ForensicsWidth = 120, DurationWidth = 80, RatingWidth = 84;

    /// <summary>Artwork 60 + queue 44 + insert 32 + artist 150 + title (minimum) 150 + BPM/key 110,
    /// plus room for the vertical scrollbar.</summary>
    public const double AlwaysShownWidth = 60 + 44 + 32 + 150 + 150 + 110 + 16;

    public static TrackListColumns Fit(double availableWidth, bool wantFormat, bool wantForensics, bool wantDuration)
    {
        bool energy = true, format = wantFormat, forensics = wantForensics, duration = wantDuration, rating = true;
        if (availableWidth <= 0) return new(energy, format, forensics, duration, rating);

        double Needed() => AlwaysShownWidth
            + (energy ? EnergyWidth : 0) + (format ? FormatWidth : 0) + (forensics ? ForensicsWidth : 0)
            + (duration ? DurationWidth : 0) + (rating ? RatingWidth : 0);

        if (Needed() > availableWidth) forensics = false;
        if (Needed() > availableWidth) rating = false;
        if (Needed() > availableWidth) duration = false;
        if (Needed() > availableWidth) format = false;
        if (Needed() > availableWidth) energy = false;
        return new(energy, format, forensics, duration, rating);
    }
}
