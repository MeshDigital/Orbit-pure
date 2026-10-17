using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;
using Xunit;

namespace SLSKDONET.Tests.Services.EditLab;

public class EditKeyMathTests
{
    [Theory]
    [InlineData("8A", "8A")]
    [InlineData("08a", "8A")]
    [InlineData("Am", "8A")]
    [InlineData("F#", "2B")]
    [InlineData("", null)]
    [InlineData("nonsense", null)]
    public void Normalize_AcceptsCamelotAndStandardNotation(string input, string? expected) =>
        Assert.Equal(expected, EditKeyMath.Normalize(input));

    [Theory]
    [InlineData("8A", 1, "3A")]   // A minor + 1 st = Bb minor = 3A
    [InlineData("8B", 7, "9B")]   // C + fifth = G
    [InlineData("8B", -1, "1B")]  // C - 1 st = B
    [InlineData("12A", 12, "12A")]
    public void Transpose_FollowsTheWheel(string key, int semitones, string expected) =>
        Assert.Equal(expected, EditKeyMath.Transpose(key, semitones));

    [Theory]
    [InlineData("8A", "8A", 0)]
    [InlineData("8A", "9A", 0)]   // adjacent: already compatible
    [InlineData("8A", "8B", 0)]   // relative major
    [InlineData("3A", "8A", -1)]  // Bb minor -> A minor: down one semitone (exact)
    [InlineData("1A", "8A", 1)]   // G# minor -> A minor: up one semitone (exact)
    public void SuggestShift_FindsTheSmallestCompatibleShift(string clip, string project, int expected) =>
        Assert.Equal(expected, EditKeyMath.SuggestShift(clip, project));

    [Fact]
    public void SuggestShift_ResultIsAlwaysCompatible()
    {
        for (int a = 1; a <= 12; a++)
        foreach (var am in new[] { "A", "B" })
        for (int b = 1; b <= 12; b++)
        foreach (var bm in new[] { "A", "B" })
        {
            var shift = EditKeyMath.SuggestShift($"{a}{am}", $"{b}{bm}");
            Assert.NotNull(shift);
            Assert.InRange(shift!.Value, -6, 6);
            Assert.True(EditKeyMath.IsCompatible(EditKeyMath.Transpose($"{a}{am}", shift.Value), $"{b}{bm}"));
        }
    }

    [Fact]
    public void SuggestShift_UnknownKey_IsNull() => Assert.Null(EditKeyMath.SuggestShift(null, "8A"));
}

public class EditMatchingTests
{
    private static (EditProject p, EditSource s, EditClip c) Setup(double projectBpm, double sourceBpm, string? pKey = "8A", string? sKey = "3A")
    {
        var s = new EditSource { TrackHash = "s", Bpm = sourceBpm, Key = sKey, DownbeatSeconds = 0.5 };
        var p = new EditProject { Bpm = projectBpm, Key = pKey, Sources = { s } };
        var c = new EditClip { SourceHash = "s" };
        return (p, s, c);
    }

    [Fact]
    public void AutoTempo_StretchesToProjectBpm()
    {
        var (p, s, c) = Setup(126, 120);
        var m = EditMatching.Resolve(p, c, s);
        Assert.True(m.FollowsTempo);
        Assert.Equal(1.05, m.Speed, 6);
        Assert.Equal(5.0, m.StretchPercent, 6);
        Assert.False(m.StretchWarning);
    }

    [Fact]
    public void AutoTempoOff_PlaysNative_ButPerClipFollowOverrides()
    {
        var (p, s, c) = Setup(126, 120);
        p.AutoMatchTempo = false;
        Assert.Equal(1.0, EditMatching.Resolve(p, c, s).Speed);
        c.TempoMode = ClipTempoMode.Follow;
        Assert.Equal(1.05, EditMatching.Resolve(p, c, s).Speed, 6);
        p.AutoMatchTempo = true;
        c.TempoMode = ClipTempoMode.Native;
        Assert.Equal(1.0, EditMatching.Resolve(p, c, s).Speed);
    }

    [Fact]
    public void HalfTimeSource_IsFoldedNotStretchedDouble()
    {
        var (p, s, c) = Setup(174, 87);
        var m = EditMatching.Resolve(p, c, s);
        Assert.Equal(174, m.EffectiveSourceBpm, 6);
        Assert.Equal(1.0, m.Speed, 6);
    }

    [Fact]
    public void LargeStretch_IsFlagged()
    {
        var (p, s, c) = Setup(140, 125);
        Assert.True(EditMatching.Resolve(p, c, s).StretchWarning);
    }

