using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Library.Rekordbox;

/// <summary>One matched track whose non-empty Rekordbox values differ from ORBIT's export.</summary>
public sealed record RekordboxTrackConflict(
    string TrackId,
    string TrackLabel,
    IReadOnlyList<string> Fields,
    bool HasRekordboxCueEdits);

/// <summary>
/// A read-only preview of the existing Rekordbox data an export would replace if the user explicitly
/// approves it. Field names are UI-ready and grouped (for example, one "cues / loops" entry per track).
/// </summary>
public sealed record RekordboxExportConflictReport(IReadOnlyList<RekordboxTrackConflict> Tracks)
{
    public static readonly RekordboxExportConflictReport Empty = new(Array.Empty<RekordboxTrackConflict>());
    public bool HasConflicts => Tracks.Count > 0;
    public int TracksWithRekordboxCueEdits => Tracks.Count(track => track.HasRekordboxCueEdits);
}

/// <summary>
/// Reconciles a freshly-built Rekordbox XML export against an existing on-disk file at the same path.
/// The default is non-destructive: existing non-empty Rekordbox values win and ORBIT only fills empty
/// fields or adds new tracks. Replacing conflicting values requires the explicit overwrite option,
/// granted by the caller only after the user approves <see cref="RekordboxExportConflictReport"/>.
/// Unknown attributes/elements, TrackID, DateAdded, and unrelated playlists are always preserved.
/// Pure XML manipulation — no DB access, independently unit-testable.
/// </summary>
public static class RekordboxXmlMerger
{
    private static readonly string[] ManagedAttributes =
    {
        "Name", "Artist", "Album", "Genre", "Kind", "Size", "TotalTime",
        "BitRate", "SampleRate", "AverageBpm", "Tonality", "Location",
        "Label", "TrackNumber", "Year", "Rating", "Colour", "Comments",
    };

