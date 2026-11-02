using System;
using System.Threading.Tasks;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Sets;
using Xunit;

namespace SLSKDONET.Tests.Services.Sets;

/// <summary>A pair met again starts from the transition saved for it last time (P3.4).</summary>
public sealed class TransitionHistoryServiceTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("transition-history");
    private SetProjectService Sets => new(_db.Factory);
    private SetTransitionRepository Repo => new(_db.Factory);
    private TransitionHistoryService History => new(_db.Factory);

    public void Dispose() => _db.Dispose();

    private async Task<(Guid SetId, Guid A, Guid B)> SetWithPairAsync(string name, string fromHash, string toHash)
    {
        var set = await Sets.CreateSetAsync(name);
        var a = await Sets.AppendTrackAsync(set.Id, fromHash);
        var b = await Sets.AppendTrackAsync(set.Id, toHash);
        return (set.Id, a!.Id, b!.Id);
    }

    private Task SaveAsync(Guid setId, Guid a, Guid b, string preset)
        => Repo.UpsertTransitionAsync(new PlaylistTrackTransition
        {
            PlaylistId = setId, OutgoingPlaylistTrackId = a, IncomingPlaylistTrackId = b, PresetName = preset,
        });

    [Fact]
    public async Task FindsTheNewestTransitionForThePair_InAnotherSet()
    {
        var older = await SetWithPairAsync("Warmup", "x", "y");
        await SaveAsync(older.SetId, older.A, older.B, "Blend");
        await Task.Delay(20);
        var newer = await SetWithPairAsync("Peak night", "x", "y");
        await SaveAsync(newer.SetId, newer.A, newer.B, "Melt");
        var editing = await SetWithPairAsync("Tonight", "x", "y");

        var found = await History.FindLatestAsync("x", "y", editing.A, editing.B);

        Assert.Equal(("Melt", "Peak night", true), (found!.Transition.PresetName, found.SourceName, found.IsSet));
    }

    [Fact]
    public async Task IgnoresThePairBeingEdited_AndOtherDirections()
    {
        var only = await SetWithPairAsync("Warmup", "x", "y");
        await SaveAsync(only.SetId, only.A, only.B, "Blend");

        Assert.Null(await History.FindLatestAsync("x", "y", only.A, only.B)); // itself
        Assert.Null(await History.FindLatestAsync("y", "x", Guid.Empty, Guid.Empty)); // reverse direction
        Assert.NotNull(await History.FindLatestAsync("x", "y", Guid.Empty, Guid.Empty));
    }
}
