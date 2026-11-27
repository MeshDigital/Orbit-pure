using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Repositories;
using SLSKDONET.Services.Transitions;
using Xunit;

namespace SLSKDONET.Tests.Services.Transitions;

/// <summary>
/// The playback mix decision moved out of PlayerViewModel.AttachSavedTransitionAsync so MixBenchmark
/// (#599) can score exactly what playback plays. These pin its order: saved → echo-out fallback → plan.
/// </summary>
public sealed class PlaybackTransitionDeciderTests
{
    private static readonly PlaybackPairTrack Out = new(Guid.NewGuid(), Guid.NewGuid(), "out-hash", "8A", 0.7, 174);
    private static readonly PlaybackPairTrack In = new(Guid.NewGuid(), Guid.NewGuid(), "in-hash", "8A", 0.7, 174);

    [Fact]
    public async Task SavedTransition_WinsWithItsOwnMixPoints()
    {
        var saved = new PlaylistTrackTransition { PresetName = "Echo Out", Type = TransitionType.EchoOut, DurationBars = 8, SourceTriggerSeconds = 200, TargetTriggerSeconds = 12 };
        var sut = new PlaybackTransitionDecider(new FakeResolver(ResolvedTransitionSource.Container, saved, Planned(174, 174)));

        var d = await sut.DecideAsync(Out, In);

        Assert.Equal(PlaybackTransitionPath.Saved, d.Path);
        Assert.Equal("Echo Out", d.PresetName);
        Assert.Equal(200, d.SourceTriggerSeconds);
        Assert.Equal(12, d.TargetTriggerSeconds);
        Assert.Equal(ResolvedTransitionSource.Container, d.ResolvedSource);
        Assert.Equal(174, d.IncomingBpm); // analysed BPMs from the plan, for tempo matching
    }

    [Fact]
    public async Task AnalysedPairWithinTempo_PlaysThePlan()
    {
        var planned = Planned(174, 175);
        var sut = new PlaybackTransitionDecider(new FakeResolver(ResolvedTransitionSource.Auto, null, planned));

        var d = await sut.DecideAsync(Out, In);

        Assert.Equal(PlaybackTransitionPath.Planned, d.Path);
        Assert.Equal(planned.Plan.PresetName, d.PresetName);
        Assert.Equal(planned.Plan.SourceTriggerSeconds, d.SourceTriggerSeconds);
        Assert.Equal(planned.Plan.TargetTriggerSeconds, d.TargetTriggerSeconds);
        Assert.Equal(175, d.IncomingBpm);
        Assert.Equal(174, d.OutgoingBpm);
    }

    [Fact]
    public async Task NoAnalysis_EchoOutWithoutMixPoints()
    {
        var sut = new PlaybackTransitionDecider(new FakeResolver(ResolvedTransitionSource.Auto, null, null));

        var d = await sut.DecideAsync(Out, In with { FileBpm = null });

        Assert.Equal(PlaybackTransitionPath.EchoOutFallback, d.Path);
        Assert.Equal("EchoOut", d.PresetName);
        Assert.Equal(TransitionType.EchoOut, d.Model.Type);
        Assert.Null(d.SourceTriggerSeconds); // the player starts it near the file's end
        Assert.Null(d.TargetTriggerSeconds);
        Assert.Equal(128, d.IncomingBpm); // no file or analysed BPM
    }

    [Fact]
    public async Task TempoJumpOverSixPercent_EchoOutKeepsThePlannedPoints()
    {
        var planned = Planned(174, 140);
        var sut = new PlaybackTransitionDecider(new FakeResolver(ResolvedTransitionSource.Auto, null, planned));

        var d = await sut.DecideAsync(Out, In);

        Assert.Equal(PlaybackTransitionPath.EchoOutFallback, d.Path);
        Assert.Equal(planned.Plan.SourceTriggerSeconds, d.SourceTriggerSeconds);
    }

    [Fact]
    public async Task WithoutResolver_UsesTheStoreAndThePlanService()
    {
        var saved = new PlaylistTrackTransition { PresetName = "Cut", Type = TransitionType.Cut };
        var sut = new PlaybackTransitionDecider(null, new FakeStore(saved));

        var d = await sut.DecideAsync(Out, In);

        Assert.True(sut.CanDecide);
        Assert.Equal(PlaybackTransitionPath.Saved, d.Path);
        Assert.Null(d.ResolvedSource);
    }

    [Fact]
    public void NothingToDecideWith_CanDecideIsFalse() =>
        Assert.False(new PlaybackTransitionDecider(null).CanDecide);

    private static (TransitionPlan Plan, TrackStructure Outgoing, TrackStructure Incoming) Planned(double outBpm, double inBpm)
    {
        var outgoing = Structure(outBpm);
        var incoming = Structure(inBpm);
        return (TransitionPlanner.Plan(outgoing, incoming, 80), outgoing, incoming);
    }

    private static TrackStructure Structure(double bpm)
    {
        double beat = 60.0 / bpm;
        return new TrackStructure
        {
            DurationSeconds = 360,
            Bpm = bpm,
            FirstDownbeat = 0.5,
            Drop1 = 0.5 + 64 * 4 * beat,
            Drop2 = 0.5 + 160 * 4 * beat,
            Energy = 0.7,
            BeatGrid = Enumerable.Range(0, (int)(359 / beat)).Select(i => 0.5 + i * beat).ToList(),
        };
    }

    private sealed class FakeResolver : ITransitionResolver
    {
        private readonly ResolvedTransition _result;

        public FakeResolver(ResolvedTransitionSource source, PlaylistTrackTransition? saved,
            (TransitionPlan Plan, TrackStructure Outgoing, TrackStructure Incoming)? planned) =>
            _result = new ResolvedTransition(source, saved?.PresetName ?? "Auto", saved, null, false,
                planned?.Plan, planned?.Outgoing, planned?.Incoming);

        public Task<ResolvedTransition> ResolveAsync(TransitionScopeKind scopeKind, Guid scopeId, Guid outgoingItemId, Guid incomingItemId,
            string? outgoingTrackHash, string? incomingTrackHash, double compatibilityScore = 0.75,
            ITransitionRepository? storeOverride = null, CancellationToken ct = default) => Task.FromResult(_result);
    }

    private sealed class FakeStore : ITransitionRepository
    {
        private readonly PlaylistTrackTransition _saved;
        public FakeStore(PlaylistTrackTransition saved) => _saved = saved;
        public Task<PlaylistTrackTransition?> GetTransitionAsync(Guid outgoingPlaylistTrackId, Guid incomingPlaylistTrackId) =>
            Task.FromResult<PlaylistTrackTransition?>(_saved);
        public Task<System.Collections.Generic.List<PlaylistTrackTransition>> GetTransitionsForPlaylistAsync(Guid playlistId) => throw new NotSupportedException();
        public Task UpsertTransitionAsync(PlaylistTrackTransition transition) => throw new NotSupportedException();
        public Task DeleteTransitionAsync(Guid outgoingPlaylistTrackId, Guid incomingPlaylistTrackId) => throw new NotSupportedException();
    }
}
