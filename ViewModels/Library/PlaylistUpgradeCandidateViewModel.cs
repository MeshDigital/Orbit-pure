using System;

namespace SLSKDONET.ViewModels.Library;

public sealed class PlaylistUpgradeCandidateViewModel
{
    public PlaylistTrackViewModel? Track { get; }
    public string ReplacementTrackTitle { get; }
    public string ReplacementTrackArtist { get; }
    public string ReplacementKey { get; }
    public string ReplacementBpm { get; }
    public string ReplacementEnergy { get; }
    public bool IsSavedDoubleAligned { get; }
    public bool IsBridgeCandidate { get; }
    public bool IsReplacementCandidate { get; }
    public string UpgradeReason { get; }
    public double BaseScore { get; }
    public double PriorBonus { get; }
    public double AdjustedScore => BaseScore + PriorBonus;
    public string ScoreLabel => $"Score {(AdjustedScore * 100):0}%";
    public bool HasPriorBonus => PriorBonus > 0.0001;
    public string PriorBonusLabel => $"+{(PriorBonus * 100):0}% prior";
    public string ScoreBreakdownLabel => $"Base {(BaseScore * 100):0}%";

    public PlaylistUpgradeCandidateViewModel(
        PlaylistTrackViewModel? track,
        bool isSavedDoubleAligned = false,
        bool isBridgeCandidate = false,
        bool isReplacementCandidate = false,
        string? upgradeReason = null,
        double baseScore = 0.0,
        double priorBonus = 0.0)
    {
        Track = track;
        ReplacementTrackTitle = track?.TrackTitle ?? "Unknown Title";
        ReplacementTrackArtist = track?.ArtistName ?? "Unknown Artist";
        ReplacementKey = track?.CamelotDisplay ?? "-";
        ReplacementBpm = track?.BpmDisplay ?? "-";
        ReplacementEnergy = track?.EnergyRating ?? "-";
        IsSavedDoubleAligned = isSavedDoubleAligned;
        IsBridgeCandidate = isBridgeCandidate;
        IsReplacementCandidate = isReplacementCandidate;
        BaseScore = Math.Clamp(baseScore, 0.0, 1.0);
        PriorBonus = Math.Clamp(priorBonus, 0.0, 0.2);
        UpgradeReason = string.IsNullOrWhiteSpace(upgradeReason)
            ? "Scaffold candidate"
            : upgradeReason;
    }
}
