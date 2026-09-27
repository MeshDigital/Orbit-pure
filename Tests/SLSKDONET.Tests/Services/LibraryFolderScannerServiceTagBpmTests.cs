using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Regression coverage for reading a file's embedded BPM tag at import — the fix for Essentia's
/// confirmed quantization bias on breakbeat/DNB content (raw estimates from two different tracks
/// converged to 172.265xx to five decimal places). CreateLibraryEntry is private with no DB/network
/// side effects, so it's tested directly via reflection against a real (minimal, synthesized) WAV
/// file — the same TagLib.Tag.BeatsPerMinute read already proven in SeratoMetadataImporter, now
/// wired into the actual import path for the first time.
/// </summary>
public class LibraryFolderScannerServiceTagBpmTests
{
    [Fact]
    public void CreateLibraryEntry_PopulatesTagBpmAndBpm_WhenFileHasAPlausibleBpmTag()
    {
        var path = CreateTaggedWavFile(bpm: 174);
        try
        {
            var entry = InvokeCreateLibraryEntry(path);

            Assert.NotNull(entry);
            Assert.Equal(174.0, entry!.TagBPM);
            Assert.Equal(174.0, entry.BPM);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateLibraryEntry_LeavesBpmNull_WhenFileHasNoBpmTag()
    {
        var path = CreateTaggedWavFile(bpm: null);
        try
        {
            var entry = InvokeCreateLibraryEntry(path);

            Assert.NotNull(entry);
            Assert.Null(entry!.TagBPM);
            Assert.Null(entry.BPM);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateLibraryEntry_IgnoresAnImplausibleBpmTag()
    {
        // Out of BpmDetectionService's [60,220] sanity range — a garbage/placeholder tag value,
        // must not be trusted over an eventual real analysis.
        var path = CreateTaggedWavFile(bpm: 999);
        try
        {
            var entry = InvokeCreateLibraryEntry(path);

            Assert.NotNull(entry);
            Assert.Null(entry!.TagBPM);
            Assert.Null(entry.BPM);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LibraryEntryEntity? InvokeCreateLibraryEntry(string filePath)
    {
        var scanner = new LibraryFolderScannerService(
            NullLogger<LibraryFolderScannerService>.Instance,
            null!,
            null!,
            null!);

        var method = typeof(LibraryFolderScannerService).GetMethod(
            "CreateLibraryEntry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (LibraryEntryEntity?)method.Invoke(scanner, new object[] { filePath });
    }

    /// <summary>Writes a minimal valid (silent, 1-sample) PCM WAV file and, if <paramref name="bpm"/>
    /// is given, tags it via TagLib — same library/API this fix reads with.</summary>
    private static string CreateTaggedWavFile(uint? bpm)
    {
        var path = Path.Combine(Path.GetTempPath(), $"orbit-tagbpm-test-{Guid.NewGuid():N}.wav");

        const int sampleRate = 44100;
        const short bitsPerSample = 16;
        const short channels = 1;
        const int dataSize = 2; // one 16-bit silent sample

        using (var stream = new FileStream(path, FileMode.Create))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write((short)0);
        }

        if (bpm.HasValue)
        {
            using var file = TagLib.File.Create(path);
            file.Tag.BeatsPerMinute = bpm.Value;
            file.Save();
        }

        return path;
    }
}
