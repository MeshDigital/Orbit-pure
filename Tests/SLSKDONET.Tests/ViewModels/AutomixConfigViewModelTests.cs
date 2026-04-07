using System.Linq;
using Xunit;
using SLSKDONET.Configuration;
using SLSKDONET.Services.Playlist;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Tests.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// AutomixConfigViewModel tests — Task 3.4 (#77)
// ─────────────────────────────────────────────────────────────────────────────

public class AutomixConfigViewModelTests
{
    private static AutomixConfigViewModel Create() => new();

    // ── Defaults ──────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_AreReasonable()
    {
        var vm = Create();
        Assert.Equal(100,   vm.MinBpm);
        Assert.Equal(160,   vm.MaxBpm);
        Assert.True(vm.MatchKey);
        Assert.Equal(3,     vm.MaxEnergyJump);
        Assert.Equal(20,    vm.MaxTracks);
        Assert.Equal("Wave", vm.EnergyCurve);
        Assert.Equal(3.0,   vm.HarmonicWeight);
        Assert.Equal(1.0,   vm.TempoWeight);
        Assert.Equal(0.5,   vm.EnergyWeight);
    }

    [Fact]
    public void PreviewTransitions_PopulatedOnConstruction()
    {
        var vm = Create();
        Assert.Equal(5, vm.PreviewTransitions.Count);
    }

    // ── Clamping ──────────────────────────────────────────────────────────

    [Fact]
    public void MinBpm_CannotExceedMaxBpmMinus1()
    {
        var vm = Create();
        vm.MaxBpm = 130;
        vm.MinBpm = 200; // way above MaxBpm
        Assert.True(vm.MinBpm < vm.MaxBpm);
    }

    [Fact]
    public void MaxBpm_CannotFallBelowMinBpmPlus1()
    {
        var vm = Create();
        vm.MinBpm = 120;
        vm.MaxBpm = 80; // below MinBpm
        Assert.True(vm.MaxBpm > vm.MinBpm);
    }

    [Fact]
    public void MaxEnergyJump_ClampedTo1_9()
    {
        var vm = Create();
        vm.MaxEnergyJump = 100;
        Assert.Equal(9, vm.MaxEnergyJump);
        vm.MaxEnergyJump = 0;
        Assert.Equal(1, vm.MaxEnergyJump);
    }

    // ── ResetToDefaults ───────────────────────────────────────────────────

    [Fact]
    public void ResetToDefaultsCommand_RestoresAllValues()
    {
        var vm = Create();
        vm.MinBpm        = 85;
        vm.MaxBpm        = 200;
        vm.MatchKey      = false;
        vm.MaxEnergyJump = 7;
        vm.EnergyCurve   = "Peak";
        vm.HarmonicWeight = 1.0;

        vm.ResetToDefaultsCommand.Execute(System.Reactive.Unit.Default).Subscribe();

        Assert.Equal(100,    vm.MinBpm);
        Assert.Equal(160,    vm.MaxBpm);
        Assert.True(vm.MatchKey);
        Assert.Equal(3,      vm.MaxEnergyJump);
        Assert.Equal("Wave", vm.EnergyCurve);
        Assert.Equal(3.0,    vm.HarmonicWeight);
    }

    // ── AppConfig round-trip ──────────────────────────────────────────────

    [Fact]
    public void LoadFrom_HydratesAllProperties()
    {
        var config = new AppConfig
        {
            AutomixMinBpm        = 110,
            AutomixMaxBpm        = 140,
            AutomixMatchKey      = false,
            AutomixMaxEnergyJump = 5,
            AutomixEnergyCurve   = "Rising",
            AutomixHarmonicWeight = 2.0,
            AutomixTempoWeight   = 0.5,
            AutomixEnergyWeight  = 1.5,
        };

        var vm = Create();
        vm.LoadFrom(config);

        Assert.Equal(110,       vm.MinBpm);
        Assert.Equal(140,       vm.MaxBpm);
        Assert.False(vm.MatchKey);
        Assert.Equal(5,         vm.MaxEnergyJump);
        Assert.Equal("Rising",  vm.EnergyCurve);
        Assert.Equal(2.0,       vm.HarmonicWeight);
    }