    private static readonly IReadOnlyDictionary<string, string> FieldLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = "title", ["Artist"] = "artist", ["Album"] = "album",
            ["Genre"] = "genre", ["Kind"] = "file type", ["Size"] = "file size",
            ["TotalTime"] = "duration", ["BitRate"] = "bit rate",
            ["SampleRate"] = "sample rate", ["AverageBpm"] = "BPM",
            ["Tonality"] = "musical key", ["Location"] = "file location",
            ["Label"] = "label", ["TrackNumber"] = "track number", ["Year"] = "year",
            ["Rating"] = "rating", ["Colour"] = "colour", ["Comments"] = "comments",
        };

    /// <param name="playlistPathChain">
    /// Root-to-leaf node names identifying the specific playlist being exported, e.g.
    /// <c>["ROOT", "MyFolder", "My Playlist"]</c> — matches the chain built by
    /// <see cref="PlaylistExportService.ResolveFolderChainNamesAsync"/>. Only this one playlist's
    /// node (and matched tracks within it) are affected; every other node in the file is untouched.
    /// </param>
    /// <param name="overwriteExisting">
    /// False (the safe default) preserves every non-empty value on matched tracks and only fills
    /// empty fields. True replaces managed values with ORBIT's fresh export and must only be used
    /// after the user approves the report from <see cref="DetectConflicts"/>. TrackID, DateAdded,
    /// unknown fields, and unrelated playlists are preserved in both modes.
    /// </param>
    public static XDocument MergeIntoExisting(
        XDocument existingDoc, XDocument freshDoc, IReadOnlyList<string> playlistPathChain, ILogger logger,
        bool overwriteExisting = false)
    {
        var result = new XDocument(existingDoc);
        var djPlaylists = result.Root;
        if (djPlaylists == null)
        {
            logger.LogWarning("Rekordbox merge: existing document has no root element — falling back to fresh export.");
            return freshDoc;
        }

        var existingCollection = djPlaylists.Element("COLLECTION");
        var freshCollection = freshDoc.Root?.Element("COLLECTION");
        if (existingCollection != null && freshCollection != null)
            MergeCollection(existingCollection, freshCollection, logger, overwriteExisting);

        var existingPlaylistsRoot = djPlaylists.Element("PLAYLISTS");
        var freshPlaylistsRoot = freshDoc.Root?.Element("PLAYLISTS");
        if (existingPlaylistsRoot != null && freshPlaylistsRoot != null && playlistPathChain.Count > 0)
            MergePlaylistNode(existingPlaylistsRoot, freshPlaylistsRoot, playlistPathChain, logger);

        return result;
    }

    /// <summary>
    /// Finds only genuine conflicts: a non-empty Rekordbox value that differs from ORBIT's fresh
    /// value. Empty Rekordbox fields are deliberately excluded because filling them is always safe.
    /// The optional cue snapshots identify Rekordbox-side cue edits so the confirmation can warn
    /// that those hand edits would also be replaced.
    /// </summary>
    public static RekordboxExportConflictReport DetectConflicts(
        XDocument existingDoc,
        XDocument freshDoc,
        IReadOnlyDictionary<string, string>? priorCueSnapshotByTrackId = null)
    {
        var existingCollection = existingDoc.Root?.Element("COLLECTION");
        var freshCollection = freshDoc.Root?.Element("COLLECTION");
        if (existingCollection == null || freshCollection == null)
            return RekordboxExportConflictReport.Empty;

        IndexTracks(existingCollection, out var existingById, out var existingByLocation);
        var conflicts = new List<RekordboxTrackConflict>();

        foreach (var freshTrack in freshCollection.Elements("TRACK"))
        {
            var matched = FindMatchedTrack(freshTrack, existingById, existingByLocation);
            if (matched == null) continue;

            var fields = ManagedAttributes
                .Where(name => HasAttributeValue(matched, name) && !AttributeValuesEqual(matched, freshTrack, name))
                .Select(name => FieldLabels[name])
                .ToList();

            var existingTempos = matched.Elements("TEMPO").ToList();
            if (existingTempos.Any(HasMeaningfulTempo)
                && CanonicalizeTempos(existingTempos) != CanonicalizeTempos(freshTrack.Elements("TEMPO")))
                fields.Add("beat grid");

            var existingCues = CanonicalizeCues(matched.Elements("POSITION_MARK"));
            var hasCueConflict = matched.Elements("POSITION_MARK").Any()
                && existingCues != CanonicalizeCues(freshTrack.Elements("POSITION_MARK"));
            if (hasCueConflict) fields.Add("cues / loops");

            if (fields.Count == 0) continue;

            var freshId = (string?)freshTrack.Attribute("TrackID");
            var hasRekordboxCueEdits = hasCueConflict
                && freshId != null
                && priorCueSnapshotByTrackId != null
                && priorCueSnapshotByTrackId.TryGetValue(freshId, out var priorSnapshot)
                && priorSnapshot != existingCues;
            var trackId = (string?)matched.Attribute("TrackID") ?? freshId ?? "";
            conflicts.Add(new RekordboxTrackConflict(
                trackId, TrackLabel(matched, freshTrack, trackId), fields, hasRekordboxCueEdits));
        }

        return conflicts.Count == 0 ? RekordboxExportConflictReport.Empty : new RekordboxExportConflictReport(conflicts);
    }

    private static void MergeCollection(
        XElement existingCollection, XElement freshCollection, ILogger logger, bool overwriteExisting)
    {
        IndexTracks(existingCollection, out var existingById, out var existingByLocation);

        foreach (var freshTrack in freshCollection.Elements("TRACK"))
        {
            var matched = FindMatchedTrack(freshTrack, existingById, existingByLocation);
            if (matched == null)
            {
                existingCollection.Add(new XElement(freshTrack));
                continue;
            }

            var freshId = (string?)freshTrack.Attribute("TrackID");
            var existingId = (string?)matched.Attribute("TrackID");
            if (!string.IsNullOrEmpty(freshId) && !string.Equals(freshId, existingId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Rekordbox merge: TrackID mismatch for matched track at Location '{Location}' (existing={ExistingId}, fresh={FreshId}) — keeping the existing TrackID so other playlists' references stay valid.",
                    (string?)freshTrack.Attribute("Location"), existingId, freshId);
            }

            PatchTrack(matched, freshTrack, overwriteExisting);
        }

        existingCollection.SetAttributeValue("Entries", existingCollection.Elements("TRACK").Count());
    }

    private static void IndexTracks(
        XElement collection,
        out Dictionary<string, XElement> byId,
        out Dictionary<string, XElement> byLocation)
    {
        byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        byLocation = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in collection.Elements("TRACK"))
        {
            var id = (string?)track.Attribute("TrackID");
            var location = (string?)track.Attribute("Location");
            if (!string.IsNullOrEmpty(id)) byId.TryAdd(id, track);
            if (!string.IsNullOrEmpty(location)) byLocation.TryAdd(location, track);
        }
    }

    private static XElement? FindMatchedTrack(
        XElement freshTrack,
        IReadOnlyDictionary<string, XElement> existingById,
        IReadOnlyDictionary<string, XElement> existingByLocation)
    {
        var freshId = (string?)freshTrack.Attribute("TrackID");
        var freshLocation = (string?)freshTrack.Attribute("Location");
        if (!string.IsNullOrEmpty(freshId) && existingById.TryGetValue(freshId, out var byId)) return byId;
        if (!string.IsNullOrEmpty(freshLocation) && existingByLocation.TryGetValue(freshLocation, out var byLocation)) return byLocation;
        return null;
    }

    private static void PatchTrack(XElement existingTrack, XElement freshTrack, bool overwriteExisting)
    {
        foreach (var attrName in ManagedAttributes)
        {
            if (overwriteExisting)
                existingTrack.SetAttributeValue(attrName, freshTrack.Attribute(attrName)?.Value);
            else
                FillIfBlank(existingTrack, freshTrack, attrName);
        }

        if (overwriteExisting || !existingTrack.Elements("TEMPO").Any(HasMeaningfulTempo))
        {
            existingTrack.Elements("TEMPO").Remove();
            foreach (var tempo in freshTrack.Elements("TEMPO"))
                existingTrack.Add(new XElement(tempo));
        }

        if (overwriteExisting || !existingTrack.Elements("POSITION_MARK").Any())
        {
            existingTrack.Elements("POSITION_MARK").Remove();
            foreach (var mark in freshTrack.Elements("POSITION_MARK"))
                existingTrack.Add(new XElement(mark));
        }
    }

    private static string TrackLabel(XElement existingTrack, XElement freshTrack, string trackId)
    {
        var artist = (string?)freshTrack.Attribute("Artist");
        if (string.IsNullOrWhiteSpace(artist)) artist = (string?)existingTrack.Attribute("Artist");
        var name = (string?)freshTrack.Attribute("Name");
        if (string.IsNullOrWhiteSpace(name)) name = (string?)existingTrack.Attribute("Name");
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(name))
            return string.IsNullOrWhiteSpace(trackId) ? "Unknown track" : $"Track {trackId}";
        return string.Join(" — ", new[] { artist, name }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static bool HasAttributeValue(XElement track, string attributeName)
    {
        var value = (string?)track.Attribute(attributeName);
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!NumericAttributes.Contains(attributeName)) return true;
        return !double.TryParse(value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var number)
               || number > 0;
    }

    private static bool AttributeValuesEqual(XElement existingTrack, XElement freshTrack, string attributeName)
    {
        var existing = (string?)existingTrack.Attribute(attributeName);
        var fresh = (string?)freshTrack.Attribute(attributeName);
        if (NumericAttributes.Contains(attributeName)
            && double.TryParse(existing, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var oldNumber)
            && double.TryParse(fresh, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var freshNumber))
            return Math.Abs(oldNumber - freshNumber) < 0.0005;
        return string.Equals(existing, fresh, StringComparison.Ordinal);
    }

    private static readonly HashSet<string> NumericAttributes = new(StringComparer.Ordinal)
    {
        "Size", "TotalTime", "BitRate", "SampleRate", "AverageBpm",
        "TrackNumber", "Year", "Rating",
    };

    private static bool HasMeaningfulTempo(XElement tempo) =>
        double.TryParse((string?)tempo.Attribute("Bpm"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var bpm) && bpm > 0;

    private static string CanonicalizeTempos(IEnumerable<XElement> tempos) =>
        string.Join("|", tempos.Select(tempo => string.Join(":",
                FormatNumericAttribute(tempo, "Inizio"),
                FormatNumericAttribute(tempo, "Bpm"),
                (string?)tempo.Attribute("Metro") ?? "",
                (string?)tempo.Attribute("Battito") ?? ""))
            .OrderBy(value => value, StringComparer.Ordinal));

    /// <summary>
    /// Builds an order-independent, formatting-tolerant fingerprint of a track's POSITION_MARK set
    /// so two cue lists can be compared for genuine semantic equality regardless of attribute order
    /// or minor float-formatting differences a re-save might introduce.
    /// </summary>
    internal static string CanonicalizeCues(IEnumerable<XElement> marks) =>
        string.Join("|", marks
            .Select(m => string.Join(":",
                (string?)m.Attribute("Type") ?? "",
                FormatNumericAttribute(m, "Start"),
                FormatNumericAttribute(m, "End"),
                (string?)m.Attribute("Num") ?? "",
                (string?)m.Attribute("Name") ?? "",
                (string?)m.Attribute("Red") ?? "",
                (string?)m.Attribute("Green") ?? "",
                (string?)m.Attribute("Blue") ?? ""))
            .OrderBy(s => s, StringComparer.Ordinal));

    private static string FormatNumericAttribute(XElement elem, string attributeName)
    {
        var raw = (string?)elem.Attribute(attributeName);
        if (raw == null) return "";
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val)
            ? val.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            : raw;
    }

    private static void FillIfBlank(XElement existingTrack, XElement freshTrack, string attributeName)
    {
        if (HasAttributeValue(existingTrack, attributeName)) return;
        var freshAttr = freshTrack.Attribute(attributeName);
        if (freshAttr != null)
            existingTrack.SetAttributeValue(attributeName, freshAttr.Value);
    }

    /// <summary>
    /// Locates the specific playlist node (by <paramref name="pathChain"/>) in both trees, replacing
    /// the existing leaf in place (or inserting the missing tail) — every sibling node at every
    /// level is left completely untouched.
    /// </summary>
    private static void MergePlaylistNode(
        XElement existingPlaylistsRoot, XElement freshPlaylistsRoot, IReadOnlyList<string> pathChain, ILogger logger)
    {
        var freshLeaf = FindNodeByChain(freshPlaylistsRoot, pathChain);
        if (freshLeaf == null)
        {
            logger.LogWarning(
                "Rekordbox merge: could not locate the freshly-built playlist node for chain [{Chain}] — skipping playlist merge.",
                string.Join(" > ", pathChain));
            return;
        }

        var current = existingPlaylistsRoot.Elements("NODE")
            .FirstOrDefault(n => string.Equals((string?)n.Attribute("Name"), pathChain[0], StringComparison.Ordinal));

        if (current == null)
        {
            logger.LogWarning(
                "Rekordbox merge: existing file has no '{Name}' node under PLAYLISTS — skipping playlist merge.",
                pathChain[0]);
            return;
        }

        for (int i = 1; i < pathChain.Count - 1; i++)
        {
            var folderName = pathChain[i];
            var next = current.Elements("NODE")
                .FirstOrDefault(n => (string?)n.Attribute("Type") == "0"
                    && string.Equals((string?)n.Attribute("Name"), folderName, StringComparison.Ordinal));

            if (next == null)
            {
                next = new XElement("NODE", new XAttribute("Type", "0"), new XAttribute("Name", folderName));
                current.Add(next);
            }
            current = next;
        }

        var playlistName = pathChain[^1];
        var existingLeaf = current.Elements("NODE")
            .FirstOrDefault(n => (string?)n.Attribute("Type") == "1"
                && string.Equals((string?)n.Attribute("Name"), playlistName, StringComparison.Ordinal));

        var freshLeafClone = new XElement(freshLeaf);
        if (existingLeaf != null)
            existingLeaf.ReplaceWith(freshLeafClone);
        else
            current.Add(freshLeafClone);
    }

    private static XElement? FindNodeByChain(XElement playlistsRoot, IReadOnlyList<string> pathChain)
    {
        XElement? current = playlistsRoot.Elements("NODE")
            .FirstOrDefault(n => string.Equals((string?)n.Attribute("Name"), pathChain[0], StringComparison.Ordinal));
        if (current == null) return null;

        for (int i = 1; i < pathChain.Count; i++)
        {
            current = current.Elements("NODE")
                .FirstOrDefault(n => string.Equals((string?)n.Attribute("Name"), pathChain[i], StringComparison.Ordinal));
            if (current == null) return null;
        }

        return current;
    }
}
