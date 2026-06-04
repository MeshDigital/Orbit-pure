namespace SLSKDONET.ViewModels.Library;

public sealed class SuggestNextCandidateViewModel
{
    public PlaylistTrackViewModel? Track { get; }
    public bool IsSavedDoubleSuggested { get; }
    public double BaseScore { get; }
    public double Bonus { get; }

    public string TrackTitle => Track?.TrackTitle ?? "Unknown Title";
    public string ArtistName => Track?.ArtistName ?? "Unknown Artist";
    public string CamelotDisplay => Track?.CamelotDisplay ?? "-";
    public string BpmDisplay => Track?.BpmDisplay ?? "-";
    public string EnergyRating => Track?.EnergyRating ?? "-";
    public string BaseScoreDisplay => $"{BaseScore * 100:0}%";
    public bool HasSuggestionReason => IsSavedDoubleSuggested;
    public string SuggestionReason => IsSavedDoubleSuggested
        ? "Boosted by your saved double history"
        : string.Empty;

    public SuggestNextCandidateViewModel(
        PlaylistTrackViewModel? track,
        double baseScore = 0.0,
        double bonus = 0.0,
        bool isSavedDoubleSuggested = false)
    {
        Track = track;
        BaseScore = baseScore;
        Bonus = bonus;
        IsSavedDoubleSuggested = isSavedDoubleSuggested;
    }
}
