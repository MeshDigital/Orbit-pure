using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Integrations;
using SLSKDONET.Services.Integrations.Serato;
using Xunit;

namespace SLSKDONET.Tests.Services.Integrations;

public class SeratoMarkers2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orbit-serato-" + Guid.NewGuid().ToString("N"));
    public SeratoMarkers2Tests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Cue_MatchesTheDocumentedByteLayout()
    {
        // serato-tags docs: CUE entry for slot 0 at 0 ms, red, no name
        //   "CUE\0" 00 00 00 0D | 00 00 00000000 00 CC0000 0000 00
        var entry = SeratoMarkers2.WriteCue(new SeratoCue(0, 0, 0xCC, 0x00, 0x00, ""));
        var payload = SeratoMarkers2.BuildPayload(new[] { entry });

        var expected = new byte[] { 1, 1 }
            .Concat(Encoding.ASCII.GetBytes("CUE")).Concat(new byte[] { 0, 0, 0, 0, 13 })
            .Concat(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0xCC, 0, 0, 0, 0, 0 })
            .Concat(new byte[] { 0 }) // terminator
            .ToArray();
        Assert.Equal(expected, payload);
    }

    [Fact]
    public void Id3Body_RoundTrips_AndLooksLikeSerato()
    {
        var entries = new List<SeratoEntry>
        {
            new("COLOR", new byte[] { 0, 0xFF, 0x99, 0xFF }),
            new("BPMLOCK", new byte[] { 0 }),
            SeratoMarkers2.WriteCue(new SeratoCue(2, 123_456, 0x00, 0xCC, 0x00, "Drop 1 ✓")),
            SeratoMarkers2.WriteLoop(new SeratoLoop(1, 10_000, 20_000, 0x27, 0xAA, 0xE1, true, "Intro loop")),
        };

        var body = SeratoMarkers2.BuildId3Body(entries);

        Assert.Equal(new byte[] { 1, 1 }, body[..2]);
        Assert.True(body.Length >= 470);
        var text = Encoding.ASCII.GetString(body, 2, body.Length - 2).TrimEnd('\0');
        Assert.DoesNotContain("=", text);
        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= 72));

        var parsed = SeratoMarkers2.ParseId3Body(body);
        Assert.Equal(entries.Select(e => e.Type), parsed.Select(e => e.Type));
        Assert.Equal(entries.Select(e => e.Data), parsed.Select(e => e.Data));
        Assert.Equal(new SeratoCue(2, 123_456, 0x00, 0xCC, 0x00, "Drop 1 ✓"), SeratoMarkers2.ReadCue(parsed[2]));
        Assert.Equal(new SeratoLoop(1, 10_000, 20_000, 0x27, 0xAA, 0xE1, true, "Intro loop"), SeratoMarkers2.ReadLoop(parsed[3]));
    }

    [Fact]
    public void VorbisValue_RoundTrips()
    {
        var entries = new List<SeratoEntry> { SeratoMarkers2.WriteCue(new SeratoCue(0, 5_000, 1, 2, 3, "A")) };
        var value = SeratoMarkers2.BuildVorbisValue(entries);

        var envelope = Convert.FromBase64String(value.Replace("\n", ""));
        Assert.StartsWith("application/octet-stream\0\0Serato Markers2\0", Encoding.ASCII.GetString(envelope));
        Assert.Equal(entries[0].Data, SeratoMarkers2.ParseVorbisValue(value).Single().Data);
    }

    [Fact]
    public void Merge_KeepsSeratosCues_AndFillsFreeSlots()
    {
        var existing = new List<SeratoEntry>
        {
            new("COLOR", new byte[] { 0, 0xFF, 0, 0 }),
            SeratoMarkers2.WriteCue(new SeratoCue(0, 1_000, 0xCC, 0, 0, "mine")),
            SeratoMarkers2.WriteCue(new SeratoCue(1, 60_000, 0xCC, 0, 0, "mine too")),
        };
        var orbit = new List<CuePointEntity>
        {
            new() { TimestampInSeconds = 60.02, Label = "dup", SlotIndex = 3 },          // same spot as Serato's slot 1
            new() { TimestampInSeconds = 30, Label = "Drop 1", SlotIndex = 0, Color = "#FF0000" }, // slot 0 is taken
            new() { TimestampInSeconds = 90, Label = "Outro" },                          // memory cue
            new() { TimestampInSeconds = 45, LoopEndSeconds = 50, IsLoop = true, Label = "Loop" },
        };

        var (entries, cues, loops, skipped) = SeratoCueExportService.Merge(existing, orbit, SeratoWriteMode.KeepExisting);

        Assert.Equal((2, 1, 1), (cues, loops, skipped));
        var written = entries.Select(SeratoMarkers2.ReadCue).Where(c => c != null).ToDictionary(c => c!.Index, c => c!);
        Assert.Equal("mine", written[0].Name);
        Assert.Equal("mine too", written[1].Name);
        Assert.Equal(("Drop 1", 30_000u, (byte)0xFF), (written[2].Name, written[2].PositionMs, written[2].R)); // moved to the first free slot
        Assert.Equal("Outro", written[3].Name);
        Assert.Equal(new SeratoLoop(0, 45_000, 50_000, 0x27, 0xAA, 0xE1, false, "Loop"), entries.Select(SeratoMarkers2.ReadLoop).Single(l => l != null));
        Assert.Equal("COLOR", entries[0].Type);
    }

    [Fact]
    public void Merge_Replace_DropsOldCuesButKeepsTrackColour()
    {
        var existing = new List<SeratoEntry>
        {
            SeratoMarkers2.WriteCue(new SeratoCue(0, 1_000, 0xCC, 0, 0, "old")),
            new("COLOR", new byte[] { 0, 0xFF, 0, 0 }),
            new("FLIP", new byte[] { 0, 1, 2 }),
        };
        var orbit = new List<CuePointEntity> { new() { TimestampInSeconds = 1.0, Label = "new", SlotIndex = 0 } };

        var (entries, cues, _, _) = SeratoCueExportService.Merge(existing, orbit, SeratoWriteMode.Replace);

        Assert.Equal(1, cues);
        Assert.Equal(new[] { "COLOR", "FLIP", "CUE" }, entries.Select(e => e.Type));
        Assert.Equal("new", SeratoMarkers2.ReadCue(entries[2])!.Name);
    }

    [Theory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("flac", "flac")]
    public void WriteTags_OnARealFile_IsReadBackByTheImporter(string ext, string codec)
    {
        var path = Path.Combine(_dir, "t." + ext);
        if (!TryFfmpeg($"-v error -y -f lavfi -i sine=frequency=440:duration=2 -c:a {codec} \"{path}\"")) return; // no ffmpeg
        var cues = new List<CuePointEntity>
        {
            new() { TimestampInSeconds = 0.5, Label = "Intro", SlotIndex = 0, Color = "#00FF00" },
            new() { TimestampInSeconds = 1.25, Label = "Drop 1", SlotIndex = 1, Color = "#FF0000" },
        };

        var first = SeratoCueExportService.WriteTags(path, path, cues, SeratoWriteMode.KeepExisting);
        var second = SeratoCueExportService.WriteTags(path, path, cues, SeratoWriteMode.KeepExisting); // re-export: no duplicates

        Assert.Equal((true, 2), (first.Success, first.CuesWritten));
        Assert.Equal((0, 2), (second.CuesWritten, second.Skipped));
        var imported = new SeratoMetadataImporter(NullLogger<SeratoMetadataImporter>.Instance).Import(path);
        Assert.NotNull(imported);
        Assert.Equal(new[] { ("Intro", 0.5, "#00FF00"), ("Drop 1", 1.25, "#FF0000") },
            imported!.Cues.Select(c => (c.Name, c.TimestampSeconds, c.Color)));
        using var check = TagLib.File.Create(path);
        Assert.True(check.Properties.Duration.TotalSeconds > 1.9); // audio intact
    }

    private static bool TryFfmpeg(string args)
    {
        try
        {
            var ffmpeg = SLSKDONET.Services.AudioAnalysis.AudioIngestionPipeline.ResolveFfmpegPath();
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
            if (p == null) return false;
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
