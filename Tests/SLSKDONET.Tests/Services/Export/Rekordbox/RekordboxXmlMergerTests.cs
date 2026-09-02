using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Library.Rekordbox;
using Xunit;

namespace SLSKDONET.Tests.Services.Export.Rekordbox;

/// <summary>
/// Pure unit tests for <see cref="RekordboxXmlMerger"/> against hand-built XDocuments — no DB,
/// no file I/O. Covers the field-ownership rules from the Phase 2 merge-mode design: ORBIT-owned
/// data always refreshes, user-editable-in-Rekordbox fields are preserved when already present.
/// </summary>
public class RekordboxXmlMergerTests
{
    private static XElement BuildTrack(
        string trackId, string location, string name = "Track", string bpm = "128.00",
        string? rating = "255", string? colour = null, string? comments = "", bool withCue = false)
    {
        var elem = new XElement("TRACK",
            new XAttribute("TrackID", trackId),
            new XAttribute("Name", name),
            new XAttribute("Artist", "Artist"),
            new XAttribute("Album", "Album"),
            new XAttribute("Genre", "Genre"),
            new XAttribute("Kind", "MP3 File"),
            new XAttribute("Size", "1000"),
            new XAttribute("TotalTime", "200"),
            new XAttribute("DateAdded", "2020-01-01"),
            new XAttribute("BitRate", "320"),
            new XAttribute("SampleRate", "44100"),
            new XAttribute("AverageBpm", bpm),
            new XAttribute("Tonality", "8B"),
            new XAttribute("Location", location));

        if (rating != null) elem.Add(new XAttribute("Rating", rating));
        if (colour != null) elem.Add(new XAttribute("Colour", colour));
        if (comments != null) elem.Add(new XAttribute("Comments", comments));

        elem.Add(new XElement("TEMPO",
            new XAttribute("Inizio", "0.000"), new XAttribute("Bpm", bpm),
            new XAttribute("Metro", "4/4"), new XAttribute("Battito", "1")));

        if (withCue)
        {
            elem.Add(new XElement("POSITION_MARK",
                new XAttribute("Name", "Drop"), new XAttribute("Type", "0"),
                new XAttribute("Start", "1.000"), new XAttribute("Num", "0"),
                new XAttribute("Red", "255"), new XAttribute("Green", "0"), new XAttribute("Blue", "0")));
        }

        return elem;
    }

    private static XDocument BuildDoc(
        XElement[] tracks, string playlistName, XElement[]? playlistTracksOverride = null, string[]? folderChain = null)
    {
        XElement node = new XElement("NODE",
            new XAttribute("Name", playlistName),
            new XAttribute("Type", "1"),
            new XAttribute("Entries", (playlistTracksOverride ?? tracks).Length),
            (playlistTracksOverride ?? tracks).Select(t => new XElement("TRACK", new XAttribute("Key", (string)t.Attribute("TrackID")!))));

        foreach (var folderName in (folderChain ?? Array.Empty<string>()).Reverse())
        {
            node = new XElement("NODE", new XAttribute("Type", "0"), new XAttribute("Name", folderName), node);
        }

        return new XDocument(
            new XElement("DJ_PLAYLISTS",
                new XAttribute("Version", "1.0.0"),
                new XElement("COLLECTION", new XAttribute("Entries", tracks.Length), tracks),
                new XElement("PLAYLISTS",
                    new XElement("NODE", new XAttribute("Type", "0"), new XAttribute("Name", "ROOT"), node))));
    }

    private static XElement? FindTrack(XDocument doc, string trackId) =>
        doc.Root!.Element("COLLECTION")!.Elements("TRACK")
            .FirstOrDefault(t => (string?)t.Attribute("TrackID") == trackId);

    [Fact]
    public void Merge_NewTrack_IsAppendedWithoutDisturbingExisting()
    {
        var existing = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3") }, "MyPlaylist");
        var fresh = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3"), BuildTrack("2", @"C:\b.mp3") }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);

        Assert.Equal(2, merged.Root!.Element("COLLECTION")!.Elements("TRACK").Count());
        Assert.NotNull(FindTrack(merged, "2"));
        Assert.Equal("2", merged.Root.Element("COLLECTION")!.Attribute("Entries")!.Value);
    }

    [Fact]
    public void Merge_MatchedTrack_PreservesExistingRatingColourCommentsAndCues()
    {
        var existing = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3", rating: "255", colour: "FF0000", comments: "hand-edited note", withCue: true) }, "MyPlaylist");
        var fresh = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3", rating: "51", colour: "0000FF", comments: "orbit note", withCue: false) }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);
        var track = FindTrack(merged, "1")!;

        Assert.Equal("255", (string)track.Attribute("Rating")!);
        Assert.Equal("FF0000", (string)track.Attribute("Colour")!);
        Assert.Equal("hand-edited note", (string)track.Attribute("Comments")!);
        Assert.Single(track.Elements("POSITION_MARK"));
    }

