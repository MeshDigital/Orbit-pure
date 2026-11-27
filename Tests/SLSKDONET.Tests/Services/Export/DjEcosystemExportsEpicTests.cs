using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Export;
using SLSKDONET.Services.Integrations;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>
/// Regression tests for Epic #551 (DJ ecosystem exports):
/// - DX0 (#555): KeyNotation 24-key round-trip + ExportFieldMapping + ExportTrackProjection
/// - DX1 (#556): Traktor NML standalone playlist export + round-trip via TraktorMetadataImporter
/// - DX2 (#557): VirtualDJ .vdjfolder + guarded database.xml merge (60/BPM, backup, running check, unknown element preservation)
/// - DX3 (#558): Serato binary .crate writer (UTF-16BE, relative path, %% nesting, multi-drive)
/// - DX4 (#559): Spike deliverable exists
/// </summary>
public sealed class DjEcosystemExportsEpicTests
{
    // ── DX0 (#555): KeyNotation 24-key round-trip & field mapping ────────────

    [Fact]
    public void KeyNotation_RoundTripsAll24Keys_AcrossCamelotOpenKeyMusicalAndTraktorInt()
    {
        Assert.Equal(24, KeyNotation.AllKeys.Count);

        for (int i = 0; i < 24; i++)
        {
            Assert.True(KeyNotation.TryFromTraktorInt(i, out var info));
            Assert.Equal(i, info.TraktorInt);

            Assert.Equal(i, KeyNotation.ToTraktorInt(info.Camelot));
            Assert.Equal(i, KeyNotation.ToTraktorInt(info.OpenKey));
            Assert.Equal(i, KeyNotation.ToTraktorInt(info.Musical));

            Assert.Equal(info.Camelot, KeyNotation.ToCamelot(info.OpenKey));
            Assert.Equal(info.OpenKey, KeyNotation.ToOpenKey(info.Musical));
            Assert.Equal(info.Musical, KeyNotation.ToMusical(info.Camelot));
        }

        // Enharmonic aliases
        Assert.Equal("2B", KeyNotation.ToCamelot("Gb"));
        Assert.Equal("6B", KeyNotation.ToCamelot("Bb"));
        Assert.Equal("8A", KeyNotation.ToCamelot("08A"));
        Assert.Null(KeyNotation.ToTraktorInt("not-a-key"));
    }

    [Fact]
    public void ExportFieldMapping_FormatsEnergyPrefixAndPreflightWarnings()
    {
        var cues = Enumerable.Range(0, 10)
            .Select(i => new ExportCueProjection(i, $"Cue {i}", i * 10.0, null, false, "#27AE60", true))
            .ToList();

        var track = new ExportTrackProjection(
            "h1", @"C:\Music\DnB\track.flac", "Even If", "Calibre", "Shelflife",
            "Drum and Bass", "Signature", "Warm opener", 174.0, 0.25, "8A", 0.72, 5, 310, 1000, cues);

        var comment = ExportFieldMapping.FormatComment(track, ExportTargetKind.Traktor);
        Assert.StartsWith("E7", comment);
        Assert.Contains("Warm opener", comment);

        var preflight = ExportFieldMapping.Preflight(new[] { track }, ExportTargetKind.Traktor);
        Assert.Single(preflight.Warnings);
        Assert.Contains("8 hot cues", preflight.Warnings[0]);
    }

    // ── DX1 (#556): Traktor NML playlist export + round-trip ─────────────────

