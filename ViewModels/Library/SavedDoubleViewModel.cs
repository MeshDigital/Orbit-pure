using System;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.ViewModels.Library;

public sealed class SavedDoubleViewModel
{
    public SavedDouble Model { get; }
    public PlaylistTrackViewModel TrackA { get; }
    public PlaylistTrackViewModel TrackB { get; }
    public string? LeadTrackId { get; set; }

    public DateTime CreatedAt => Model.CreatedAt;
    public double? CachedScore => Model.CachedScore;
    public string? Label => Model.Label;
    public string PairTitle => $"{TrackA.TrackTitle} -> {TrackB.TrackTitle}";
    public string PairSubtitle => $"{TrackA.ArtistName} -> {TrackB.ArtistName}";
    public string CounterpartTrackTitle
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LeadTrackId))
                return TrackB.TrackTitle;

            if (string.Equals(LeadTrackId, Model.TrackAId, StringComparison.Ordinal))
                return TrackB.TrackTitle;

            if (string.Equals(LeadTrackId, Model.TrackBId, StringComparison.Ordinal))
                return TrackA.TrackTitle;

            return PairTitle;
        }
    }

    public string CounterpartArtistName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LeadTrackId))
                return TrackB.ArtistName;

            if (string.Equals(LeadTrackId, Model.TrackAId, StringComparison.Ordinal))
                return TrackB.ArtistName;

            if (string.Equals(LeadTrackId, Model.TrackBId, StringComparison.Ordinal))
                return TrackA.ArtistName;

            return PairSubtitle;
        }
    }

    public string DirectionLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LeadTrackId))
                return "This track -> Other";

            if (string.Equals(LeadTrackId, Model.TrackAId, StringComparison.Ordinal))
                return $"This track -> {TrackB.TrackTitle}";

            if (string.Equals(LeadTrackId, Model.TrackBId, StringComparison.Ordinal))
                return $"{TrackA.TrackTitle} -> This track";

            return PairTitle;
        }
    }

    public SavedDoubleViewModel(SavedDouble model, PlaylistTrackViewModel trackA, PlaylistTrackViewModel trackB)
    {
        Model = model;
        TrackA = trackA;
        TrackB = trackB;
    }

    public static SavedDoubleViewModel? TryCreate(SavedDouble model, Func<string, PlaylistTrackViewModel?> resolver)
    {
        var trackA = resolver(model.TrackAId);
        var trackB = resolver(model.TrackBId);

        if (trackA is null || trackB is null)
            return null;

        return new SavedDoubleViewModel(model, trackA, trackB);
    }
}
