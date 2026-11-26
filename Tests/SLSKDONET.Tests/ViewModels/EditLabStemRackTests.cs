using System;
using System.IO;
using SLSKDONET.ViewModels.EditLab;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Covers Epic #517 Phase 5 — #541 (FO5a: Workstation's stem rack moved into Edit Lab).
/// </summary>
public class EditLabStemRackTests
{
    [Fact]
    public void StemRack_PresetsAndMutes_MatchWorkstationBehaviour()
    {
        int changeCount = 0;
        var rack = new EditLabStemRackViewModel { OnStemMuteChanged = () => changeCount++ };

        Assert.Equal("All stems active", rack.StemControlSummary);

        // Instrumental mutes vocals only; toggling again restores all stems
        rack.ToggleInstrumentalCommand.Execute().Subscribe();
        Assert.True(rack.VocalsMuted);
        Assert.False(rack.DrumsMuted);
        Assert.False(rack.BassMuted);
        Assert.False(rack.OtherMuted);
        Assert.Equal("Instrumental (vocals muted)", rack.StemControlSummary);

        // Drums only keeps drums active and mutes vocals/bass/other
        rack.ToggleDrumsOnlyCommand.Execute().Subscribe();
        Assert.True(rack.VocalsMuted);
        Assert.False(rack.DrumsMuted);
        Assert.True(rack.BassMuted);
        Assert.True(rack.OtherMuted);
        Assert.Equal("Drums only", rack.StemControlSummary);

        // Reset restores all stems
        rack.ResetAllStemsCommand.Execute().Subscribe();
        Assert.Equal("All stems active", rack.StemControlSummary);
        Assert.True(changeCount >= 3);
    }

    [Fact]
    public void EditLabPage_ExposesStemRackControls()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
            dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "Views", "Avalonia", "EditLabPage.axaml"));

        Assert.Contains("StemRack.VocalsMuted", xaml, StringComparison.Ordinal);
        Assert.Contains("StemRack.DrumsMuted", xaml, StringComparison.Ordinal);
        Assert.Contains("StemRack.BassMuted", xaml, StringComparison.Ordinal);
        Assert.Contains("StemRack.OtherMuted", xaml, StringComparison.Ordinal);
        Assert.Contains("StemRack.ToggleInstrumentalCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("StemRack.ToggleDrumsOnlyCommand", xaml, StringComparison.Ordinal);
    }
}
