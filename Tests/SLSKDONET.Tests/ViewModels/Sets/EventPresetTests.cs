using System;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Sets;
using SLSKDONET.Tests.Services.Sets;
using SLSKDONET.ViewModels.Sets;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.Sets;

/// <summary>#207 (P6): event presets feed Set Health's energy flow.</summary>
public sealed class EventPresetTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("event-presets");
    private readonly SetProjectService _service;

    public EventPresetTests() => _service = new SetProjectService(_db.Factory);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void EnergyFlow_IsPerfectOnTheCurve_LowerOffIt_NullWithFewTargets()
    {
        var curve = EventPresets.ByKey("warmup")!.EnergyCurve;
        Assert.Equal(100, EventPresets.EnergyFlow(new double?[] { curve[0], curve[5] }, curve)!.Value, 6);
        var off = EventPresets.EnergyFlow(new double?[] { 95, 95 }, curve)!.Value;
        Assert.True(off < 60, $"{off}");
        Assert.Null(EventPresets.EnergyFlow(new double?[] { 40, null, null }, curve)); // one target only
    }

    [Fact]
    public void ProfileNames_RoundTripToPresetKeys()
    {
        Assert.Equal("club", EventPresets.KeyFromProfileName("preset:club"));
        Assert.Null(EventPresets.KeyFromProfileName("My festival"));
        Assert.Null(EventPresets.KeyFromProfileName("preset:unknown"));
    }

    [Fact]
    public async Task SetEventPreset_IsStored_SharedPerPreset_AndClearable()
    {
        var a = await _service.CreateSetAsync("A");
        var b = await _service.CreateSetAsync("B");
        Assert.True(await _service.SetEventPresetAsync(a.Id, "peak"));
        Assert.True(await _service.SetEventPresetAsync(b.Id, "peak"));

        var sets = await _service.GetSetsAsync();
        Assert.All(sets, s => Assert.Equal("peak", s.EventPresetKey));
        await using (var db = _db.Factory.CreateDbContext())
            Assert.Equal(1, db.EventProfiles.Count(p => p.Name == "preset:peak")); // one shared profile row

        Assert.True(await _service.SetEventPresetAsync(a.Id, null));
        Assert.Null((await _service.GetSetsAsync()).Single(s => s.Id == a.Id).EventPresetKey);
        Assert.False(await _service.SetEventPresetAsync(Guid.NewGuid(), "club")); // gone set
    }

    [Fact]
    public async Task Picker_ShowsTheStoredPreset_AndChangingItAddsEnergyFlow()
    {
        _db.SeedLibrary(("h1", "Noisia", "One"), ("h2", "Mefjus", "Two"));
        var set = await _service.CreateSetAsync("Friday");
        await _service.AppendTrackAsync(set.Id, "h1");
        await _service.AppendTrackAsync(set.Id, "h2");
        await _service.SetEventPresetAsync(set.Id, "club");

        var vm = new SetsViewModel(_service, new Mock<IDialogService>().Object, new Mock<INotificationService>().Object);
        await vm.LoadAsync();
        vm.SelectedSet = vm.Sets.Single();
        await vm.PendingSlotLoad;

        Assert.Equal("club", vm.SelectedEventPreset.Preset?.Key);
        Assert.Contains(vm.Health.Metrics, m => m.Label.StartsWith("Energy flow"));

        vm.SelectedEventPreset = vm.EventPresetOptions.First(o => o.Preset is null); // "No event"
        await vm.PendingPresetSave;
        Assert.Null((await _service.GetSetsAsync()).Single().EventPresetKey);
        Assert.DoesNotContain(vm.Health.Metrics, m => m.Label.StartsWith("Energy flow"));
    }
}
