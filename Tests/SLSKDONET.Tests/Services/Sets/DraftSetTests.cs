using System;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Copilot;
using SLSKDONET.Services.Sets;
using SLSKDONET.ViewModels.Sets;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.Services.Sets;

/// <summary>#205 P4.5: "Draft a set" builds a spine and opens it as a new Draft set.</summary>
public sealed class DraftSetTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("draft-set");
    private readonly SetProjectService _service;

    public DraftSetTests() => _service = new SetProjectService(_db.Factory);

    public void Dispose() => _db.Dispose();

    private async Task SeedAnalysedAsync(params (string Hash, string Artist, double Bpm, string Key)[] tracks)
    {
        _db.SeedLibrary(tracks.Select(t => (t.Hash, t.Artist, "Title " + t.Hash)).ToArray());
        await using var db = _db.Factory.CreateDbContext();
        foreach (var t in tracks)
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = t.Hash, Bpm = (float)t.Bpm, CamelotKey = t.Key, Energy = 0.7f, TrackDuration = 300 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateDraftSet_StoresTheSpineInOrder_AsADraft_WithItsPreset()
    {
        _db.SeedLibrary(("a", "X", "One"), ("b", "Y", "Two"), ("c", "Z", "Three"));
        var created = await _service.CreateDraftSetAsync("Draft · 174", new[] { "c", "a", "b" }, "peak");

        var summary = Assert.Single(await _service.GetSetsAsync());
        Assert.Equal(SetProjectStatus.Draft, summary.Status);
        Assert.Equal("peak", summary.EventPresetKey);
        Assert.Equal(new[] { "c", "a", "b" }, (await _service.GetSlotsAsync(created.Id)).Select(s => s.TrackUniqueHash));
    }

    [Fact]
    public async Task BuildSet_ToleratesSeveralKnowledgeRowsPerPair_AndKeepsAnOutOfBandSeed()
    {
        await SeedAnalysedAsync(("a", "X", 174, "8A"), ("b", "Y", 175, "8A"), ("c", "Z", 173, "9A"), ("seed", "S", 160, "8A"));
        await using (var db = _db.Factory.CreateDbContext())
        {
            db.TransitionKnowledge.AddRange(
                new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "b", ConfidenceScore = 0.4 },
                new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "b", TransitionType = KnowledgeTransitionType.Cut, ConfidenceScore = 0.9 });
            await db.SaveChangesAsync();
        }
        var copilot = new OrbitCopilot(_db.Factory);

        var built = await copilot.BuildSetAsync(new SetBuildRequest(15, null, 0.06, TargetBpm: 174));
        Assert.Equal(3, built.SpineTrackHashes.Count);
        Assert.Equal(15, built.DurationMinutes, 6);

        var seeded = await copilot.BuildSetAsync(new SetBuildRequest(15, null, 0.06, SeedTrackHash: "seed", TargetBpm: 174));
        Assert.Equal("seed", seeded.SpineTrackHashes[0]);
    }

    [Fact]
    public async Task DraftSet_OpensANewDraft_AndLeavesTheCurrentSetAlone()
    {
        await SeedAnalysedAsync(("a", "X", 174, "8A"), ("b", "Y", 175, "8A"), ("c", "Z", 173, "9A"), ("d", "W", 128, "8A"));
        var existing = await _service.CreateSetAsync("Friday");
        await _service.AppendTrackAsync(existing.Id, "a");

        var vm = new SetsViewModel(_service, new Mock<IDialogService>().Object, new Mock<INotificationService>().Object,
            copilot: new OrbitCopilot(_db.Factory));
        await vm.LoadAsync();
        vm.SelectedSet = vm.Sets.Single();
        await vm.PendingSlotLoad;
        Assert.True(vm.CanDraftSet);

        vm.PrepareDraft();
        Assert.Equal(174m, vm.DraftBpm); // prefilled from the set
        vm.DraftMinutes = 15;
        await vm.DraftSetAsync();
        await vm.PendingSlotLoad;

        Assert.Equal(2, vm.Sets.Count);
        Assert.StartsWith("Draft · 174 BPM · 15 min", vm.SelectedSet!.Name);
        Assert.Equal(3, vm.Slots.Count); // the 128 BPM track stays out
        Assert.DoesNotContain(vm.Slots, s => s.Slot.TrackUniqueHash == "d");
        Assert.Equal("Drafted 3 tracks, 15 min.", vm.DraftStatus);
        Assert.Single(await _service.GetSlotsAsync(existing.Id)); // untouched
    }

    [Fact]
    public async Task DraftSet_WithoutEnoughTracks_SaysWhy_AndCreatesNothing()
    {
        await SeedAnalysedAsync(("a", "X", 174, "8A"));
        var vm = new SetsViewModel(_service, new Mock<IDialogService>().Object, new Mock<INotificationService>().Object,
            copilot: new OrbitCopilot(_db.Factory));
        await vm.LoadAsync();

        vm.DraftBpm = 140;
        await vm.DraftSetAsync();

        Assert.Equal("Not enough analysed tracks within 6 % of 140 BPM to draft a set.", vm.DraftStatus);
        Assert.Empty(await _service.GetSetsAsync());
    }
}
