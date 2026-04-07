using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Integrations;

/// <summary>
/// A track record used as input to <see cref="AbletonLiveProjectWriter"/>.
/// </summary>
public sealed record AbletonTrack(
    string FilePath,
    string Title,
    string Artist,
    double DurationSeconds,
    float  Bpm,
    string Key   = "",
    string Scale = "");

/// <summary>
/// Writes an Ableton Live 11+ project file (.als) containing one AudioClip per track
/// arranged on separate Audio tracks in the Arrangement view.
/// The .als format is gzip-compressed XML.  Implements Issue 6.2 / #39.
/// </summary>
public sealed class AbletonLiveProjectWriter
{
    private readonly ILogger<AbletonLiveProjectWriter> _logger;

    public AbletonLiveProjectWriter(ILogger<AbletonLiveProjectWriter> logger)
    {
        _logger = logger;
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Exports <paramref name="tracks"/> to an Ableton Live .als project file
    /// at <paramref name="outputPath"/>.
    /// </summary>
    public void Export(IReadOnlyList<AbletonTrack> tracks, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path must not be empty.", nameof(outputPath));

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        XDocument doc  = BuildProjectXml(tracks);
        string    xml  = doc.ToString(SaveOptions.DisableFormatting);
        byte[]    data = Encoding.UTF8.GetBytes(xml);

        using var fs     = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var gz     = new GZipStream(fs, CompressionLevel.Optimal);
        gz.Write(data, 0, data.Length);

        _logger.LogInformation("Ableton .als exported: {Path} ({N} tracks)", outputPath, tracks.Count);
    }

    // ── XML builder (internal / testable) ────────────────────────────────

    /// <summary>
    /// Builds the Ableton XML DOM from a list of tracks.
    /// Public so unit tests can inspect the structure without disk I/O.
    /// </summary>
    public XDocument BuildProjectXml(IReadOnlyList<AbletonTrack> tracks)
    {
        var root = new XElement("Ableton",
            new XAttribute("MajorVersion", "11"),
            new XAttribute("MinorVersion", "11.3.2"),
            new XAttribute("SchemaChangeCount", "3"),
            new XAttribute("Creator", "OrbitPure"),
            new XAttribute("Revision", ""));

        var liveSet = new XElement("LiveSet");

        // Tracks container
        var tracksEl = new XElement("Tracks");
        double timelineOffset = 0.0;

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            tracksEl.Add(BuildAudioTrack(i, t, ref timelineOffset));
        }

        liveSet.Add(tracksEl);
        liveSet.Add(BuildMasterTrack());
        liveSet.Add(new XElement("Transport",
            new XElement("Tempo",
                new XAttribute("Value", tracks.Count > 0
                    ? tracks[0].Bpm.ToString("F2", CultureInfo.InvariantCulture)
                    : "120.00"))));

        root.Add(liveSet);
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

    // ── Track builders ───────────────────────────────────────────────────

    private static XElement BuildAudioTrack(int index, AbletonTrack track, ref double offset)
    {
        double durationBeats = track.Bpm > 0f
            ? track.DurationSeconds * track.Bpm / 60.0
            : track.DurationSeconds * 2.0; // fallback: 120 bpm

        var clip = new XElement("AudioClip",
            new XAttribute("Id", index),
            new XAttribute("Time", offset.ToString("F6", CultureInfo.InvariantCulture)),
            new XElement("Name",  new XAttribute("Value", SanitiseName(track.Title))),
            new XElement("Color", new XAttribute("Value", "-1")),
            new XElement("CurrentStart", new XAttribute("Value",
                offset.ToString("F6", CultureInfo.InvariantCulture))),
            new XElement("CurrentEnd", new XAttribute("Value",
                (offset + durationBeats).ToString("F6", CultureInfo.InvariantCulture))),
            new XElement("Loop",
                new XElement("LoopStart",  new XAttribute("Value", "0")),
                new XElement("LoopEnd",    new XAttribute("Value",
                    durationBeats.ToString("F6", CultureInfo.InvariantCulture))),
                new XElement("StartRelative", new XAttribute("Value", "0")),
                new XElement("LoopOn",     new XAttribute("Value", "false"))),
            new XElement("AudioRef",
                BuildFileReference(track.FilePath)),
            BuildWarpSection(track));

        double beatGridAnnotation = offset;
        offset += durationBeats;

        var arrangementClips = new XElement("ArrangerAutomation",
            new XElement("Events"),
            new XElement("AutomationTransformViewState",
                new XElement("IsTransformPending", new XAttribute("Value", "false"))));

        return new XElement("AudioTrack",
            new XAttribute("Id", index),
            new XElement("LomId", new XAttribute("Value", "0")),
            new XElement("IsContentSelectedInDocument", new XAttribute("Value", "false")),
            new XElement("PreferredContentViewMode", new XAttribute("Value", "0")),
            new XElement("TrackDelay",
                new XElement("Value", new XAttribute("Value", "0")),
                new XElement("IsValueSampleBased", new XAttribute("Value", "false"))),
            new XElement("Name",
                new XElement("EffectiveName", new XAttribute("Value",
                    $"{SanitiseName(track.Artist)} – {SanitiseName(track.Title)}"))),
            new XElement("ClipSlotList",
                new XElement("ClipSlot", new XAttribute("Id", "0"))),
            new XElement("DeviceChain",
                new XElement("AudioInputRouting",
                    new XElement("Target", new XAttribute("Value", "AudioIn/None"))),
                new XElement("AudioOutputRouting",
                    new XElement("Target", new XAttribute("Value", "AudioOut/Master")))),
            new XElement("FreezeSequencer",
                new XElement("Events")),
            new XElement("VelocityDetail", new XAttribute("Value", "0")),
            new XElement("NeedArrangerRefreeze", new XAttribute("Value", "true")),
            new XElement("PostProcessFreezeClips", new XAttribute("Value", "0")),
            arrangementClips,
            new XElement("MainSequencer",
                new XElement("BeatTime", new XAttribute("Value", "0")),
                new XElement("ClipTimeable",
                    new XElement("ArrangerAutomation",
                        new XElement("Events", clip)))));
    }

    private static XElement BuildFileReference(string filePath)
    {
        string absPath = Path.GetFullPath(filePath);
        return new XElement("FileRef",
            new XElement("HasRelativePath", new XAttribute("Value", "false")),
            new XElement("RelativePathType", new XAttribute("Value", "0")),
            new XElement("RelativePath"),
            new XElement("Name", new XAttribute("Value", Path.GetFileName(absPath))),
            new XElement("Dir",  new XAttribute("Value", Path.GetDirectoryName(absPath) ?? "")),
            new XElement("Path", new XAttribute("Value", absPath)),
            new XElement("Type", new XAttribute("Value", "1")));
    }

    private static XElement BuildWarpSection(AbletonTrack track)
    {
        // When BPM is known, add a single warp marker at beat 0 → time 0
        var markers = new XElement("WarpMarkers");
        if (track.Bpm > 0f)
        {
            markers.Add(new XElement("WarpMarker",
                new XAttribute("Id", "0"),
                new XAttribute("SecTime",  "0"),
                new XAttribute("BeatTime", "0")));
        }

        return new XElement("WarpMode", new XAttribute("Value", "0"),
            new XElement("WarpIsActive", new XAttribute("Value", track.Bpm > 0f ? "true" : "false")),
            new XElement("Tempo",
                new XAttribute("Value",
                    track.Bpm > 0f
                        ? track.Bpm.ToString("F2", CultureInfo.InvariantCulture)
                        : "120.00")),
            markers);
    }

    private static XElement BuildMasterTrack()
    {
        return new XElement("MasterTrack",
            new XElement("LomId", new XAttribute("Value", "0")),
            new XElement("Name",
                new XElement("EffectiveName", new XAttribute("Value", "Master"))),
            new XElement("DeviceChain",
                new XElement("AudioOutputRouting",
                    new XElement("Target", new XAttribute("Value", "AudioOut/None")))));
    }

    private static string SanitiseName(string name)
    {
        // Strip XML-unsafe characters; keep under 128 chars
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c < 0x20 && c != '\t') continue; // control chars
            sb.Append(c);
        }
        string result = sb.ToString().Trim();
        return result.Length > 128 ? result[..128] : result;
    }
}