    [Fact]
    public void TraktorNmlExporter_WritesStandaloneNml_ThatRoundTripsThroughTraktorImporter()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"orbit-dx1-{Guid.NewGuid():N}.nml");
        try
        {
            var track = new ExportTrackProjection(
                "h1", @"C:\Music\DnB\EvenIf.flac", "Even If", "Calibre", "Shelflife",
                "Drum and Bass", "Signature", "", 174.0, 0.5, "8A", 0.7, 5, 300, 1000,
                new[]
                {
                    new ExportCueProjection(0, "Drop", 44.0, null, false, "#E2272E", true),
                    new ExportCueProjection(1, "Outro Loop", 240.0, 251.034, true, "#27AE60", true),
                });

            TraktorNmlExporter.WriteToFile(tempFile, "Liquid Warmup", new[] { track });

            var doc = XDocument.Load(tempFile);
            var entry = doc.Root!.Element("COLLECTION")!.Element("ENTRY")!;
            Assert.Equal("0", entry.Element("MUSICAL_KEY")!.Attribute("VALUE")!.Value); // 8A == Am == Traktor 0
            Assert.Equal("174.000000", entry.Element("TEMPO")!.Attribute("BPM")!.Value);
            Assert.Contains(entry.Elements("CUE_V2"), c => c.Attribute("TYPE")?.Value == "4"); // beatgrid
            Assert.Contains(entry.Elements("CUE_V2"), c => c.Attribute("TYPE")?.Value == "0" && c.Attribute("HOTCUE")?.Value == "0");
            Assert.Contains(entry.Elements("CUE_V2"), c => c.Attribute("TYPE")?.Value == "5" && c.Attribute("HOTCUE")?.Value == "1");

            // Round-trip through TraktorMetadataImporter
            var importer = new TraktorMetadataImporter(NullLogger<TraktorMetadataImporter>.Instance);
            var imported = importer.ImportLibrary(tempFile);
            Assert.Single(imported);
            Assert.Equal("Even If", imported[0].Title);
            Assert.Equal("Calibre", imported[0].Artist);
            Assert.Equal(174.0, imported[0].Bpm, precision: 2);
            Assert.Equal("1m", imported[0].Key); // Open Key for Am / 8A
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── DX2 (#557): VirtualDJ .vdjfolder + guarded database.xml merge ────────

    [Fact]
    public void VirtualDjExporter_RefusesWhenRunning_CreatesBackup_PreservesUnknownXml_AndWrites60OverBpm()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"orbit-dx2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, "database.xml");
            var existingPath = @"C:\Music\track1.flac";
            var newPath = @"C:\Music\track2.flac";

            var fixtureXml = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <VirtualDJ_Database Version="8.5">
                  <Song FilePath="{existingPath}" FileSize="123456" CustomAttr="keep-me">
                    <Tags Author="Calibre" Title="Even If" />
                    <CustomVendorElement Foo="bar" />
                    <Poi Pos="1.0" Type="automix" />
                    <Poi Pos="5.0" Type="cue" Num="1" Name="OldCue" />
                  </Song>
                </VirtualDJ_Database>
                """;
            File.WriteAllText(dbPath, fixtureXml);

            var tracks = new[]
            {
                new ExportTrackProjection("h1", existingPath, "Even If", "Calibre", "", "", "", "", 120.0, 0.25, "8A", null, 0, 300, 1000,
                    new[] { new ExportCueProjection(0, "Drop", 32.5, null, false, "#E2272E", true) }),
                new ExportTrackProjection("h2", newPath, "Glue", "Bicep", "", "", "", "", 130.0, null, "11A", null, 0, 260, 1000,
                    Array.Empty<ExportCueProjection>()),
            };

            // 1. Refuses while VirtualDJ is running
            var blocked = VirtualDjExporter.MergeDatabaseXml(dbPath, tracks, isRunningCheck: () => true);
            Assert.False(blocked.Success);
            Assert.Contains("running", blocked.Error, StringComparison.OrdinalIgnoreCase);

            // 2. Merges when not running, creates backup, preserves unknown elements/attrs, writes 60/BPM
            var merged = VirtualDjExporter.MergeDatabaseXml(dbPath, tracks, isRunningCheck: () => false);
            Assert.True(merged.Success);
            Assert.Equal(1, merged.UpdatedSongs);
            Assert.Equal(1, merged.AddedSongs);
            Assert.NotNull(merged.BackupPath);
            Assert.True(File.Exists(merged.BackupPath));

            var doc = XDocument.Load(dbPath);
            var song1 = doc.Root!.Elements("Song").Single(s => s.Attribute("FilePath")?.Value == existingPath);
            Assert.Equal("keep-me", song1.Attribute("CustomAttr")?.Value);
            Assert.NotNull(song1.Element("CustomVendorElement"));
            Assert.Contains(song1.Elements("Poi"), p => p.Attribute("Type")?.Value == "automix"); // non-cue Poi preserved

            // 60 / 120 BPM == 0.500000 seconds per beat
            var spb = double.Parse(song1.Element("Scan")!.Attribute("Bpm")!.Value, CultureInfo.InvariantCulture);
            Assert.Equal(0.5, spb, precision: 5);
            Assert.Equal("8A", song1.Element("Scan")!.Attribute("Key")!.Value);

            var newCue = song1.Elements("Poi").Single(p => p.Attribute("Type")?.Value == "cue");
            Assert.Equal("Drop", newCue.Attribute("Name")?.Value);
            Assert.Equal("32.500000", newCue.Attribute("Pos")?.Value);

            // .vdjfolder output
            var folderPath = Path.Combine(tempDir, "Set.vdjfolder");
            VirtualDjExporter.WriteVdjFolder(folderPath, tracks);
            var folderDoc = XDocument.Load(folderPath);
            Assert.Equal(2, folderDoc.Root!.Elements("song").Count());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── DX3 (#558): Serato binary .crate writer ──────────────────────────────

    [Fact]
    public void SeratoCrateExporter_WritesValidBinaryCrate_WithUtf16BeRelativePathsNestingAndMultiDrive()
    {
        Assert.Equal("Liquid%%Warmup.crate", SeratoCrateExporter.FormatCrateFileName(new[] { "Liquid", "Warmup" }));

        var (_, rel) = SeratoCrateExporter.SplitDriveAndRelativePath(@"E:\Music\DnB\Calibre - Even If.flac");
        Assert.Equal("Music/DnB/Calibre - Even If.flac", rel);

        var bytes = SeratoCrateExporter.BuildCrateBytes(new[] { rel, "Music/House/Bicep - Glue.flac" });
        // Header starts with ASCII "vrsn"
        Assert.Equal("vrsn", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        var parsed = SeratoCrateExporter.ReadCrateTrackPaths(bytes);
        Assert.Equal(new[] { rel, "Music/House/Bicep - Glue.flac" }, parsed.ToArray());

        // Multi-drive selection produces one crate per drive + refuses when running
        var tempDir = Path.Combine(Path.GetTempPath(), $"orbit-dx3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var blocked = SeratoCrateExporter.WriteCratesForSelection(
                new[] { "Festival" },
                new[] { @"C:\Music\a.flac" },
                isRunningCheck: () => true);
            Assert.False(blocked.Success);

            var result = SeratoCrateExporter.WriteCratesForSelection(
                new[] { "Festival", "DnB" },
                new[] { @"C:\Music\a.flac", @"D:\Audio\b.flac" },
                subcratesDirResolver: drive => Path.Combine(tempDir, drive.TrimEnd('\\', ':', '/')),
                isRunningCheck: () => false);

            Assert.True(result.Success);
            Assert.Equal(2, result.WrittenCratePaths.Count);
            Assert.Equal(2, result.TracksWritten);
            Assert.All(result.WrittenCratePaths, p => Assert.EndsWith("Festival%%DnB.crate", p));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
