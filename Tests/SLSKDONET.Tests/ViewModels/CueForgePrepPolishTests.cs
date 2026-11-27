using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Integrations;
using SLSKDONET.Services.Integrations.Serato;
using SLSKDONET.Services.Rekordbox;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Covers CF2 follow-ups #616 (hot loops end-to-end) and #618 (Serato LOOP import parity).
/// </summary>
public sealed class CueForgePrepPolishTests
{
    [Fact]
    public void HotLoop_CanBeAssignedToPad_AndDemotesPreviousHolder_WithUndo()
    {
        var session = new CueEditorSession(new Mock<ICuePointService>().Object, new Mock<ICueAudition>().Object);
        session.Load(
            new CueTrackInfo("hash1", "Test Track", null, Bpm: 128, Downbeat: 0, Duration: 240),
            new[]
            {
                new CuePointEntity { TrackUniqueHash = "hash1", TimestampInSeconds = 16, Label = "Intro", SlotIndex = 0, IsLoop = false },
                new CuePointEntity { TrackUniqueHash = "hash1", TimestampInSeconds = 32, Label = "4 bars", SlotIndex = -1, IsLoop = true, LoopEndSeconds = 39.5 }
            });

        var loop = session.Cues.Single(c => c.IsLoop);
        var intro = session.Cues.Single(c => !c.IsLoop);
        session.SelectedCue = loop;

        session.SetSelectedCueSlotCommand.Execute(0).Subscribe();

        Assert.Equal(0, loop.SlotIndex);
        Assert.Equal(-1, intro.SlotIndex);
        Assert.Same(loop, session.HotCuePads[0].Cue);

        session.UndoCommand.Execute().Subscribe();

        var restoredLoop = session.Cues.Single(c => c.IsLoop);
        var restoredIntro = session.Cues.Single(c => !c.IsLoop);
        Assert.Equal(-1, restoredLoop.SlotIndex);
        Assert.Equal(0, restoredIntro.SlotIndex);
        Assert.Same(restoredIntro, session.HotCuePads[0].Cue);
    }

    [Fact]
    public void RekordboxCueImport_PreservesHotLoopSlot()
    {
        var track = new RekordboxDbTrack(
            Id: "1",
            Path: "test.flac",
            Title: "Track",
            Artist: "Artist",
            Bpm: 128,
            Cues: new List<RekordboxDbCue>
            {
                new(Kind: 1, InMsec: 32000, OutMsec: 39500, ColorTableIndex: 1, Comment: "Drop Loop"),
                new(Kind: 0, InMsec: 64000, OutMsec: 71500, ColorTableIndex: 0, Comment: "Mem Loop"),
            });

        var cues = RekordboxCueImport.ToOrbitCues(track);

        Assert.Equal(2, cues.Count);
        Assert.True(cues[0].IsLoop);
        Assert.Equal(0, cues[0].SlotIndex); // Kind 1 -> Pad A (slot 0)
        Assert.True(cues[1].IsLoop);
        Assert.Equal(-1, cues[1].SlotIndex); // Kind 0 -> Memory loop
    }

    [Fact]
    public void SeratoMetadataImporter_ReadsExportedCuesAndLoops()
    {
        var wavPath = Path.Combine(Path.GetTempPath(), $"orbit-serato-loop-{Guid.NewGuid():N}.wav");
        try
        {
            // Minimal valid PCM WAV file (100 ms silence) that TagLib# can tag with ID3v2 GEOB.
            WriteSilentWav(wavPath);

            var orbitCues = new List<CuePointEntity>
            {
                new() { TrackUniqueHash = "h", TimestampInSeconds = 12.5, Label = "Drop 1", Color = "#FF0000", SlotIndex = 0, IsLoop = false },
                new() { TrackUniqueHash = "h", TimestampInSeconds = 30.0, LoopEndSeconds = 38.0, Label = "Outro Loop", Color = "#00AAFF", SlotIndex = 1, IsLoop = true }
            };

            var writeRes = SeratoCueExportService.WriteTags(wavPath, wavPath, orbitCues, SeratoWriteMode.Replace);
            Assert.True(writeRes.Success);
            Assert.Equal(1, writeRes.CuesWritten);
            Assert.Equal(1, writeRes.LoopsWritten);

            var importer = new SeratoMetadataImporter(NullLogger<SeratoMetadataImporter>.Instance);
            var imported = importer.Import(wavPath);

            Assert.NotNull(imported);
            Assert.Equal(2, imported!.Cues.Count);

            var cue = imported.Cues.Single(c => !c.IsLoop);
            Assert.Equal("Drop 1", cue.Name);
            Assert.Equal(12.5, cue.TimestampSeconds, precision: 2);

            var loop = imported.Cues.Single(c => c.IsLoop);
            Assert.Equal("Outro Loop", loop.Name);
            Assert.Equal(30.0, loop.TimestampSeconds, precision: 2);
            Assert.Equal(38.0, loop.LoopEndSeconds, precision: 2);
        }
        finally
        {
            if (File.Exists(wavPath)) File.Delete(wavPath);
        }
    }

    private static void WriteSilentWav(string path)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataSize = 4410 * 2 * 2;
        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)2);
        bw.Write(44100);
        bw.Write(44100 * 4);
        bw.Write((short)4);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataSize);
        bw.Write(new byte[dataSize]);
    }
}