    [Fact]
    public void Key_AutoOnlyWhenToggled_ManualAlwaysWins()
    {
        var (p, s, c) = Setup(120, 120, "8A", "3A");
        var off = EditMatching.Resolve(p, c, s);
        Assert.Equal(0, off.Semitones);
        Assert.Equal(-1, off.SuggestedSemitones);

        p.AutoMatchKey = true;
        var on = EditMatching.Resolve(p, c, s);
        Assert.Equal(-1, on.Semitones);
        Assert.Equal("8A", on.ResultKey);

        c.KeyMode = ClipKeyMode.Manual;
        c.ManualSemitones = 2;
        Assert.Equal(2, EditMatching.Resolve(p, c, s).Semitones);
    }

    [Fact]
    public void PitchFactor_KeyLocksTheStretch()
    {
        var (p, s, c) = Setup(126, 120);
        var m = EditMatching.Resolve(p, c, s);
        Assert.Equal(1.0 / 1.05, m.PitchFactor, 9);
    }

    [Fact]
    public void BarMaths_RoundTrip()
    {
        var (p, s, _) = Setup(120, 128);
        Assert.Equal(2.0, EditMatching.SecondsPerBar(p), 9);
        Assert.Equal(8.0, EditMatching.BarToSeconds(p, 4), 9);
        double sec = EditMatching.SourceBarToSeconds(s, 5);
        Assert.Equal(0.5 + 4 * 1.875, sec, 9);
        Assert.Equal(5.0, EditMatching.SourceSecondsToBar(s, sec), 9);
    }
}

public class EditProjectStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orbit-editlab-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* temp dir */ }
    }

    private static EditProject Sample(string name)
    {
        var project = new EditProject { Name = name, Bpm = 174.2, Key = "8A", BaseTrackHash = "a", AutoMatchKey = true, LoopStartBar = 4, LoopEndBar = 8 };
        project.Sources.Add(new EditSource { TrackHash = "a", Title = "Base", Artist = "Artist", Bpm = 174.2, Key = "8A", StemPaths = { [EditStem.Vocals] = "v.wav" } });
        var lane = new EditLane { Name = "Vocals" };
        lane.Clips.Add(new EditClip { SourceHash = "a", Stem = EditStem.Vocals, StartBar = 2, LengthBars = 8, GainDb = -3, TempoMode = ClipTempoMode.Native, KeyMode = ClipKeyMode.Manual, ManualSemitones = -2 });
        project.Lanes.Add(lane);
        return project;
    }

    [Fact]
    public async Task SaveLoad_RoundTripsEverything()
    {
        var store = new EditProjectStore(rootDirectory: _dir);
        var project = Sample("Intro edit");
        await store.SaveAsync(project);

        var loaded = await store.LoadAsync(project.Id);
        Assert.NotNull(loaded);
        Assert.Equal(EditProjectStore.Serialize(project), EditProjectStore.Serialize(loaded!));
        var clip = loaded!.AllClips.Single();
        Assert.Equal(EditStem.Vocals, clip.Stem);
        Assert.Equal(ClipKeyMode.Manual, clip.KeyMode);
        Assert.Equal("v.wav", loaded.Sources[0].StemPaths[EditStem.Vocals]);
        Assert.True(loaded.HasLoopRegion);
    }

    [Fact]
    public async Task ListRecent_NewestFirst_SkipsCorruptFiles()
    {
        var store = new EditProjectStore(rootDirectory: _dir);
        var first = Sample("First");
        await store.SaveAsync(first);
        await Task.Delay(20);
        var second = Sample("Second");
        await store.SaveAsync(second);
        await File.WriteAllTextAsync(Path.Combine(store.ProjectsDirectory, "broken.json"), "{ not json");

        var recent = await store.ListRecentAsync();
        Assert.Equal(new[] { "Second", "First" }, recent.Select(r => r.Name));
        Assert.Equal("Base", recent[0].BaseTitle);
        Assert.Equal(1, recent[0].ClipCount);

        await store.DeleteAsync(first.Id);
        Assert.Single(await store.ListRecentAsync());
    }

    [Fact]
    public void DefaultRoot_FollowsTheIsolatedTestDatabase()
    {
        var root = EditProjectStore.DefaultRoot();
        Assert.StartsWith(Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!, root);
        Assert.DoesNotContain(Path.Combine("AppData", "Roaming", "ORBIT"), root, StringComparison.OrdinalIgnoreCase);
    }
}