    [Fact]
    public void Merge_MatchedTrack_FillsColourCommentsCuesFromFreshWhenAbsentOnDisk()
    {
        var existing = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3", colour: null, comments: "", withCue: false) }, "MyPlaylist");
        var fresh = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3", colour: "0000FF", comments: "orbit note", withCue: true) }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);
        var track = FindTrack(merged, "1")!;

        Assert.Equal("0000FF", (string)track.Attribute("Colour")!);
        Assert.Equal("orbit note", (string)track.Attribute("Comments")!);
        Assert.Single(track.Elements("POSITION_MARK"));
    }

    [Fact]
    public void Merge_MatchedTrack_AlwaysRefreshesTempoLocationAndFileMetadata()
    {
        var existing = BuildDoc(new[] { BuildTrack("1", @"C:\old-path.mp3", bpm: "120.00", name: "Old Name") }, "MyPlaylist");
        var fresh = BuildDoc(new[] { BuildTrack("1", @"C:\old-path.mp3", bpm: "128.00", name: "New Name") }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);
        var track = FindTrack(merged, "1")!;

        Assert.Equal("New Name", (string)track.Attribute("Name")!);
        Assert.Equal("128.00", (string)track.Attribute("AverageBpm")!);
        Assert.Equal("128.00", (string)track.Element("TEMPO")!.Attribute("Bpm")!);
    }

    [Fact]
    public void Merge_NeverTouchesTrackIdOrDateAdded()
    {
        var existing = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3") }, "MyPlaylist");
        existing.Root!.Element("COLLECTION")!.Elements("TRACK").First().SetAttributeValue("DateAdded", "2019-05-05");
        var fresh = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3") }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);
        var track = FindTrack(merged, "1")!;

        Assert.Equal("1", (string)track.Attribute("TrackID")!);
        Assert.Equal("2019-05-05", (string)track.Attribute("DateAdded")!);
    }

    [Fact]
    public void Merge_SiblingPlaylistsAndFolders_AreLeftUntouched()
    {
        var existing = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3") }, "MyPlaylist");
        // Add an unrelated sibling folder + playlist that this export knows nothing about.
        var root = existing.Root!.Element("PLAYLISTS")!.Element("NODE")!; // ROOT
        root.Add(new XElement("NODE", new XAttribute("Type", "0"), new XAttribute("Name", "OtherFolder"),
            new XElement("NODE", new XAttribute("Type", "1"), new XAttribute("Name", "OtherPlaylist"), new XAttribute("Entries", "0"))));

        var fresh = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3"), BuildTrack("2", @"C:\b.mp3") }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);
        var mergedRoot = merged.Root!.Element("PLAYLISTS")!.Element("NODE")!;

        var otherFolder = mergedRoot.Elements("NODE").FirstOrDefault(n => (string?)n.Attribute("Name") == "OtherFolder");
        Assert.NotNull(otherFolder);
        Assert.NotNull(otherFolder!.Elements("NODE").FirstOrDefault(n => (string?)n.Attribute("Name") == "OtherPlaylist"));
    }

    [Fact]
    public void Merge_PlaylistLeafNode_ReplacedWithFreshMembership()
    {
        var existing = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3") }, "MyPlaylist");
        var fresh = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3"), BuildTrack("2", @"C:\b.mp3") }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);
        var root = merged.Root!.Element("PLAYLISTS")!.Element("NODE")!;
        var playlistNode = root.Elements("NODE").Single(n => (string?)n.Attribute("Name") == "MyPlaylist");

        Assert.Equal("2", (string)playlistNode.Attribute("Entries")!);
        Assert.Equal(2, playlistNode.Elements("TRACK").Count());
    }

