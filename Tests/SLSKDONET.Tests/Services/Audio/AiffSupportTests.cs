using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Tests.Helpers;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>
/// AIFF end to end, on generated files in a temp folder and the isolated test DB: the owner's
/// real-library import listed every .aif/.aiff as "can't be imported yet" because the importer
/// had its own extension list without AIFF. Covers the format table, import (ID3 tags incl. BPM
/// and key, sample rate, bit depth), re-scan dedupe, AIFF-C (TagLib# rejects it), playback and
/// exact seeking, the ffmpeg analysis decode and the Rekordbox Kind.
/// </summary>
[Collection(PlayableAudioCacheCollection.Name)]
public class AiffSupportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "orbit-aiff-tests", Guid.NewGuid().ToString("N"));
    private readonly EventBusService _bus = new();
    private readonly LibraryFolderScannerService _scanner;

    public AiffSupportTests()
    {
        Directory.CreateDirectory(_folder);
        PlayableAudio.CacheDirectory = Path.Combine(_folder, "cache");
        _scanner = new LibraryFolderScannerService(NullLogger<LibraryFolderScannerService>.Instance, null!, null!, _bus);
    }

    public void Dispose()
    {
        try
        {
            using var context = new AppDbContext();
            var prefix = _folder + Path.DirectorySeparatorChar;
            context.LibraryEntries.RemoveRange(context.LibraryEntries.Where(e => e.FilePath.StartsWith(prefix)).ToList());
            context.LibraryFolders.RemoveRange(context.LibraryFolders.ToList()
                .Where(f => string.Equals(f.FolderPath.TrimEnd(Path.DirectorySeparatorChar), _folder, StringComparison.OrdinalIgnoreCase)));
            context.SaveChanges();
        }
        finally
        {
            _bus.Dispose();
            try { Directory.Delete(_folder, recursive: true); }
            catch (IOException) { /* intentional: a reader may still be closing; temp folder */ }
        }
    }

    // ── Format table ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("track.aif")]
    [InlineData("track.aiff")]
    [InlineData("TRACK.AIFF")]
    [InlineData("track.aifc")]
    [InlineData("track.opus")]
    [InlineData("track.wma")]
    public void Aiff_IsImportable(string file)
    {
        Assert.True(AudioFormats.IsImportable(file));
        Assert.Equal(LibraryAudioFormats.Kind.Supported, LibraryAudioFormats.Classify(file));
    }

    [Theory]
    [InlineData("aiff", true)]
    [InlineData("AIF", true)]
    [InlineData(".aifc", true)]
    [InlineData("wav", true)]
    [InlineData("flac", true)]
    [InlineData("mp3", false)]
    [InlineData("m4a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Lossless_IncludesAiff(string? token, bool lossless) =>
        Assert.Equal(lossless, AudioFormats.IsLossless(token));

    [Theory]
    [InlineData(@"D:\Music\a.aiff", "AIFF File")]
    [InlineData(@"D:\Music\a.aif", "AIFF File")]
    [InlineData(@"D:\Music\a.AIFC", "AIFF File")]
    [InlineData(@"D:\Music\a.wav", "WAV File")]
    [InlineData(@"D:\Music\a.mp3", "MP3 File")]
    [InlineData(@"D:\Music\a.flac", "FLAC File")]
    [InlineData(@"D:\Music\a.m4a", "M4A File")]
    [InlineData(@"D:\Music\a.xyz", "XYZ File")]
    [InlineData(@"D:\Music\noext", "Unknown File Type")]
    public void RekordboxKind_ComesFromTheTable(string path, string kind) =>
        Assert.Equal(kind, AudioFormats.RekordboxKind(path));

    [Fact]
    public void Ape_IsRecognisedButNotImportable()
    {
        Assert.Equal(LibraryAudioFormats.Kind.Unsupported, LibraryAudioFormats.Classify("x.ape"));
        Assert.Equal(LibraryAudioFormats.Kind.NotAudio, LibraryAudioFormats.Classify("cover.jpg"));
        Assert.Equal(LibraryAudioFormats.Kind.NotAudio, LibraryAudioFormats.Classify("clip.mp4")); // usually video locally
    }

    [Fact]
    public void QualityTier_AiffIsLossless() =>
        Assert.True(new SLSKDONET.ViewModels.PlaylistTrackViewModel(new PlaylistTrack { Format = "aif", Bitrate = 1411 }).IsTierLossless);

    // ── Import ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryReadEntry_16BitAiff_ReadsId3TagsAndStreamFacts()
    {
        var path = AiffTestFiles.WriteAiff(Path.Combine(_folder, "a.aiff"), 16, 44100, 2.0, new("Aiff Title", "Aiff Artist", 128, "8A"));

        var entry = _scanner.TryReadEntry(path, out var error);

        Assert.Null(error);
        Assert.NotNull(entry);
        Assert.Equal("Aiff Title", entry!.Title);
        Assert.Equal("Aiff Artist", entry.Artist);
        Assert.Equal(128, entry.TagBPM);
        Assert.Equal("8A", entry.MusicalKey);
        Assert.Equal("aiff", entry.Format);
        Assert.Equal(2, entry.DurationSeconds);
        Assert.Contains("samplerate=44100", entry.QualityDetails);
        Assert.Contains("bitdepth=16", entry.QualityDetails);
    }

    [Fact]
    public void TryReadEntry_24BitAif_ReportsSampleRateAndBitDepth()
    {
        var path = AiffTestFiles.WriteAiff(Path.Combine(_folder, "b.aif"), 24, 48000, 1.0, new("Hi Res", "Artist"));

        var entry = _scanner.TryReadEntry(path, out var error);

        Assert.Null(error);
        Assert.Equal("aif", entry!.Format);
        Assert.Contains("samplerate=48000", entry.QualityDetails);
        Assert.Contains("bitdepth=24", entry.QualityDetails);
    }

    [Theory]
    [InlineData("c.aifc")]
    [InlineData("logic-export.aif")] // AIFF-C content behind an .aif name, as Logic writes it
    public void TryReadEntry_AifcSowt_ReadsTheHeaderAndId3Chunk(string name)
    {
        var path = AiffTestFiles.WriteAifcSowt(Path.Combine(_folder, name), 44100, 2.0, new("Logic Bounce", "Producer", 174, "4A"));

        var entry = _scanner.TryReadEntry(path, out var error);

        Assert.Null(error);
        Assert.Equal("Logic Bounce", entry!.Title);
        Assert.Equal("Producer", entry.Artist);
        Assert.Equal(174, entry.TagBPM);
        Assert.Equal("4A", entry.MusicalKey);
        Assert.Equal(2, entry.DurationSeconds);
        Assert.Contains("samplerate=44100", entry.QualityDetails);
    }

    [Fact]
    public async Task ImportFolder_ImportsAiffFiles_AndReScanDedupes()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        AiffTestFiles.WriteAiff(Path.Combine(_folder, "one.aiff"), 16, 44100, 1.0, new($"One {unique}", "Artist"));
        AiffTestFiles.WriteAiff(Path.Combine(_folder, "sub", "two.aif"), 24, 48000, 1.0, new($"Two {unique}", "Artist"));
        AiffTestFiles.WriteAifcSowt(Path.Combine(_folder, "three.aifc"), 44100, 1.0, new($"Three {unique}", "Artist"));

        var (folderId, _) = await _scanner.RegisterFolderAsync(_folder);
        var first = await _scanner.ImportFoldersAsync(new LibraryImportRequest { FolderIds = new[] { folderId } });

        Assert.Null(first.Error);
        Assert.Equal(3, first.FilesFound);
        Assert.Equal(3, first.Added);
        Assert.Equal(0, first.Unsupported);
        Assert.DoesNotContain(first.Issues, i => i.Kind == LibraryImportIssueKind.Unsupported);

        var again = await _scanner.ImportFoldersAsync(new LibraryImportRequest { FolderIds = new[] { folderId } });
        Assert.Equal(0, again.Added);
        Assert.Equal(3, again.AlreadyInLibrary);
    }

    // ── Playback, seeking, analysis decode ───────────────────────────────────────

    [Theory]
    [InlineData(16, 44100)]
    [InlineData(24, 48000)]
    public void PlainAiff_PlaysDirectly_AndSeeksExactly(int bits, int rate)
    {
        var path = AiffTestFiles.WriteAiff(Path.Combine(_folder, $"p{bits}.aiff"), bits, rate, 3.0, new("P", "A"));

        Assert.False(ExactSeek.NeedsDecodeForward(path));
        using var reader = PlayableAudio.Open(path, out var playable);
        Assert.Equal(path, playable); // NAudio's AiffFileReader, no ffmpeg copy
        Assert.Equal(rate, reader.WaveFormat.SampleRate);
        Assert.InRange(reader.TotalTime.TotalSeconds, 2.99, 3.01);

        ExactSeek.Seek(reader, path, 1.5);
        Assert.InRange(reader.CurrentTime.TotalSeconds, 1.49, 1.51);
        var buffer = new float[2048];
        Assert.Equal(buffer.Length, reader.Read(buffer, 0, buffer.Length));
        Assert.InRange(buffer.Max(Math.Abs), 0.3f, 0.6f); // the half-scale sine, not noise or silence
    }

    [Fact]
    public void AifcSowt_NeedsTheDecodedPath_AndPlaysThroughFfmpeg()
    {
        var path = AiffTestFiles.WriteAifcSowt(Path.Combine(_folder, "logic.aif"), 44100, 2.0);
        Assert.True(ExactSeek.NeedsDecodeForward(path)); // NAudio rejects "Compressed AIFC"
        if (!FfmpegAvailable()) return;

        using var reader = PlayableAudio.Open(path, out var playable);
        Assert.EndsWith(".wav", playable);
        Assert.InRange(reader.TotalTime.TotalSeconds, 1.95, 2.05);
        var buffer = new float[2048];
        reader.Read(buffer, 0, buffer.Length);
        Assert.InRange(buffer.Max(Math.Abs), 0.3f, 0.6f);
    }

    [Theory]
    [InlineData("an.aiff", false)]
    [InlineData("an.aifc", true)]
    public async Task AnalysisDecode_HandlesAiff(string name, bool aifc)
    {
        if (!FfmpegAvailable()) return;
        var path = Path.Combine(_folder, name);
        if (aifc) AiffTestFiles.WriteAifcSowt(path, 48000, 2.0);
        else AiffTestFiles.WriteAiff(path, 24, 48000, 2.0, new("An", "A"));

        var pipeline = new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance);
        var wav = await pipeline.DecodeToTempWavAsync(new TrackAudioSource(path));
        try
        {
            var (samples, sampleRate, channels) = AudioIngestionPipeline.ReadPcmFloat(wav);
            Assert.True(sampleRate > 0 && channels > 0);
            Assert.InRange(samples.Length / (double)(sampleRate * channels), 1.9, 2.1);
            Assert.InRange(samples.Max(Math.Abs), 0.3f, 0.6f);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            var ffmpeg = AudioIngestionPipeline.ResolveFfmpegPath();
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg, "-version")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            p!.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // no ffmpeg on this machine: the ffmpeg-only checks are skipped
        }
    }
}

/// <summary>Tests that point the process-wide <see cref="PlayableAudio.CacheDirectory"/> at their own temp folder.</summary>
[CollectionDefinition(Name)]
public sealed class PlayableAudioCacheCollection
{
    public const string Name = "PlayableAudio cache directory";
}