    [Fact]
    public void SaveTo_PersistsAllProperties()
    {
        var vm = Create();
        vm.MinBpm        = 115;
        vm.MaxBpm        = 145;
        vm.MatchKey      = false;
        vm.MaxEnergyJump = 4;
        vm.EnergyCurve   = "Peak";
        vm.HarmonicWeight = 2.5;

        var config = new AppConfig();
        vm.SaveTo(config);

        Assert.Equal(115,    config.AutomixMinBpm);
        Assert.Equal(145,    config.AutomixMaxBpm);
        Assert.False(config.AutomixMatchKey);
        Assert.Equal(4,      config.AutomixMaxEnergyJump);
        Assert.Equal("Peak", config.AutomixEnergyCurve);
        Assert.Equal(2.5,    config.AutomixHarmonicWeight);
    }

    // ── ToConstraints ─────────────────────────────────────────────────────

    [Fact]
    public void ToConstraints_MapsAllFields()
    {
        var vm = Create();
        vm.MinBpm        = 120;
        vm.MaxBpm        = 150;
        vm.MatchKey      = false;
        vm.MaxEnergyJump = 2;

        var c = vm.ToConstraints();

        Assert.Equal(120, c.MinBpm);
        Assert.Equal(150, c.MaxBpm);
        Assert.False(c.MatchKey);
        Assert.Equal(2,   c.MaxEnergyJump);
    }

    // ── ToOptimizerOptions ────────────────────────────────────────────────

    [Fact]
    public void ToOptimizerOptions_MapsCurveNone()
    {
        var vm = Create();
        vm.EnergyCurve = "None";
        Assert.Equal(EnergyCurvePattern.None, vm.ToOptimizerOptions().EnergyCurve);
    }

    [Fact]
    public void ToOptimizerOptions_MapsCurveRising()
    {
        var vm = Create();
        vm.EnergyCurve = "Rising";
        Assert.Equal(EnergyCurvePattern.Rising, vm.ToOptimizerOptions().EnergyCurve);
    }

    [Fact]
    public void ToOptimizerOptions_MapsCurveWave()
    {
        var vm = Create();
        vm.EnergyCurve = "Wave";
        Assert.Equal(EnergyCurvePattern.Wave, vm.ToOptimizerOptions().EnergyCurve);
    }

    [Fact]
    public void ToOptimizerOptions_MapsCurvePeak()
    {
        var vm = Create();
        vm.EnergyCurve = "Peak";
        Assert.Equal(EnergyCurvePattern.Peak, vm.ToOptimizerOptions().EnergyCurve);
    }

    // ── Preview rebuilds ── ───────────────────────────────────────────────

    [Fact]
    public void PreviewTransitions_RebuildsWhenMatchKeyChanges()
    {
        var vm = Create();
        vm.MatchKey = true;
        var labelsBefore = vm.PreviewTransitions.Select(t => t.CompatibilityLabel).ToArray();

        vm.MatchKey = false;
        var labelsAfter = vm.PreviewTransitions.Select(t => t.CompatibilityLabel).ToArray();

        // At least one label should differ (index 2 row switches based on MatchKey)
        Assert.False(labelsBefore.SequenceEqual(labelsAfter));
    }

    [Fact]
    public void PreviewTransitions_ContainsExactly5Items_AfterBpmChange()
    {
        var vm = Create();
        vm.MinBpm = 125;
        Assert.Equal(5, vm.PreviewTransitions.Count);
    }

    // ── SaveCommand ───────────────────────────────────────────────────────

    [Fact]
    public void SaveCommand_SetsWasSavedTrue()
    {
        var vm = Create();
        Assert.False(vm.WasSaved);
        vm.SaveCommand.Execute(System.Reactive.Unit.Default).Subscribe();
        Assert.True(vm.WasSaved);
    }

    // ── Null guards ───────────────────────────────────────────────────────

    [Fact]
    public void LoadFrom_ThrowsOnNull()
        => Assert.Throws<ArgumentNullException>(() => Create().LoadFrom(null!));

    [Fact]
    public void SaveTo_ThrowsOnNull()
        => Assert.Throws<ArgumentNullException>(() => Create().SaveTo(null!));
}
