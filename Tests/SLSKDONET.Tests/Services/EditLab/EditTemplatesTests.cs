using System;
using System.Linq;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;
using Xunit;

namespace SLSKDONET.Tests.Services.EditLab;

public sealed class EditTemplatesTests
{
    // A: 120 BPM base (2 s bars), downbeat 0.5 s, 240 s. B: 126 BPM, downbeat 1.0 s.
    private static EditProject TwoTracks()
    {
        var p = new EditProject { Bpm = 120, Key = "8A", BaseTrackHash = "a" };
        p.Sources.Add(new EditSource { TrackHash = "a", Title = "Alpha", Bpm = 120, Key = "8A", DownbeatSeconds = 0.5, DurationSeconds = 240 });
        p.Sources.Add(new EditSource { TrackHash = "b", Title = "Bravo", Bpm = 126, Key = "9A", DownbeatSeconds = 1.0, DurationSeconds = 200 });
        p.Lanes.Add(new EditLane { Name = "old", Clips = { new EditClip { SourceHash = "a" } } });
        return p;
    }

    [Fact]
    public void CustomIntro_BVocalsOverADrums_ThenAContinuesSeamlessly()
    {
        var p = TwoTracks();
        EditTemplates.Apply(p, new EditTemplateOptions(EditTemplateKind.CustomIntro, "a", "b", Bars: 8));

        Assert.Equal(3, p.Lanes.Count);
        var drums = p.Lanes[0].Clips.Single();
        var vocals = p.Lanes[1].Clips.Single();
        var rest = p.Lanes[2].Clips.Single();

        Assert.Equal((EditStem.Drums, "a", 0.0, 8.0, 0.5), (drums.Stem, drums.SourceHash, drums.StartBar, drums.LengthBars, drums.SourceInSeconds));
        Assert.Equal((EditStem.Vocals, "b", 0.0, 8.0, 1.0), (vocals.Stem, vocals.SourceHash, vocals.StartBar, vocals.LengthBars, vocals.SourceInSeconds));
        Assert.Equal(EditStem.FullMix, rest.Stem);
        Assert.Equal(8, rest.StartBar);
        Assert.Equal(0.5 + 8 * 2.0, rest.SourceInSeconds, 9);         // carries on where A's drums were
        Assert.True(rest.LengthBars > 100);                             // to the end of A
        Assert.Equal(1, EditMatching.Resolve(p, rest, p.Sources[0]).Speed);
    }

    [Fact]
    public void BasslineSwap_ReplacesOnlyTheRange()
    {
        var p = TwoTracks();
        EditTemplates.Apply(p, new EditTemplateOptions(EditTemplateKind.BasslineSwap, "a", "b", Bars: 16, StartBar: 32));

        var main = p.Lanes[0].Clips.OrderBy(c => c.StartBar).ToList();
        Assert.Equal(2, main.Count);
        Assert.Equal((0.0, 32.0), (main[0].StartBar, main[0].EndBar));
        Assert.Equal(48, main[1].StartBar);
        Assert.Equal(0.5 + 48 * 2.0, main[1].SourceInSeconds, 9);

        var inRange = p.Lanes.Skip(1).SelectMany(l => l.Clips).ToList();
        Assert.Equal(new[] { EditStem.Vocals, EditStem.Drums, EditStem.Other, EditStem.Bass }, inRange.Select(c => c.Stem));
        Assert.All(inRange, c => Assert.Equal((32.0, 48.0), (c.StartBar, c.EndBar)));
        var bass = inRange.Single(c => c.Stem == EditStem.Bass);
        Assert.Equal("b", bass.SourceHash);
        // B is stretched 126 → 120 (speed 120/126), so 32 project bars = 32 * 2 s * (126/120)… of B's own time.
        Assert.Equal(1.0 + 32 * 2.0 * (120.0 / 126.0), bass.SourceInSeconds, 9);
    }

    [Fact]
    public void ExtendedIntro_RepeatsTheIntroPhrase_AndLoopsIt()
    {
        var p = TwoTracks();
        p.Sources[0].Phrases.Add(new EditPhrase { Label = "Intro", StartSeconds = 0.5, DurationSeconds = 32, Bars = 16 });
        p.Sources[0].Phrases.Add(new EditPhrase { Label = "Build", StartSeconds = 32.5, DurationSeconds = 16, Bars = 8 });
        EditTemplates.Apply(p, new EditTemplateOptions(EditTemplateKind.ExtendedIntro, "a", Repeats: 3));

        var clips = p.Lanes.Single().Clips.OrderBy(c => c.StartBar).ToList();
        Assert.Equal(4, clips.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i * 16, clips[i].StartBar);
            Assert.Equal(16, clips[i].LengthBars);
            Assert.Equal(0.5, clips[i].SourceInSeconds);
        }
        Assert.Equal(48, clips[3].StartBar);
        Assert.Equal(32.5, clips[3].SourceInSeconds, 9);
        Assert.Equal((0.0, 16.0), (p.LoopStartBar!.Value, p.LoopEndBar!.Value));
    }

    [Fact]
    public void ExtendedIntro_WithoutPhrases_Uses16BarsFromTheDownbeat()
    {
        var p = TwoTracks();
        EditTemplates.Apply(p, new EditTemplateOptions(EditTemplateKind.ExtendedIntro, "a", Repeats: 2));
        var first = p.Lanes.Single().Clips.First();
        Assert.Equal((16.0, 0.5), (first.LengthBars, first.SourceInSeconds));
    }

    [Theory]
    [InlineData(EditTemplateKind.Acapella, EditStem.Vocals)]
    [InlineData(EditTemplateKind.Instrumental, EditStem.Instrumental)]
    public void SingleTrackExports_CoverTheWholeTrack(EditTemplateKind kind, EditStem stem)
    {
        var p = TwoTracks();
        EditTemplates.Apply(p, new EditTemplateOptions(kind, "a"));
        var clip = p.Lanes.Single().Clips.Single();
        Assert.Equal(stem, clip.Stem);
        Assert.Equal(0, clip.SourceInSeconds);
        Assert.Equal(120, clip.LengthBars); // 240 s at 2 s per bar
    }

    [Fact]
    public void TwoTrackTemplates_NeedTwoDifferentTracks()
    {
        var p = TwoTracks();
        Assert.Throws<InvalidOperationException>(() => EditTemplates.Apply(p, new EditTemplateOptions(EditTemplateKind.CustomIntro, "a", null)));
        Assert.Throws<InvalidOperationException>(() => EditTemplates.Apply(p, new EditTemplateOptions(EditTemplateKind.BasslineSwap, "a", "a")));
    }

    [Fact]
    public void AppliedTemplate_IsOneUndoStep_AndClipsStayEditable()
    {
        var editor = new EditProjectEditor();
        editor.Load(TwoTracks());
        editor.Mutate(p => EditTemplates.Apply(p, new EditTemplateOptions(EditTemplateKind.CustomIntro, "a", "b", Bars: 4)));
        var vocal = editor.Project.Lanes[1].Clips.Single();
        editor.SetClip(vocal.Id, c => { c.GainDb = -3; c.KeyMode = ClipKeyMode.Manual; c.ManualSemitones = -1; });
        editor.TrimEnd(vocal.Id, 6);
        Assert.Equal(6, editor.Project.FindClip(vocal.Id)!.EndBar);

        editor.Undo(); editor.Undo(); editor.Undo();
        Assert.Equal("old", editor.Project.Lanes.Single().Name);
    }
}
