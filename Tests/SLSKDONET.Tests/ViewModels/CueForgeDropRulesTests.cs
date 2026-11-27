using System;
using System.Linq;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public sealed class CueForgeDropRulesTests
{
    [Fact]
    public void ToggleCountdownBar_SwitchesToCustom_AndLiveRefreshesExistingDrop_WithUndo()
    {
        var session = new CueEditorSession(new Mock<ICuePointService>().Object, new Mock<ICueAudition>().Object);
        // 120 BPM => 1 bar = 2.0 s. Place a Drop at 80 s (bar 41) so -32, -16, -8 all fit.
        session.Load(
            new CueTrackInfo("h-drop", "Test DnB", null, Bpm: 120, Downbeat: 0, Duration: 240, Genre: "Drum & Bass"),
            Array.Empty<CuePointEntity>());

        session.SetDropAtCommand.Execute(80.0).Subscribe();

        // DnB default is -16 and -8.
        Assert.True(session.HasBar16);
        Assert.True(session.HasBar8);
        Assert.False(session.HasBar32);
        Assert.Contains(session.Cues, c => c.Name == "[IN -16]");
        Assert.Contains(session.Cues, c => c.Name == "[IN -8]");
        Assert.DoesNotContain(session.Cues, c => c.Name == "[IN -32]");

        // Click the −32 bar pill -> switches to Custom ("32,16,8") and adds [IN -32] live!
        session.ToggleCountdownBarCommand.Execute(32).Subscribe();

        Assert.Equal(DropCountdownCues.Custom, session.DropCountdownMode);
        Assert.Equal("32,16,8", session.CustomCountdownBars);
        Assert.True(session.HasBar32);
        Assert.Contains(session.Cues, c => c.Name == "[IN -32]");
        Assert.Contains("−32 −16 −8", session.DropRulesSummary);

        // Undo restores the previous cue list cleanly.
        session.UndoCommand.Execute().Subscribe();
        Assert.DoesNotContain(session.Cues, c => c.Name == "[IN -32]");
    }
}
