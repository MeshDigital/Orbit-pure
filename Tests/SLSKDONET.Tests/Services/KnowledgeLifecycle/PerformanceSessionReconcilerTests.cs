using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

public class PerformanceSessionReconcilerTests
{
    private static PerformanceSessionTrackEntity Track(string id, int order, bool audible, double? crowd = null, int? type = null, Guid? plannedSlotId = null)
        => new() { TrackId = id, SequenceOrder = order, AudibleChange = audible, CrowdReaction = crowd, TransitionType = type, PlannedSlotId = plannedSlotId };

    [Fact]
    public void ExtractAudibleTransitions_OnlyAudibleChanges()
    {
        var tracks = new[]
        {
            Track("A", 0, audible: false),
            Track("B", 1, audible: false),
            Track("X", 2, audible: true, crowd: 85.0),   // live audible B -> X
            Track("C", 3, audible: false),
            Track("Y", 4, audible: true, crowd: 70.0),   // live audible C -> Y
        };

        var result = PerformanceSessionReconciler.ExtractAudibleTransitions(tracks);

        Assert.Equal(2, result.Count);
        Assert.Equal(("B", "X"), (result[0].SourceTrackId, result[0].TargetTrackId));
        Assert.Equal(("C", "Y"), (result[1].SourceTrackId, result[1].TargetTrackId));
        Assert.Equal(85.0, result[0].CrowdReaction);
    }

    [Fact]
    public void ExtractAudibleTransitions_PivotSkipsPlannedTracks()
    {
        // A was played, then B/C/D were skipped (never in the sequence), then the DJ pivoted to X.
        // Only the A -> X audible should be extracted.
        var tracks = new[]
        {
            Track("A", 0, audible: false),
            Track("X", 1, audible: true, crowd: 90.0),
        };

        var result = PerformanceSessionReconciler.ExtractAudibleTransitions(tracks);

        var single = Assert.Single(result);
        Assert.Equal("A", single.SourceTrackId);
        Assert.Equal("X", single.TargetTrackId);
    }

    [Fact]
    public void FindSkippedPlannedTracks_ReturnsUnplayedSlots()
    {
        var planned = new[]
        {
            new SetTrackEntity { Id = Guid.NewGuid(), TrackUniqueHash = "A", Position = 0 },
            new SetTrackEntity { Id = Guid.NewGuid(), TrackUniqueHash = "B", Position = 1 },
            new SetTrackEntity { Id = Guid.NewGuid(), TrackUniqueHash = "C", Position = 2 },
        };
        var actual = new[]
        {
            Track("A", 0, audible: false, plannedSlotId: planned[0].Id),
            Track("X", 1, audible: true, plannedSlotId: null), // audible, not planned
        };

        var skipped = PerformanceSessionReconciler.FindSkippedPlannedTracks(planned, actual);

        Assert.Equal(new[] { "B", "C" }, skipped);
    }
}
