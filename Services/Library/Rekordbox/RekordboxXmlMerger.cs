using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Library.Rekordbox;

/// <summary>
/// Reconciles a freshly-built Rekordbox XML export against an existing on-disk file at the same
/// path, so re-exporting a playlist doesn't destroy edits a user made directly inside Rekordbox
/// since the last export (Rating, Colour, Comments, hot cues). ORBIT-owned/file-derived data
/// (tempo grid, file metadata, brand-new tracks) always refreshes from the fresh export regardless.
/// Pure XML manipulation — no DB access, independently unit-testable.
/// </summary>
public static class RekordboxXmlMerger
{
    private static readonly string[] AlwaysRefreshAttributes =
    {
        "Name", "Artist", "Album", "Genre", "Kind", "Size", "TotalTime",
        "BitRate", "SampleRate", "AverageBpm", "Tonality", "Location",
        "Label", "TrackNumber", "Year",
    };

    /// <param name="playlistPathChain">
    /// Root-to-leaf node names identifying the specific playlist being exported, e.g.
    /// <c>["ROOT", "MyFolder", "My Playlist"]</c> — matches the chain built by
    /// <see cref="PlaylistExportService.ResolveFolderChainNamesAsync"/>. Only this one playlist's
    /// node (and matched tracks within it) are affected; every other node in the file is untouched.
    /// </param>
    public static XDocument MergeIntoExisting(
        XDocument existingDoc, XDocument freshDoc, IReadOnlyList<string> playlistPathChain, ILogger logger)
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
        {
            MergeCollection(existingCollection, freshCollection, logger);
        }

        var existingPlaylistsRoot = djPlaylists.Element("PLAYLISTS");
        var freshPlaylistsRoot = freshDoc.Root?.Element("PLAYLISTS");
        if (existingPlaylistsRoot != null && freshPlaylistsRoot != null && playlistPathChain.Count > 0)
        {
            MergePlaylistNode(existingPlaylistsRoot, freshPlaylistsRoot, playlistPathChain, logger);
        }

        return result;
    }

    private static void MergeCollection(XElement existingCollection, XElement freshCollection, ILogger logger)
    {
        var existingById = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var existingByLocation = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in existingCollection.Elements("TRACK"))
        {
            var id = (string?)track.Attribute("TrackID");
            var loc = (string?)track.Attribute("Location");
            if (!string.IsNullOrEmpty(id)) existingById.TryAdd(id, track);
            if (!string.IsNullOrEmpty(loc)) existingByLocation.TryAdd(loc, track);
        }

        foreach (var freshTrack in freshCollection.Elements("TRACK"))
        {
            var freshId = (string?)freshTrack.Attribute("TrackID");
            var freshLoc = (string?)freshTrack.Attribute("Location");

            XElement? matched = null;
            if (!string.IsNullOrEmpty(freshId) && existingById.TryGetValue(freshId, out var byId))
                matched = byId;
            else if (!string.IsNullOrEmpty(freshLoc) && existingByLocation.TryGetValue(freshLoc, out var byLoc))
                matched = byLoc;

            if (matched == null)
            {
                existingCollection.Add(new XElement(freshTrack));
                continue;
            }

            var existingId = (string?)matched.Attribute("TrackID");
            if (!string.IsNullOrEmpty(freshId) && !string.Equals(freshId, existingId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Rekordbox merge: TrackID mismatch for matched track at Location '{Location}' (existing={ExistingId}, fresh={FreshId}) — keeping the existing TrackID so other playlists' references stay valid.",
                    freshLoc, existingId, freshId);
            }

            PatchTrackAttributes(matched, freshTrack);
        }

        existingCollection.SetAttributeValue("Entries", existingCollection.Elements("TRACK").Count());
    }

    /// <summary>
    /// Patches one matched &lt;TRACK&gt; in place: file/analysis-derived attributes and the tempo
    /// grid always refresh from <paramref name="freshTrack"/>; Rating/Colour/Comments/cues are kept
    /// as-is when the existing element already has them (only filled from fresh when absent);
    /// TrackID and DateAdded are never touched.
    /// </summary>
    private static void PatchTrackAttributes(XElement existingTrack, XElement freshTrack)
    {
        foreach (var attrName in AlwaysRefreshAttributes)
        {
            var freshAttr = freshTrack.Attribute(attrName);
            if (freshAttr != null)
                existingTrack.SetAttributeValue(attrName, freshAttr.Value);
        }

        FillIfAttributeMissing(existingTrack, freshTrack, "Rating");
        FillIfAttributeMissing(existingTrack, freshTrack, "Colour");
        FillIfBlank(existingTrack, freshTrack, "Comments");

        existingTrack.Elements("TEMPO").Remove();
        foreach (var tempo in freshTrack.Elements("TEMPO"))
            existingTrack.Add(new XElement(tempo));

        // All-or-nothing: only replace cues if the existing track has none at all.
        if (!existingTrack.Elements("POSITION_MARK").Any())
        {
            foreach (var mark in freshTrack.Elements("POSITION_MARK"))
                existingTrack.Add(new XElement(mark));
        }
    }

    private static void FillIfAttributeMissing(XElement existingTrack, XElement freshTrack, string attributeName)
    {
        if (existingTrack.Attribute(attributeName) != null) return;
        var freshAttr = freshTrack.Attribute(attributeName);
        if (freshAttr != null)
            existingTrack.SetAttributeValue(attributeName, freshAttr.Value);
    }

    private static void FillIfBlank(XElement existingTrack, XElement freshTrack, string attributeName)
    {
        var existingAttr = existingTrack.Attribute(attributeName);
        if (existingAttr != null && !string.IsNullOrWhiteSpace(existingAttr.Value)) return;
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
