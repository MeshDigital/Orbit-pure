using System;
using System.IO;
using System.Linq;
using NAudio.Wave;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;
using Xunit;

namespace SLSKDONET.Tests.Services.EditLab;

public sealed class EditProjectEditorTests
{
    private static EditProjectEditor WithSource(double bpm = 120, string key = "8A", double downbeat = 0.5)
    {
        var editor = new EditProjectEditor();
        editor.Load(new EditProject());
        editor.AddSource(new EditSource { TrackHash = "a", Title = "Alpha", Bpm = bpm, Key = key, DownbeatSeconds = downbeat, DurationSeconds = 240 });
        return editor;
    }

    [Fact]
    public void FirstSource_SetsBase_ManualTempoSurvivesBaseChange()
    {
        var editor = WithSource(124, "5A");
        Assert.Equal("a", editor.Project.BaseTrackHash);
        Assert.Equal(124, editor.Project.Bpm);
        Assert.Equal("5A", editor.Project.Key);

        editor.SetTempo(128);
        editor.AddSource(new EditSource { TrackHash = "b", Bpm = 140, Key = "9B" });
        editor.SetBase("b");
        Assert.Equal(128, editor.Project.Bpm);   // typed tempo is kept
        Assert.Equal("9B", editor.Project.Key);  // key wasn't typed, so it follows the base
    }