    [Fact]
    public void Merge_MissingFolderChainTail_IsCreated()
    {
        // Existing file has no folder wrapper at all yet — export now specifies one.
        var existing = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3") }, "SomeOtherPlaylist");
        var fresh = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3") }, "MyPlaylist", folderChain: new[] { "NewFolder" });

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "NewFolder", "MyPlaylist" }, NullLogger.Instance);
        var root = merged.Root!.Element("PLAYLISTS")!.Element("NODE")!;

        var newFolder = root.Elements("NODE").FirstOrDefault(n => (string?)n.Attribute("Name") == "NewFolder");
        Assert.NotNull(newFolder);
        Assert.NotNull(newFolder!.Elements("NODE").FirstOrDefault(n => (string?)n.Attribute("Name") == "MyPlaylist"));

        // The pre-existing flat playlist is untouched.
        Assert.NotNull(root.Elements("NODE").FirstOrDefault(n => (string?)n.Attribute("Name") == "SomeOtherPlaylist"));
    }

    [Fact]
    public void Merge_ExistingDocWithNoRoot_FallsBackToFreshWithoutThrowing()
    {
        var existing = new XDocument(); // no root element at all
        var fresh = BuildDoc(new[] { BuildTrack("1", @"C:\a.mp3") }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);

        Assert.NotNull(merged.Root);
        Assert.NotNull(FindTrack(merged, "1"));
    }

    [Fact]
    public void Merge_TrackIdMismatchButLocationMatch_KeepsExistingTrackId()
    {
        var existing = BuildDoc(new[] { BuildTrack("111", @"C:\a.mp3") }, "MyPlaylist");
        var fresh = BuildDoc(new[] { BuildTrack("999", @"C:\a.mp3") }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);

        Assert.Single(merged.Root!.Element("COLLECTION")!.Elements("TRACK"));
        Assert.Equal("111", (string)merged.Root.Element("COLLECTION")!.Elements("TRACK").Single().Attribute("TrackID")!);
    }

    private static XElement BuildCue(string name, string start, string num = "0") =>
        new("POSITION_MARK",
            new XAttribute("Name", name), new XAttribute("Type", "0"),
            new XAttribute("Start", start), new XAttribute("Num", num),
            new XAttribute("Red", "255"), new XAttribute("Green", "0"), new XAttribute("Blue", "0"));

    [Fact]
    public void Merge_CueSnapshotMatchesPrior_WritesThroughFreshCues()
    {
        // Existing on-disk cues are exactly what ORBIT wrote last time (per the prior snapshot) —
        // nothing changed in Rekordbox, so a genuine ORBIT-side cue edit should propagate.
        var existingTrack = BuildTrack("1", @"C:\a.mp3", withCue: false);
        existingTrack.Add(BuildCue("Old Drop", "10.000"));
        var existing = BuildDoc(new[] { existingTrack }, "MyPlaylist");

        var freshTrack = BuildTrack("1", @"C:\a.mp3", withCue: false);
        freshTrack.Add(BuildCue("New Drop", "20.000"));
        var fresh = BuildDoc(new[] { freshTrack }, "MyPlaylist");

        var priorSnapshot = RekordboxXmlMerger.CanonicalizeCues(existingTrack.Elements("POSITION_MARK"));
        var priorByTrackId = new Dictionary<string, string> { ["1"] = priorSnapshot };

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance, priorByTrackId);
        var track = FindTrack(merged, "1")!;

        Assert.Single(track.Elements("POSITION_MARK"));
        Assert.Equal("New Drop", (string)track.Elements("POSITION_MARK").Single().Attribute("Name")!);
    }

    [Fact]
    public void Merge_CueSnapshotDiffersFromPrior_PreservesExistingHandEdit()
    {
        // On-disk cues no longer match what ORBIT last wrote (a name/position changed) — presumed
        // hand-edited in Rekordbox since then, so ORBIT's fresh cues must NOT overwrite them.
        var trackAtLastSync = BuildTrack("1", @"C:\a.mp3", withCue: false);
        trackAtLastSync.Add(BuildCue("Old Drop", "10.000"));
        var priorSnapshot = RekordboxXmlMerger.CanonicalizeCues(trackAtLastSync.Elements("POSITION_MARK"));

        var existingTrack = BuildTrack("1", @"C:\a.mp3", withCue: false);
        existingTrack.Add(BuildCue("Hand-Renamed In Rekordbox", "10.000")); // user changed it since
        var existing = BuildDoc(new[] { existingTrack }, "MyPlaylist");

        var freshTrack = BuildTrack("1", @"C:\a.mp3", withCue: false);
        freshTrack.Add(BuildCue("New Drop", "20.000"));
        var fresh = BuildDoc(new[] { freshTrack }, "MyPlaylist");

        var priorByTrackId = new Dictionary<string, string> { ["1"] = priorSnapshot };

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance, priorByTrackId);
        var track = FindTrack(merged, "1")!;

        Assert.Single(track.Elements("POSITION_MARK"));
        Assert.Equal("Hand-Renamed In Rekordbox", (string)track.Elements("POSITION_MARK").Single().Attribute("Name")!);
    }

    [Fact]
    public void Merge_NoPriorSnapshotSupplied_FallsBackToPreservingExistingCues()
    {
        // No snapshot info at all (e.g. an older caller, or nothing recorded yet for this track) —
        // must degrade to the original conservative all-or-nothing rule, not silently overwrite.
        var existingTrack = BuildTrack("1", @"C:\a.mp3", withCue: false);
        existingTrack.Add(BuildCue("Existing Cue", "10.000"));
        var existing = BuildDoc(new[] { existingTrack }, "MyPlaylist");

        var freshTrack = BuildTrack("1", @"C:\a.mp3", withCue: false);
        freshTrack.Add(BuildCue("Fresh Cue", "20.000"));
        var fresh = BuildDoc(new[] { freshTrack }, "MyPlaylist");

        var merged = RekordboxXmlMerger.MergeIntoExisting(existing, fresh, new[] { "ROOT", "MyPlaylist" }, NullLogger.Instance);
        var track = FindTrack(merged, "1")!;

        Assert.Equal("Existing Cue", (string)track.Elements("POSITION_MARK").Single().Attribute("Name")!);
    }
}
