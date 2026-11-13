using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Sets;
using Xunit;

namespace SLSKDONET.Tests.Services.Sets;

/// <summary>Saved set transitions (P3.2): own table, same model as playlist transitions.</summary>
public sealed class SetTransitionRepositoryTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("set-transitions");
    private SetProjectService Sets => new(_db.Factory);
    private SetTransitionRepository Repo => new(_db.Factory);

    public void Dispose() => _db.Dispose();

    private static PlaylistTrackTransition Transition(Guid setId, Guid from, Guid to, string preset = "Blend") => new()
    {
        PlaylistId = setId,
        OutgoingPlaylistTrackId = from,
        IncomingPlaylistTrackId = to,
        PresetName = preset,
        Type = TransitionType.Crossfade,
        DurationBars = 32,
        EqSwapLow = true,
        SourceTriggerSeconds = 180.5,
        TargetTriggerSeconds = 12.25,
    };

    [Fact]
    public async Task Upsert_Get_Update_Delete_RoundTrip()
    {
        var set = await Sets.CreateSetAsync("Friday");
        var a = await Sets.AppendTrackAsync(set.Id, "a");
        var b = await Sets.AppendTrackAsync(set.Id, "b");

        await Repo.UpsertTransitionAsync(Transition(set.Id, a!.Id, b!.Id));
        var saved = await Repo.GetTransitionAsync(a.Id, b.Id);
        Assert.Equal(("Blend", 32, true, 180.5, 12.25), (saved!.PresetName, saved.DurationBars, saved.EqSwapLow, saved.SourceTriggerSeconds, saved.TargetTriggerSeconds));
        Assert.Equal(set.Id, saved.PlaylistId);

        await Repo.UpsertTransitionAsync(Transition(set.Id, a.Id, b.Id, preset: "Wave"));
        Assert.Equal("Wave", (await Repo.GetTransitionAsync(a.Id, b.Id))!.PresetName);
        Assert.Single(await Repo.GetTransitionsForPlaylistAsync(set.Id));

        await Repo.DeleteTransitionAsync(a.Id, b.Id);
        Assert.Null(await Repo.GetTransitionAsync(a.Id, b.Id));
    }

    [Fact]
    public async Task RemovingASlot_DropsItsTransitions_AndUndoRestoresThem()
    {
        var set = await Sets.CreateSetAsync("Friday");
        var a = await Sets.AppendTrackAsync(set.Id, "a");
        var b = await Sets.AppendTrackAsync(set.Id, "b");
        var c = await Sets.AppendTrackAsync(set.Id, "c");
        await Repo.UpsertTransitionAsync(Transition(set.Id, a!.Id, b!.Id));
        await Repo.UpsertTransitionAsync(Transition(set.Id, b.Id, c!.Id, preset: "Melt"));

        var snapshot = await Sets.RemoveSlotAsync(b.Id);
        Assert.Empty(await Repo.GetTransitionsForPlaylistAsync(set.Id));

        await Sets.RestoreSlotAsync(snapshot!);
        Assert.Equal("Melt", (await Repo.GetTransitionAsync(b.Id, c.Id))!.PresetName);
        Assert.NotNull(await Repo.GetTransitionAsync(a.Id, b.Id));
    }

    [Fact]
    public async Task Duplicate_CopiesTransitionsOntoTheNewSlots_DeleteClearsThem()
    {
        var set = await Sets.CreateSetAsync("Friday");
        var a = await Sets.AppendTrackAsync(set.Id, "a");
        var b = await Sets.AppendTrackAsync(set.Id, "b");
        await Repo.UpsertTransitionAsync(Transition(set.Id, a!.Id, b!.Id));

        var copy = await Sets.DuplicateSetAsync(set.Id);
        var copiedSlots = await Sets.GetSlotsAsync(copy!.Id);
        var copied = await Repo.GetTransitionAsync(copiedSlots[0].SlotId, copiedSlots[1].SlotId);
        Assert.Equal(("Blend", copy.Id), (copied!.PresetName, copied.PlaylistId));

        await Sets.DeleteSetAsync(set.Id);
        using var db = _db.Factory.CreateDbContext();
        Assert.Equal(1, await db.SetTrackTransitions.CountAsync());
    }
}
