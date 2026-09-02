using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Library.Rekordbox;
using SLSKDONET.Services.Models.Export;

namespace SLSKDONET.Services.Library;

public class PlaylistExportService
{
    private readonly ILogger<PlaylistExportService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PlaylistExportService(
        ILogger<PlaylistExportService> logger,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <param name="pathMap">
    /// Optional override map: local source path → destination path.
    /// When supplied (USB/folder export) the XML Location attribute uses the
    /// destination path instead of the original local path.
    /// </param>
    /// <param name="folderId">
    /// Optional playlist-folder ID (<see cref="PlaylistFolder"/>) the playlist belongs to.
    /// When supplied, the exported &lt;PLAYLISTS&gt; tree mirrors the real folder chain up to
    /// ROOT instead of placing the playlist directly under ROOT.
    /// </param>
    /// <remarks>
    /// If <paramref name="targetPath"/> already exists and parses as a Rekordbox XML file, this
    /// merges into it via <see cref="RekordboxXmlMerger"/> instead of overwriting — preserving
    /// Rating/Colour/Comments/cues a user may have edited directly in Rekordbox since the last
    /// export, while still refreshing ORBIT-owned data (tempo grid, file metadata, new tracks).
    /// Falls back to a full overwrite if the existing file can't be parsed as Rekordbox XML.
    /// </remarks>
    public async Task ExportToRekordboxXmlAsync(
        string playlistName,
        IEnumerable<PlaylistTrack> tracks,
        string targetPath,
        IReadOnlyDictionary<string, string>? pathMap = null,
        Guid? folderId = null)
    {
        try
        {
            _logger.LogInformation("Exporting playlist '{PlaylistName}' to Rekordbox XML: {Path}", playlistName, targetPath);

            // Deduplicate by ResolvedFilePath so the same physical file never appears twice in the XML.
            // A playlist can have multiple PlaylistTrack rows pointing to the same file when a track was
            // re-imported or synced more than once. Keep only the first occurrence per path.
            var trackList = tracks
                .GroupBy(
                    t => string.IsNullOrEmpty(t.ResolvedFilePath)
                        ? t.TrackUniqueHash ?? t.Id.ToString()   // fall back to hash for unresolved tracks
                        : t.ResolvedFilePath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // Pre-load all cue points + beatgrid data + playlist folders in one DB context.
            var hashes = trackList.Select(t => t.TrackUniqueHash).Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
            Dictionary<string, List<CuePointEntity>> cuesByHash = new(StringComparer.Ordinal);
            Dictionary<string, AudioFeaturesEntity> beatDataByHash = new(StringComparer.Ordinal);

            await using var db = await _dbFactory.CreateDbContextAsync();

            if (hashes.Count > 0)
            {
                var cues = await db.CuePoints
                    .AsNoTracking()
                    .Where(c => hashes.Contains(c.TrackUniqueHash))
                    .OrderBy(c => c.TimestampInSeconds)
                    .ToListAsync();

                foreach (var cue in cues)
                {
                    if (!cuesByHash.TryGetValue(cue.TrackUniqueHash, out var bucket))
                    {
                        bucket = new List<CuePointEntity>();
                        cuesByHash[cue.TrackUniqueHash] = bucket;
                    }
                    bucket.Add(cue);
                }

                var beatData = await db.AudioFeatures
                    .AsNoTracking()
                    .Where(a => hashes.Contains(a.TrackUniqueHash))
                    .ToListAsync();

                foreach (var features in beatData)
                {
                    // First match wins if a hash somehow has more than one row (shouldn't happen).
                    beatDataByHash.TryAdd(features.TrackUniqueHash, features);
                }
            }

            // Build parallel lists so cue look-up stays correct even when some tracks
            // are skipped (file not found). Using a separate source list avoids the
            // index-mismatch bug that occurs when iterating rbTracks by index into trackList.
            var rbTracks = new List<RekordboxTrack>();
            var rbSources = new List<PlaylistTrack>();
            var usedTrackIds = new HashSet<int>();

            foreach (var track in trackList)
            {
                // Use dest path when a pathMap is provided (USB/folder export), otherwise use local path
                var effectivePath = pathMap != null && pathMap.TryGetValue(track.ResolvedFilePath ?? "", out var mapped)
                    ? mapped
                    : track.ResolvedFilePath;

                if (string.IsNullOrEmpty(effectivePath) || !File.Exists(effectivePath))
                    continue;

                var fileInfo = new FileInfo(effectivePath);
                var trackId = ResolveStableTrackId(track, effectivePath, usedTrackIds);

                var rbTrack = new RekordboxTrack
                {
                    TrackID = trackId,
                    Name = track.Title ?? "Unknown Title",
                    Artist = track.Artist ?? "Unknown Artist",
                    Album = track.Album ?? "Unknown Album",
                    Genre = track.Genres ?? "",
                    Kind = ResolveKind(effectivePath),
                    Size = fileInfo.Length,
                    TotalTime = Math.Max(0, track.CanonicalDuration.GetValueOrDefault() / 1000),
                    DateAdded = track.AddedAt.ToString("yyyy-MM-dd"),
                    BitRate = track.Bitrate ?? 0,
                    SampleRate = track.SpectralSampleRateHz.GetValueOrDefault() > 0 ? track.SpectralSampleRateHz!.Value : 44100,
                    AverageBpm = track.BPM ?? 0,
                    Tonality = track.MusicalKey ?? "",
                    Label = track.Label ?? "",
                    TrackNumber = Math.Max(0, track.TrackNumber),
                    Year = track.ReleaseDate?.Year ?? 0,
                    Location = BuildLocationUri(effectivePath),
                    Rating = Math.Clamp(track.Rating, 0, 5) * 51,
                    Comments = track.Comments ?? "",
                    Colour = track.ColorTag,
                };
                rbTracks.Add(rbTrack);
                rbSources.Add(track);
            }

            var folderChainLeafToRoot = await ResolveFolderChainNamesAsync(folderId, playlistName, db);
            var playlistsElement = BuildPlaylistsElement(playlistName, rbTracks, folderChainLeafToRoot);

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement("DJ_PLAYLISTS",
                    new XAttribute("Version", "1.0.0"),
                    new XElement("PRODUCT",
                        new XAttribute("Name", "rekordbox"),
                        new XAttribute("Version", "6.0.0"),
                        new XAttribute("Company", "Pioneer DJ")),
                    new XElement("COLLECTION",
                        new XAttribute("Entries", rbTracks.Count),
                        rbTracks.Select((t, idx) =>
                        {
                            var srcTrack = rbSources[idx];
                            var hash = srcTrack.TrackUniqueHash ?? string.Empty;
                            var cues = cuesByHash.TryGetValue(hash, out var list) ? list : new List<CuePointEntity>();
                            var beatData = beatDataByHash.TryGetValue(hash, out var features) ? features : null;
                            var bpm = t.AverageBpm;

                            return BuildTrackElement(t, cues, srcTrack, bpm, beatData);
                        })
                    ),
                    playlistsElement
                )
            );

            var finalDoc = doc;
            if (File.Exists(targetPath))
            {
                try
                {
                    var existingDoc = XDocument.Load(targetPath);
                    if (existingDoc.Root?.Name.LocalName == "DJ_PLAYLISTS" && existingDoc.Root.Element("COLLECTION") != null)
                    {
                        var playlistPathChain = new List<string> { "ROOT" };
                        playlistPathChain.AddRange(Enumerable.Reverse(folderChainLeafToRoot));
                        playlistPathChain.Add(playlistName);

                        finalDoc = RekordboxXmlMerger.MergeIntoExisting(existingDoc, doc, playlistPathChain, _logger);
                        _logger.LogInformation("Merged export into existing Rekordbox XML at {Path}", targetPath);
                    }
                    else
                    {
                        _logger.LogWarning("Existing file at {Path} doesn't look like a Rekordbox XML — overwriting with a fresh export.", targetPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse existing file at {Path} for merge — overwriting with a fresh export.", targetPath);
                }
            }

            await Task.Run(() => finalDoc.Save(targetPath));
            _logger.LogInformation("Rekordbox XML export completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist to Rekordbox XML");
            throw;
        }
    }

    /// <summary>
    /// Builds the &lt;PLAYLISTS&gt; element, nesting the playlist under real &lt;NODE Type="0"&gt;
    /// folder wrappers per <paramref name="chainLeafToRoot"/> (from <see cref="ResolveFolderChainNamesAsync"/>)
    /// — an empty chain places the playlist directly under ROOT.
    /// </summary>
    private XElement BuildPlaylistsElement(
        string playlistName, List<RekordboxTrack> rbTracks, List<string> chainLeafToRoot)
    {
        var playlistNode = new XElement("NODE",
            new XAttribute("Name", playlistName),
            new XAttribute("Type", "1"),
            new XAttribute("Entries", rbTracks.Count),
            rbTracks.Select(t => new XElement("TRACK", new XAttribute("Key", t.TrackID))));

        XElement node = playlistNode;
        for (int i = 0; i < chainLeafToRoot.Count; i++)
        {
            node = new XElement("NODE",
                new XAttribute("Type", "0"),
                new XAttribute("Name", chainLeafToRoot[i]),
                node);
        }

        return new XElement("PLAYLISTS",
            new XElement("NODE", new XAttribute("Type", "0"), new XAttribute("Name", "ROOT"), node));
    }

    /// <summary>
    /// Walks <paramref name="folderId"/> → ParentFolderId → ... → null, collecting folder names
    /// leaf-to-root. Returns an empty list (flat placement under ROOT) if <paramref name="folderId"/>
    /// is null, the folder table can't be read, a folder in the chain no longer exists, or a
    /// pathological parent cycle is detected — all degrade gracefully rather than throwing.
    /// Shared by the fresh-build path (<see cref="BuildPlaylistsElementAsync"/>) and the merge
    /// path, which needs the same root-to-leaf path to locate the matching node in an existing file.
    /// </summary>
    private async Task<List<string>> ResolveFolderChainNamesAsync(Guid? folderId, string playlistName, AppDbContext db)
    {
        var chainLeafToRoot = new List<string>();
        if (folderId is null) return chainLeafToRoot;

        List<PlaylistFolderEntity> allFolders;
        try
        {
            allFolders = await db.PlaylistFolders.AsNoTracking().ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rekordbox export: failed to load playlist folders for '{PlaylistName}' — falling back to flat placement under ROOT.", playlistName);
            return chainLeafToRoot;
        }

        var foldersById = allFolders.ToDictionary(f => f.Id);

        // Guard against a dangling/missing folder and against a pathological parent cycle —
        // neither should exist today, but this must degrade gracefully, not throw or hang.
        var visited = new HashSet<Guid>();
        var currentId = folderId;
        const int maxDepth = 64;

        while (currentId is Guid id && chainLeafToRoot.Count < maxDepth)
        {
            if (!visited.Add(id))
            {
                _logger.LogWarning("Rekordbox export: cyclic playlist folder chain detected starting at {FolderId} for '{PlaylistName}' — truncating.", folderId, playlistName);
                break;
            }

            if (!foldersById.TryGetValue(id, out var folder))
            {
                _logger.LogWarning("Rekordbox export: playlist folder {FolderId} referenced by '{PlaylistName}' no longer exists — placing flat under ROOT.", id, playlistName);
                chainLeafToRoot.Clear();
                break;
            }

            chainLeafToRoot.Add(folder.Name);
            currentId = folder.ParentFolderId;
        }

        return chainLeafToRoot;
    }

    /// <summary>
    /// Maps a file extension to Rekordbox's real Kind string (e.g. "MP3 File"), confirmed against
    /// actual rekordbox-exported XML rather than guessed — Rekordbox does not use a numeric code
    /// here despite some third-party tools assuming otherwise. Unrecognized extensions get a
    /// best-effort "{EXT} File" label rather than a silently wrong guess.
    /// </summary>
    private static string ResolveKind(string filePath)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        return ext switch
        {
            "MP3" => "MP3 File",
            "WAV" => "WAV File",
            "AIFF" or "AIF" => "AIFF File",
            "FLAC" => "FLAC File",
            "M4A" or "AAC" => "M4A File",
            "" => "Unknown File Type",
            _ => $"{ext} File",
        };
    }

    /// <summary>
    /// Resolves a deterministic Rekordbox TrackID for <paramref name="track"/> so the same track
    /// gets the same ID across separate export runs (enables re-importing an updated XML without
    /// Rekordbox treating every track as brand new). Falls back through TrackUniqueHash → resolved
    /// file path → track Guid, and guards against the astronomically unlikely case of a 31-bit hash
    /// collision within a single export by deterministically bumping to the next free ID.
    /// </summary>
    private int ResolveStableTrackId(PlaylistTrack track, string effectivePath, HashSet<int> usedTrackIds)
    {
        var seed = !string.IsNullOrEmpty(track.TrackUniqueHash)
            ? track.TrackUniqueHash
            : !string.IsNullOrEmpty(effectivePath)
                ? effectivePath
                : track.Id.ToString();

        var trackId = Utils.GuidGenerator.CreateStableIntFromSeed(seed);
        if (trackId == 0) trackId = 1; // 0 is reserved/falsy-looking; never emit it

        if (usedTrackIds.Contains(trackId))
        {
            var original = trackId;
            while (usedTrackIds.Contains(trackId))
                trackId++;
            _logger.LogWarning(
                "Rekordbox export: TrackID collision for '{Title}' (seed hash {Original}) — reassigned to {NewId}.",
                track.Title, original, trackId);
        }

        usedTrackIds.Add(trackId);
        return trackId;
    }

    // ──────────────────────────────────────────────────────────────────────
    // XML node builders
    // ──────────────────────────────────────────────────────────────────────

    private XElement BuildTrackElement(RekordboxTrack t, List<CuePointEntity> dbCues, PlaylistTrack? src, double bpm, AudioFeaturesEntity? beatData)
    {
        var trackElem = new XElement("TRACK",
            new XAttribute("TrackID", t.TrackID),
            new XAttribute("Name", t.Name),
            new XAttribute("Artist", t.Artist),
            new XAttribute("Album", t.Album),
            new XAttribute("Genre", t.Genre),
            new XAttribute("Kind", t.Kind),
            new XAttribute("Size", t.Size),
            new XAttribute("TotalTime", t.TotalTime),
            new XAttribute("DateAdded", t.DateAdded),
            new XAttribute("BitRate", t.BitRate),
            new XAttribute("SampleRate", t.SampleRate),
            new XAttribute("AverageBpm", t.AverageBpm.ToString("F2", CultureInfo.InvariantCulture)),
            new XAttribute("Tonality", t.Tonality),
            new XAttribute("Location", t.Location),
            new XAttribute("Rating", t.Rating),
            new XAttribute("Comments", t.Comments),
            new XAttribute("Label", t.Label)
        );

        // Track-level colour tag — omitted entirely when unset (no UI to set it yet; this is
        // forward-compatible plumbing only). Rekordbox's Colour attribute has no "#" prefix.
        if (!string.IsNullOrWhiteSpace(t.Colour))
        {
            trackElem.Add(new XAttribute("Colour", t.Colour.TrimStart('#')));
        }

        // TrackNumber/Year: omitted entirely when unknown (0) rather than writing a misleading "0".
        if (t.TrackNumber > 0)
            trackElem.Add(new XAttribute("TrackNumber", t.TrackNumber));
        if (t.Year > 0)
            trackElem.Add(new XAttribute("Year", t.Year));

        // TEMPO node(s) — derived from the track's real beat grid when available (multiple
        // anchors for a track with genuine tempo drift), otherwise a single anchor at the real
        // downbeat offset. Falls back to a single anchor at 0.000 when no beatgrid data exists.
        var beatTimestamps = ParseBeatGridJson(beatData?.BeatGridJson, t.Name);
        var downbeatOffset = beatData?.DownbeatOffsetSeconds ?? 0.0;
        var anchors = TempoGridDeriver.DeriveAnchors(beatTimestamps, downbeatOffset, bpm, beatData?.BpmStability);

        foreach (var anchor in anchors)
        {
            trackElem.Add(new XElement("TEMPO",
                new XAttribute("Inizio", anchor.InizioSeconds.ToString("F3", CultureInfo.InvariantCulture)),
                new XAttribute("Bpm", anchor.Bpm.ToString("F2", CultureInfo.InvariantCulture)),
                new XAttribute("Metro", "4/4"),
                new XAttribute("Battito", "1")
            ));
        }

        // Merge DB cue points + user cues from CuePointsJson
        var allCues = BuildCueList(dbCues, src?.CuePointsJson, t.Name);

        // Separate loops from point cues — loops get Type=4 with End attribute, point cues get Type=0
        var pointCues = allCues.Where(c => !c.IsLoop).ToList();
        var loopCues  = allCues.Where(c => c.IsLoop).ToList();

        // Reserve every pad explicitly claimed by a loop OR a point cue first, so auto-assignment
        // (below) can never clobber a pad another cue/loop deliberately set via its own SlotIndex.
        var reservedPads = new HashSet<int>(
            allCues.Where(c => c.SlotIndex is >= 0 and <= 7).Select(c => c.SlotIndex));

        // Assign Rekordbox pad numbers for point cues: use SlotIndex when set (0-7), else next
        // free pad, else -1 (memory cue only, once all 8 pads are taken).
        int nextFreePad = 0;
        int NextAvailablePad()
        {
            while (nextFreePad < 8 && reservedPads.Contains(nextFreePad))
                nextFreePad++;
            return nextFreePad < 8 ? nextFreePad++ : -1;
        }

        foreach (var cue in pointCues.Take(32))
        {
            var (r, g, b) = HexToRgb(cue.Color, t.Name);
            int num = cue.SlotIndex is >= 0 and <= 7 ? cue.SlotIndex : NextAvailablePad();

            trackElem.Add(new XElement("POSITION_MARK",
                new XAttribute("Name", cue.Name),
                new XAttribute("Type", "0"),
                new XAttribute("Start", cue.Timestamp.ToString("F3", CultureInfo.InvariantCulture)),
                new XAttribute("Num", num),
                new XAttribute("Red", r),
                new XAttribute("Green", g),
                new XAttribute("Blue", b)
            ));

            // A cue occupying a hot-cue pad also gets a memory-cue duplicate at the same
            // position, so it stays visible on Rekordbox's memory-cue strip even if the pad
            // is later overwritten on the hardware.
            if (num >= 0)
            {
                trackElem.Add(new XElement("POSITION_MARK",
                    new XAttribute("Name", cue.Name),
                    new XAttribute("Type", "0"),
                    new XAttribute("Start", cue.Timestamp.ToString("F3", CultureInfo.InvariantCulture)),
                    new XAttribute("Num", "-1"),
                    new XAttribute("Red", r),
                    new XAttribute("Green", g),
                    new XAttribute("Blue", b)
                ));
            }
        }

        // Loop cues: Type=4, Start + End attributes (Rekordbox format). Num honors an explicit
        // SlotIndex (0-7) as a hot loop; otherwise -1 (memory loop). No auto-assignment for
        // un-slotted loops — only an explicitly-set slot is honored.
        foreach (var loop in loopCues)
        {
            var (r, g, b) = HexToRgb(loop.Color, t.Name);
            int loopNum = loop.SlotIndex is >= 0 and <= 7 ? loop.SlotIndex : -1;
            trackElem.Add(new XElement("POSITION_MARK",
                new XAttribute("Name", loop.Name),
                new XAttribute("Type", "4"),
                new XAttribute("Start", loop.Timestamp.ToString("F3", CultureInfo.InvariantCulture)),
                new XAttribute("End", loop.LoopEndSeconds.ToString("F3", CultureInfo.InvariantCulture)),
                new XAttribute("Num", loopNum),
                new XAttribute("Red", r),
                new XAttribute("Green", g),
                new XAttribute("Blue", b)
            ));
        }

        return trackElem;
    }

    /// <summary>
    /// Parses a track's serialized beat-tick array (<see cref="AudioFeaturesEntity.BeatGridJson"/>),
    /// logging a warning and returning an empty grid (safe single-anchor fallback) if malformed.
    /// </summary>
    private List<double> ParseBeatGridJson(string? beatGridJson, string trackName)
    {
        if (string.IsNullOrWhiteSpace(beatGridJson))
            return new List<double>();

        try
        {
            return JsonSerializer.Deserialize<List<double>>(beatGridJson) ?? new List<double>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rekordbox export: malformed BeatGridJson for '{Track}' — falling back to a single TEMPO anchor.", trackName);
            return new List<double>();
        }
    }

    /// <summary>
    /// Merges DB-stored <see cref="CuePointEntity"/> rows with user-placed
    /// <see cref="OrbitCue"/> objects serialised in <paramref name="cuePointsJson"/>.
    /// DB cues come first (auto-generated structural cues); user cues follow.
    /// Deduplicates by timestamp within a 50 ms window.
    /// </summary>
    private List<OrbitCue> BuildCueList(List<CuePointEntity> dbCues, string? cuePointsJson, string trackName)
    {
        var result = new List<OrbitCue>();

        // 1. Auto-generated structural cues from DB
        foreach (var c in dbCues.OrderBy(c => c.TimestampInSeconds))
        {
            result.Add(new OrbitCue
            {
                Timestamp      = c.TimestampInSeconds,
                Name           = c.Label,
                Color          = c.Color,
                Source         = CueSource.Auto,
                IsLoop         = c.IsLoop,
                LoopEndSeconds = c.LoopEndSeconds,
                SlotIndex      = c.SlotIndex
            });
        }

        // 2. User-placed cues from JSON field
        if (!string.IsNullOrWhiteSpace(cuePointsJson))
        {
            try
            {
                var userCues = JsonSerializer.Deserialize<List<OrbitCue>>(cuePointsJson);
                if (userCues != null)
                {
                    foreach (var uc in userCues.OrderBy(u => u.Timestamp))
                    {
                        // Dedup: skip if a cue within 50 ms already exists
                        bool duplicate = result.Any(e => Math.Abs(e.Timestamp - uc.Timestamp) < 0.05);
                        if (!duplicate) result.Add(uc);
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Rekordbox export: malformed CuePointsJson for '{Track}' — user-placed cues for this track were dropped (auto-generated cues are unaffected).", trackName);
            }
        }

        return result.OrderBy(c => c.Timestamp).ToList();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Location URI helper
    // ──────────────────────────────────────────────────────────────────────

    // Matches a Windows drive-letter segment (e.g. "C:") so it can be preserved verbatim —
    // Uri.EscapeDataString would otherwise turn the colon into %3A and corrupt the path.
    private static readonly Regex DriveLetterSegmentRegex = new(@"^[A-Za-z]:$", RegexOptions.Compiled);

    /// <summary>
    /// Builds a Rekordbox-compatible file:// URI, percent-encoding each path segment
    /// independently (so literal "/" separators aren't escaped) while preserving a
    /// Windows drive letter's colon verbatim.
    /// </summary>
    private static string BuildLocationUri(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/');
        var escaped = segments.Select(seg =>
            DriveLetterSegmentRegex.IsMatch(seg) ? seg : Uri.EscapeDataString(seg));
        return "file://localhost/" + string.Join("/", escaped);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Color helper
    // ──────────────────────────────────────────────────────────────────────

    private (int R, int G, int B) HexToRgb(string hex, string trackName)
    {
        var original = hex;
        hex = hex.TrimStart('#');
        if (hex.Length != 6)
        {
            _logger.LogWarning("Rekordbox export: malformed cue colour '{Hex}' for '{Track}' — using white as a fallback.", original, trackName);
            return (255, 255, 255);
        }
        try
        {
            int r = Convert.ToInt32(hex[..2], 16);
            int g = Convert.ToInt32(hex[2..4], 16);
            int b = Convert.ToInt32(hex[4..6], 16);
            return (r, g, b);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            _logger.LogWarning(ex, "Rekordbox export: malformed cue colour '{Hex}' for '{Track}' — using white as a fallback.", original, trackName);
            return (255, 255, 255);
        }
    }


    public async Task ExportToM3uAsync(
        string playlistName,
        IEnumerable<PlaylistTrack> tracks,
        string targetPath)
    {
        try
        {
            _logger.LogInformation("Exporting playlist '{PlaylistName}' to M3U: {Path}", playlistName, targetPath);

            var trackList = tracks
                .Where(t => !string.IsNullOrEmpty(t.ResolvedFilePath) && File.Exists(t.ResolvedFilePath))
                .GroupBy(t => t.ResolvedFilePath!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var lines = new List<string> { "#EXTM3U", $"#PLAYLIST:{playlistName}" };

            foreach (var track in trackList)
            {
                int durationSec = Math.Max(0, track.CanonicalDuration.GetValueOrDefault() / 1000);
                string artist = track.Artist ?? "Unknown Artist";
                string title = track.Title ?? "Unknown Title";
                lines.Add($"#EXTINF:{durationSec},{artist} - {title}");
                lines.Add(track.ResolvedFilePath!);
            }

            await File.WriteAllLinesAsync(targetPath, lines, System.Text.Encoding.UTF8);
            _logger.LogInformation("M3U export completed: {Count} tracks.", trackList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist to M3U");
            throw;
        }
    }

}