    [Fact]
    public void AddClip_DefaultsToTheDownbeat_AndAtMost16Bars()
    {
        var editor = WithSource();
        var clip = editor.AddClip("a", EditStem.Drums, 4);
        Assert.Equal(0.5, clip.SourceInSeconds);
        Assert.Equal(16, clip.LengthBars);
        Assert.Equal(4, clip.StartBar);
        Assert.Single(editor.Project.Lanes);
        Assert.Contains("drums", editor.Project.Lanes[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public void TrimStart_KeepsTheAudioUnderTheRestOfTheClipInPlace()
    {
        var editor = WithSource(120);
        editor.SetTempo(126); // speed 1.05
        var clip = editor.AddClip("a", EditStem.FullMix, 0, lengthBars: 8, sourceInSeconds: 10);
        editor.TrimStart(clip.Id, 2);
        var trimmed = editor.Project.FindClip(clip.Id)!;
        Assert.Equal(2, trimmed.StartBar);
        Assert.Equal(6, trimmed.LengthBars, 9);
        double secondsPerBar = 4 * 60 / 126.0;
        Assert.Equal(10 + 2 * secondsPerBar * 1.05, trimmed.SourceInSeconds, 9);
    }

    [Fact]
    public void TrimEnd_RespectsMinimumLength_AndClampsFades()
    {
        var editor = WithSource();
        var clip = editor.AddClip("a", EditStem.FullMix, 0, lengthBars: 8);
        editor.SetClip(clip.Id, c => { c.FadeInBars = 2; c.FadeOutBars = 4; });
        editor.TrimEnd(clip.Id, 0.1);
        var c2 = editor.Project.FindClip(clip.Id)!;
        Assert.Equal(EditProjectEditor.MinClipBars, c2.LengthBars);
        Assert.True(c2.FadeInBars + c2.FadeOutBars <= c2.LengthBars + 1e-9);
    }

    [Fact]
    public void Gesture_IsOneUndoStep_AndCancelRestores()
    {
        var editor = WithSource();
        var clip = editor.AddClip("a", EditStem.FullMix, 0);
        int undoBefore = CountUndo(editor);

        editor.BeginGesture();
        editor.MoveClip(clip.Id, 1);
        editor.MoveClip(clip.Id, 2);
        editor.EndGesture();
        Assert.Equal(undoBefore + 1, CountUndo(editor));

        editor.BeginGesture();
        editor.MoveClip(clip.Id, 9);
        editor.CancelGesture();
        Assert.Equal(2, editor.Project.FindClip(clip.Id)!.StartBar);
    }

    [Fact]
    public void MoveClip_ToAnotherLane()
    {
        var editor = WithSource();
        var clip = editor.AddClip("a", EditStem.FullMix, 0);
        editor.AddLane("Second");
        editor.MoveClip(clip.Id, 3, laneIndex: 1);
        Assert.Empty(editor.Project.Lanes[0].Clips);
        Assert.Equal(clip.Id, editor.Project.Lanes[1].Clips.Single().Id);
    }

    [Fact]
    public void RemoveSource_RemovesItsClips_AndPicksANewBase()
    {
        var editor = WithSource();
        editor.AddSource(new EditSource { TrackHash = "b", Bpm = 100 });
        editor.AddClip("a", EditStem.FullMix, 0);
        editor.AddClip("b", EditStem.FullMix, 0);
        editor.RemoveSource("a");
        Assert.All(editor.Project.AllClips, c => Assert.Equal("b", c.SourceHash));
        Assert.Equal("b", editor.Project.BaseTrackHash);
    }

    [Fact]
    public void UndoHistory_IsCapped()
    {
        var editor = WithSource();
        var clip = editor.AddClip("a", EditStem.FullMix, 0);
        for (int i = 0; i < EditProjectEditor.MaxUndo + 50; i++) editor.MoveClip(clip.Id, i);
        Assert.Equal(EditProjectEditor.MaxUndo, CountUndo(editor));
    }

    [Fact]
    public void Coalescing_GroupsRepeatedFieldEdits_WithinTheWindow()
    {
        var editor = WithSource();
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        editor.Now = () => now;
        var clip = editor.AddClip("a", EditStem.FullMix, 0);
        int before = CountUndo(editor);
        editor.SetClip(clip.Id, c => c.GainDb = -1, "gain");
        editor.SetClip(clip.Id, c => c.GainDb = -2, "gain");
        Assert.Equal(before + 1, CountUndo(editor));
        now = now.AddSeconds(5);
        editor.SetClip(clip.Id, c => c.GainDb = -3, "gain");
        Assert.Equal(before + 2, CountUndo(editor));
    }

    [Fact]
    public void Split_ProducesIdenticalAudio()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-editlab-split", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "saw.wav");
            using (var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)))
                for (long f = 0; f < 44100L * 20; f++) { float s = (f % 441) / 441f - 0.5f; w.WriteSamples(new[] { s, s }, 0, 2); }

            var editor = new EditProjectEditor();
            editor.Load(new EditProject());
            editor.AddSource(new EditSource { TrackHash = "a", Bpm = 120, FilePath = path, DurationSeconds = 20 });
            editor.SetTempo(126);
            var clip = editor.AddClip("a", EditStem.FullMix, 0, lengthBars: 4, sourceInSeconds: 1);
            float[] Render()
            {
                var plan = EditRenderPlan.Compile(editor.Project, 44100);
                var buf = new float[(int)plan.TotalFrames * 2];
                using var r = new EditTimelineRenderer(plan);
                r.Read(buf, 0, buf.Length);
                return buf;
            }
            var whole = Render();
            Assert.NotNull(editor.SplitClip(clip.Id, 1.5));
            Assert.Equal(2, editor.Project.AllClips.Count());
            var split = Render();
            Assert.Equal(whole.Length, split.Length);
            // The phase vocoder restarts at the cut, so compare away from it; timing must match exactly.
            long cut = EditRenderPlan.Compile(editor.Project, 44100).BarToFrame(1.5);
            for (long f = 0; f < whole.Length / 2; f += 97)
            {
                if (f > cut - 1 && f < cut + 8192) continue;
                if (f < cut) Assert.Equal(whole[f * 2], split[f * 2], 6);
            }

            // Native tempo, no shift: the split must be bit-identical on both sides of the cut.
            editor.Undo();
            editor.SetAutoMatch(false, null);
            whole = Render();
            Assert.NotNull(editor.SplitClip(editor.Project.AllClips.Single().Id, 1.5));
            Assert.Equal(whole, Render());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* temp dir */ }
        }
    }

    [Theory]
    [InlineData(3.3, 1, 3.25)]
    [InlineData(3.3, 4, 3.0)]
    [InlineData(3.3, 0, 3.3)]
    public void Snap_RoundsToTheGrid(double bar, int beats, double expected) =>
        Assert.Equal(expected, EditProjectEditor.Snap(bar, beats), 9);

    private static int CountUndo(EditProjectEditor editor)
    {
        int n = 0;
        var json = EditProjectStore.Serialize(editor.Project);
        while (editor.Undo()) n++;
        for (int i = 0; i < n; i++) editor.Redo();
        Assert.Equal(json, EditProjectStore.Serialize(editor.Project));
        return n;
    }
}
