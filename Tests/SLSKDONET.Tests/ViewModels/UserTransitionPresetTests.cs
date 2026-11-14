using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Repositories;
using SLSKDONET.Services.Transitions;
using SLSKDONET.Tests.Services.Sets;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>#204 D5: TransitionTemplates surface as the DJ's own presets in the transition editor.</summary>
public sealed class UserTransitionPresetTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("user-presets");
    private readonly UserTransitionPresetService _service;

    public UserTransitionPresetTests() =>
        _service = new UserTransitionPresetService(_db.Factory, NullLogger<UserTransitionPresetService>.Instance);

    public void Dispose() => _db.Dispose();

    private static readonly CustomBlendSettings Echo = new(8, EchoDecayFactor: 0.6f, FilterStartFrequency: 400, LoopEnabled: true, LoopBars: 2, LoopRepeats: 4);

    [Fact]
    public async Task Save_RoundTrips_ReplacesByNameIgnoringCase_AndDeletes()
    {
        var first = await _service.SaveAsync("  Long echo ", Echo);
        Assert.Equal("Long echo", first.Name);
        var replaced = await _service.SaveAsync("LONG ECHO", Echo with { DurationBars = 16 });
        Assert.Equal(first.Id, replaced.Id);

        var all = await _service.GetAllAsync();
        var only = Assert.Single(all);
        Assert.Equal("LONG ECHO", only.Name);
        Assert.Equal(Echo with { DurationBars = 16 }, only.Settings);

        await using (var db = _db.Factory.CreateDbContext())
        {
            var row = db.TransitionTemplates.Single();
            Assert.Equal(16, row.BarsBeforeMix);
            Assert.Contains("\"bars\":2", row.LoopSettingsJson);
        }

        Assert.True(await _service.DeleteAsync(only.Id));
        Assert.False(await _service.DeleteAsync(only.Id));
        Assert.Empty(await _service.GetAllAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync("  ", Echo));
    }

    [Fact]
    public async Task UnreadableRows_AreSkipped_NotFatal()
    {
        await using (var db = _db.Factory.CreateDbContext())
        {
            db.TransitionTemplates.Add(new TransitionTemplateEntity { Name = "Broken", EffectsJson = "{not json" });
            db.TransitionTemplates.Add(new TransitionTemplateEntity { Name = "Legacy", EffectsJson = "{}", BarsBeforeMix = 32 });
            await db.SaveChangesAsync();
        }
        var all = await _service.GetAllAsync();
        var legacy = Assert.Single(all);
        Assert.Equal("Legacy", legacy.Name);
        Assert.Equal(32, legacy.Settings.DurationBars); // falls back to the row's bar count
    }

    [Fact]
    public async Task Editor_SavesTheCustomBlend_AppliesIt_AndPickingABuiltInLetsGo()
    {
        using var vm = CreateEditor();
        await vm.LoadUserPresetsAsync();
        Assert.True(vm.CanUseUserPresets);
        Assert.False(vm.HasUserPresets);

        vm.NewUserPresetName = "Mine";
        await vm.SaveUserPresetAsync();
        Assert.False(vm.HasUserPresets); // not in Custom mode: nothing saved
        Assert.Contains("Custom", vm.StatusMessage);

        vm.SelectedPreset = "Custom";
        vm.CustomEchoDecayFactor = 0.7f;
        vm.CustomEqSwapLow = true;
        vm.DurationBars = 32;
        await vm.SaveUserPresetAsync();
        var saved = Assert.Single(vm.UserPresets);
        Assert.Same(saved, vm.SelectedUserPreset);
        Assert.Equal(string.Empty, vm.NewUserPresetName);

        vm.SelectedPreset = "Fade"; // a built-in: no longer "my preset"
        Assert.Null(vm.SelectedUserPreset);
        vm.DurationBars = 8;
        vm.CustomEchoDecayFactor = null;

        vm.SelectedUserPreset = saved; // pick it from "My presets"
        Assert.Equal("Custom", vm.SelectedPreset);
        Assert.Equal(0.7f, vm.CustomEchoDecayFactor);
        Assert.True(vm.CustomEqSwapLow);
        Assert.Equal(32, vm.DurationBars);
        Assert.Same(saved, vm.SelectedUserPreset);

        await vm.DeleteSelectedUserPresetAsync();
        Assert.Empty(vm.UserPresets);
        Assert.Null(vm.SelectedUserPreset);
        Assert.Equal("Custom", vm.SelectedPreset); // the blend itself stays
    }

    private MixTransitionViewModel CreateEditor()
    {
        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(typeof(IUserTransitionPresetService))).Returns(_service);
        var cues = new Mock<ICuePointService>();
        cues.Setup(c => c.GetByTrackIdAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(new List<CuePointEntity>());
        return new MixTransitionViewModel(
            NullLogger<MixTransitionViewModel>.Instance,
            new Mock<ILibraryService>().Object,
            new EventBusService(),
            null!,
            new Mock<ITransitionRepository>().Object,
            new Mock<ITransitionPreviewPlayer>().Object,
            new Mock<ITrackRepository>().Object,
            cues.Object,
            services.Object);
    }
}
