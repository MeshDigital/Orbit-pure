using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Repositories;
using SLSKDONET.Services.Sets;
using SLSKDONET.Services.Transitions;
using Xunit;

namespace SLSKDONET.Tests.Services.Transitions;

/// <summary>
/// Covers Epic #517 Phase 3 — #529 (FO3a: Mix vs Play per-queue mode) and
/// #530 (FO3b: transition labels from resolver results + "Use Auto instead" container override).
/// </summary>
[Collection(NonParallelCollection.Name)]
public class MixModeAndTransitionLabelsTests
{
    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new AppDbContext();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext());
    }

    [Fact]
    public void Formatter_AutoPhraseAligned_ReturnsExpectedLabel()
    {
        var grid = new[] { 0.0, 0.345, 0.69 };
        var plan = new TransitionPlan(PlannedTransitionKind.Rolling, 180, 16, 16, 22.0, false, "Rolling · 16 bars");
        var resolved = new ResolvedTransition(
            ResolvedTransitionSource.Auto,
            "Auto",
            null,
            null,
            false,
            plan,
            new TrackStructure { Bpm = 174, DurationSeconds = 240, BeatGrid = grid },
            new TrackStructure { Bpm = 174, DurationSeconds = 240, BeatGrid = grid });

        var label = TransitionLabelFormatter.Format(resolved);
        Assert.Equal("Auto · phrase-aligned", label.Text);
        Assert.False(label.IsFromEarlierMix);
    }

    [Fact]
    public void Formatter_AutoTempoJumpAboveSixPercent_ReturnsEchoOutTempoJump()
    {
        var grid = new[] { 0.0, 0.468, 0.937 };
        var resolved = new ResolvedTransition(
            ResolvedTransitionSource.Auto,
            "Auto",
            null,
            null,
            false,
            null,
            new TrackStructure { Bpm = 128, DurationSeconds = 240, BeatGrid = grid },
            new TrackStructure { Bpm = 174, DurationSeconds = 240, BeatGrid = grid });

        var label = TransitionLabelFormatter.Format(resolved);
        Assert.Equal("Auto · echo-out · tempo jump", label.Text);
        Assert.False(label.IsFromEarlierMix);
    }

    [Fact]
    public void Formatter_ContainerSavedWithCuePoints_ReturnsYoursCueToCue()
    {
        var saved = new PlaylistTrackTransition
        {
            PresetName = "EqSwap",
            Type = TransitionType.EqSwap,
            SourceTriggerSeconds = 180.0,
            TargetTriggerSeconds = 16.0,
        };
        var resolved = new ResolvedTransition(
            ResolvedTransitionSource.Container,
            "EqSwap",
            saved,
            null,
            false,
            null,
            null,
            null);

        var label = TransitionLabelFormatter.Format(resolved);
        Assert.Equal("Yours · cue to cue", label.Text);
        Assert.False(label.IsFromEarlierMix);
    }

    [Fact]
    public void Formatter_PairDefaultFromEarlierMix_ReturnsFromYourEarlierMix()
    {
        var saved = new PlaylistTrackTransition
        {
            PresetName = "EchoOut",
            Type = TransitionType.EchoOut,
        };
        var resolved = new ResolvedTransition(
            ResolvedTransitionSource.PairDefault,
            "EchoOut",
            saved,
            EarlierMixSourceName: "Friday Night DnB",
            EarlierMixIsSet: false,
            AutoPlan: null,
            OutgoingStructure: null,
            IncomingStructure: null);

        var label = TransitionLabelFormatter.Format(resolved);
        Assert.Equal("From your earlier mix", label.Text);
        Assert.True(label.IsFromEarlierMix);
        Assert.Equal("Friday Night DnB", label.EarlierMixSourceName);
    }

    [Fact]
    public async Task UseAutoInstead_SavesContainerAutoRow_WithoutTouchingEarlierMix()
    {
        using var scope = new FreshDatabaseScope();
        var factory = new TestDbContextFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var playlistA = Guid.NewGuid();
        var playlistB = Guid.NewGuid();
        var playlistC = Guid.NewGuid();
        var aOut = Guid.NewGuid();
        var aIn = Guid.NewGuid();
        var bOut = Guid.NewGuid();
        var bIn = Guid.NewGuid();
        var cOut = Guid.NewGuid();
        var cIn = Guid.NewGuid();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PlaylistTracks.AddRange(
                new PlaylistTrackEntity { Id = aOut, PlaylistId = playlistA, TrackUniqueHash = "hash-1", Title = "One" },
                new PlaylistTrackEntity { Id = aIn, PlaylistId = playlistA, TrackUniqueHash = "hash-2", Title = "Two" },
                new PlaylistTrackEntity { Id = bOut, PlaylistId = playlistB, TrackUniqueHash = "hash-1", Title = "One" },
                new PlaylistTrackEntity { Id = bIn, PlaylistId = playlistB, TrackUniqueHash = "hash-2", Title = "Two" },
                new PlaylistTrackEntity { Id = cOut, PlaylistId = playlistC, TrackUniqueHash = "hash-1", Title = "One" },
                new PlaylistTrackEntity { Id = cIn, PlaylistId = playlistC, TrackUniqueHash = "hash-2", Title = "Two" });
            await db.SaveChangesAsync();
        }

        var repo = new TransitionRepository(NullLogger<TransitionRepository>.Instance);
        var history = new TransitionHistoryService(factory);
        var resolver = new TransitionResolver(factory, history, NullLogger<TransitionResolver>.Instance);

        // Save a shared pair transition in Playlist A
        await repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = playlistA,
            OutgoingPlaylistTrackId = aOut,
            IncomingPlaylistTrackId = aIn,
            PresetName = "EchoOut",
            Type = TransitionType.EchoOut,
            UseAsPairDefault = true,
        });

        // Playlist B initially gets Step 2 ("From your earlier mix")
        var before = await resolver.ResolveAsync(TransitionScopeKind.Playlist, playlistB, bOut, bIn, "hash-1", "hash-2");
        Assert.Equal("From your earlier mix", TransitionLabelFormatter.Format(before).Text);

        // User clicks "Use Auto instead" in Playlist B (container Auto row with UseAsPairDefault = false)
        await repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = playlistB,
            OutgoingPlaylistTrackId = bOut,
            IncomingPlaylistTrackId = bIn,
            PresetName = "Auto",
            Type = TransitionType.Crossfade,
            UseAsPairDefault = false,
        });

        // Playlist B now resolves to Auto label (not "From your earlier mix")
        var afterB = await resolver.ResolveAsync(TransitionScopeKind.Playlist, playlistB, bOut, bIn, "hash-1", "hash-2");
        var labelB = TransitionLabelFormatter.Format(afterB);
        Assert.Equal("Auto · phrase-aligned", labelB.Text);
        Assert.False(labelB.IsFromEarlierMix);

        // Playlist C still gets Playlist A's shared transition ("From your earlier mix")
        var afterC = await resolver.ResolveAsync(TransitionScopeKind.Playlist, playlistC, cOut, cIn, "hash-1", "hash-2");
        Assert.Equal("From your earlier mix", TransitionLabelFormatter.Format(afterC).Text);
    }
}
