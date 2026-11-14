using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Sets;
using SLSKDONET.ViewModels.Sets;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.Services.Sets;

/// <summary>#205 P4.4: "Check clashes" — sections, vocals in the mix window, same artist twice.</summary>
public sealed class SetClashCheckTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("clashes");

    public void Dispose() => _db.Dispose();

    private static ClashSlot S(int n, string hash, string artist, double? bpm = 174) =>
        new(Guid.NewGuid(), n.ToString("00"), hash, "T" + hash, artist, bpm);

    private static readonly IReadOnlyDictionary<string, TrackMixData> NoMix = new Dictionary<string, TrackMixData>();

    [Fact]
    public void WeakSectionFit_AndSameArtist_AreClashes_OnlyMeasuredPairsCount()
    {
        var slots = new[] { S(1, "a", "Noisia"), S(2, "b", "noisia "), S(3, "c", "Mefjus") };
        var fits = new Dictionary<(string, string), double> { [("a", "b")] = 0.9, [("b", "c")] = 0.3 };

        var r = SetClashCheck.Run(slots, NoMix, (x, y) => fits.TryGetValue((x, y), out var f) ? f : null);

        Assert.Equal(2, r.Clashes.Count);
        Assert.Contains(r.Clashes, c => c.ToSlotId == slots[1].SlotId && c.Text == "01 → 02: Noisia twice in a row");
        Assert.Contains(r.Clashes, c => c.ToSlotId == slots[2].SlotId && c.Text.Contains("outro and intro sound very different (fit 30)"));
        Assert.Equal(2, r.SectionChecked);
        Assert.Equal(0, r.VocalChecked);
        Assert.Equal("2 clashes · sections · vocals: no vocal data · artists", r.Summary);
    }

    [Fact]
    public void Vocals_ClashOnlyInsideTheMixWindow_AndNeedBothSpans()
    {
        // 174 BPM: 16 bars ≈ 22 s. A ends at 300 s; B mixes in at 0 s.
        var singsToTheEnd = new TrackMixData(300, null, null, null, 30, 295);
        var stopsEarly = new TrackMixData(300, null, null, null, 30, 200);
        var singsAtOnce = new TrackMixData(300, null, null, null, 5, 250);
        var singsLate = new TrackMixData(300, null, null, null, 60, 250);

        Assert.True(SetClashCheck.VocalsOverlap(singsToTheEnd, singsAtOnce, 174));
        Assert.False(SetClashCheck.VocalsOverlap(stopsEarly, singsAtOnce, 174));
        Assert.False(SetClashCheck.VocalsOverlap(singsToTheEnd, singsLate, 174));
        // A MixOut cue at 210 s moves the window back over A's vocals that end at 200 s.
        Assert.True(SetClashCheck.VocalsOverlap(stopsEarly with { MixOutSeconds = 210 }, singsAtOnce, 174));

        var slots = new[] { S(1, "a", "X"), S(2, "b", "Y"), S(3, "c", "Z") };
        var mix = new Dictionary<string, TrackMixData> { ["a"] = singsToTheEnd, ["b"] = singsAtOnce, ["c"] = new(300, null, null, null, null, null) };
        var r = SetClashCheck.Run(slots, mix, (_, _) => null);
        Assert.Equal(1, r.VocalChecked); // b → c: c has no vocal span, so it isn't checked
        Assert.Equal("01 → 02: both tracks have vocals in the mix", Assert.Single(r.Clashes).Text);
        Assert.Equal("1 clash · sections: no phrase data · vocals 1/2 · artists", r.Summary);
    }

    [Fact]
    public async Task MixData_ReadsLengthCuesAndVocals_FromTheLibrary()
    {
        _db.SeedLibrary(("a", "X", "One"), ("b", "Y", "Two"));
        await using (var db = _db.Factory.CreateDbContext())
        {
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = "a", Bpm = 174, TrackDuration = 320, VocalStartSeconds = 40, VocalEndSeconds = 280 });
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = "b", Bpm = 174, TrackDuration = 300 });
            db.CuePoints.AddRange(
                new CuePointEntity { TrackUniqueHash = "a", Type = CuePointType.MixOut, TimestampInSeconds = 290 },
                new CuePointEntity { TrackUniqueHash = "a", Type = CuePointType.Intro, TimestampInSeconds = 0.5 },
                new CuePointEntity { TrackUniqueHash = "a", Type = CuePointType.Drop, TimestampInSeconds = 60 });
            await db.SaveChangesAsync();
        }

        var mix = await new SetProjectService(_db.Factory).GetMixDataAsync(new[] { "a", "b", "missing" });

        Assert.Equal(new TrackMixData(320, 0.5, 290, null, 40, 280), mix["a"]);
        Assert.Equal(new TrackMixData(300, null, null, null, null, null), mix["b"]);
        Assert.False(mix.ContainsKey("missing"));
    }

    [Fact]
    public async Task CheckClashes_AddsIssuesToSetHealth_AndDropsThemWhenTheSetChanges()
    {
        _db.SeedLibrary(("h1", "Noisia", "One"), ("h2", "Noisia", "Two"), ("h3", "Mefjus", "Three"));
        var service = new SetProjectService(_db.Factory);
        var set = await service.CreateSetAsync("Friday");
        foreach (var h in new[] { "h1", "h2", "h3" })
            await service.AppendTrackAsync(set.Id, h);

        var vm = new SetsViewModel(service, new Mock<IDialogService>().Object, new Mock<INotificationService>().Object);
        await vm.LoadAsync();
        vm.SelectedSet = vm.Sets.Single();
        await vm.PendingSlotLoad;

        await vm.CheckClashesAsync();
        var issue = Assert.Single(vm.Health.Issues, i => i.Text.EndsWith("Noisia twice in a row"));
        Assert.Equal(vm.Slots[1].SlotId, issue.SlotId);
        Assert.StartsWith("1 clash", vm.ClashStatus);

        await vm.MoveSlotAsync(vm.Slots[1].SlotId, 2); // h1, h3, h2: no longer back to back
        Assert.DoesNotContain(vm.Health.Issues, i => i.Text.Contains("twice in a row"));
        Assert.Equal("The set changed: check clashes again.", vm.ClashStatus);
    }
}
