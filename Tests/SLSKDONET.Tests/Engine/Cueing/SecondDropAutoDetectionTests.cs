using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.Engine.Cueing;

/// <summary>
/// Verifies that manually setting Drop 1 on a 2-drop track (e.g. DnB 175 BPM: Drop 1 -> Breakdown -> Drop 2)
/// automatically detects and places [DROP 2], its countdowns, and places [OUT] after Drop 2.
/// </summary>
public sealed class SecondDropAutoDetectionTests
{
    [Fact]
    public void DetectSecondDrop_DnBStructure_FindsDrop2AfterBreakdown()
    {
        // Mirrors Electric Eye - Prolix Remix (175 BPM, 4:18 = 258 s):
        // Bar 49 = Drop 1 (~66.0 s), Bar 81 = Drop continuation (~110.0 s),
        // Bar 97 = Breakdown (~132.0 s), Bar 129 = Drop 2 (~176.0 s).
        const double bpm = 175.0;
        double bar = 240.0 / bpm;
        double drop1 = 48 * bar;
        var phrases = new List<PhraseSegment>
        {
            new() { Label = "Intro", Start = 0f, Duration = (float)(48 * bar), Bars = 48 },
            new() { Label = "Drop", Start = (float)drop1, Duration = (float)(32 * bar), Bars = 32 },
            new() { Label = "Drop", Start = (float)(80 * bar), Duration = (float)(16 * bar), Bars = 16 },
            new() { Label = "Breakdown", Start = (float)(96 * bar), Duration = (float)(32 * bar), Bars = 32 },
            new() { Label = "Drop", Start = (float)(128 * bar), Duration = (float)(48 * bar), Bars = 48 },
        };

        double? detected = DropCountdownCues.DetectSecondDrop(drop1, phrases, bpm, downbeat: 0, duration: 258.0);

        Assert.NotNull(detected);
        Assert.Equal(128 * bar, detected!.Value, precision: 2);
    }

    [Fact]
    public void CueEditorSession_SetDropAtPlayhead_AutoPlacesDrop2AndMovesOutAfterDrop2()
    {
        const double bpm = 175.0;
        double bar = 240.0 / bpm;
        double drop1 = 48 * bar;
        double drop2 = 128 * bar;

        var cueService = new Mock<ICuePointService>();
        var audition = new Mock<ICueAudition>();
        audition.SetupGet(a => a.PositionSeconds).Returns(drop1);
        audition.SetupGet(a => a.IsPlaying).Returns(false);

        var session = new CueEditorSession(cueService.Object, audition.Object, new CueSessionRegistry(new AppConfig { DropCountdownMode = DropCountdownCues.DnB }));
        session.Load(new CueTrackInfo("dnb-hash", "Electric Eye", null, bpm, 0, 258.0, "Drum & Bass"), Array.Empty<CuePointEntity>());
        session.SetStructureHints(new List<PhraseSegment>
        {
            new() { Label = "Drop", Start = (float)drop1, Duration = (float)(32 * bar), Bars = 32 },
            new() { Label = "Breakdown", Start = (float)(96 * bar), Duration = (float)(32 * bar), Bars = 32 },
            new() { Label = "Drop", Start = (float)drop2, Duration = (float)(48 * bar), Bars = 48 },
        });

        session.SetDropAtPlayheadCommand.Execute().Subscribe();

        var drops = session.Cues.Where(c => c.Role == CueRole.Drop).OrderBy(c => c.Timestamp).ToList();
        Assert.Equal(2, drops.Count);
        Assert.Equal("[DROP 1]", drops[0].Name);
        Assert.Equal("[DROP 2]", drops[1].Name);
        Assert.Equal(drop1, drops[0].Timestamp, precision: 2);
        Assert.Equal(drop2, drops[1].Timestamp, precision: 2);

        // [OUT] must be placed AFTER Drop 2, not after Drop 1!
        var outCue = session.Cues.Single(c => c.Name == DropCountdownCues.OutName);
        Assert.True(outCue.Timestamp > drop2, $"Expected [OUT] ({outCue.Timestamp:0.0}s) after Drop 2 ({drop2:0.0}s)");

        // Single Undo step restores the clean state before SetDropAtPlayhead.
        session.UndoCommand.Execute().Subscribe();
        Assert.Empty(session.Cues);
    }
}
