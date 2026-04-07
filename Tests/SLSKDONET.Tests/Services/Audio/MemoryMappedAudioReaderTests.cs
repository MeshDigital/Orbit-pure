using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Services.IO;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

public class MemoryMappedAudioReaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MemMappedTests_" + Guid.NewGuid().ToString("N"));

    public MemoryMappedAudioReaderTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string WriteFile(string name, byte[] data)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] RandomBytes(int length)
    {
        var rng = new Random(42);
        var buf = new byte[length];
        rng.NextBytes(buf);
        return buf;
    }

    // ── threshold / constructor ───────────────────────────────────────────────

    [Fact]
    public void Constructor_DefaultThreshold_Is100MB()
    {
        var reader = new MemoryMappedAudioReader();
        Assert.Equal(100 * 1024 * 1024, reader.MmfThresholdBytes);
    }

    [Fact]
    public void Constructor_CustomThreshold_IsHonoured()
    {
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: 512);
        Assert.Equal(512, reader.MmfThresholdBytes);
    }

    [Fact]
    public void Constructor_ZeroThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryMappedAudioReader(0));
    }

    [Fact]
    public void Constructor_NegativeThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryMappedAudioReader(-1));
    }

    // ── OpenRead — small file (FileStream path) ───────────────────────────────

    [Fact]
    public void OpenRead_SmallFile_ReturnsReadableStream()
    {
        var data = RandomBytes(1024);
        var path = WriteFile("small.flac", data);
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: 4096);

        using var stream = reader.OpenRead(path);

        Assert.True(stream.CanRead);
        Assert.Equal(data.Length, stream.Length);
    }

    [Fact]
    public void OpenRead_SmallFile_DataMatchesOriginal()
    {
        var data = RandomBytes(1024);
        var path = WriteFile("small2.flac", data);
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: 4096);

        using var stream = reader.OpenRead(path);
        var result = new byte[data.Length];
        int read = stream.Read(result, 0, result.Length);

        Assert.Equal(data.Length, read);
        Assert.Equal(data, result);
    }

    // ── OpenRead — large file (MMF path) ─────────────────────────────────────

    [Fact]
    public void OpenRead_LargeFile_UsesMemoryMappedPath()
    {
        // Use a tiny threshold so our test file is treated as "large"
        const int threshold = 512;
        var data = RandomBytes(threshold + 1);
        var path = WriteFile("large.flac", data);
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: threshold);

        using var stream = reader.OpenRead(path);

        Assert.True(stream.CanRead);
        Assert.Equal(data.Length, stream.Length);
    }

    [Fact]
    public void OpenRead_LargeFile_DataMatchesOriginal()
    {
        const int threshold = 512;
        var data = RandomBytes(threshold + 200);
        var path = WriteFile("large2.flac", data);
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: threshold);

        using var stream = reader.OpenRead(path);
        var result = new byte[data.Length];
        int read = stream.Read(result, 0, result.Length);

        Assert.Equal(data.Length, read);
        Assert.Equal(data, result);
    }

    [Fact]
    public void OpenRead_LargeFile_StreamIsSeekable()
    {
        const int threshold = 256;
        var data = RandomBytes(threshold + 100);
        var path = WriteFile("seekable.flac", data);
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: threshold);

        using var stream = reader.OpenRead(path);
        stream.Seek(100, SeekOrigin.Begin);

        var tail = new byte[data.Length - 100];
        stream.Read(tail, 0, tail.Length);

        Assert.Equal(data[100..], tail);
    }

    // ── file-not-found ────────────────────────────────────────────────────────

    [Fact]
    public void OpenRead_MissingFile_ThrowsFileNotFoundException()
    {
        var reader = new MemoryMappedAudioReader();
        Assert.Throws<FileNotFoundException>(() => reader.OpenRead("/does/not/exist.flac"));
    }

    // ── ReadAllBytesAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAllBytesAsync_SmallFile_ReturnsCorrectBytes()
    {
        var data = RandomBytes(2048);
        var path = WriteFile("allbytes_small.wav", data);
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: 4096);

        var result = await reader.ReadAllBytesAsync(path);

        Assert.Equal(data, result);
    }

    [Fact]
    public async Task ReadAllBytesAsync_LargeFile_ReturnsCorrectBytes()
    {
        const int threshold = 512;
        var data = RandomBytes(threshold + 300);
        var path = WriteFile("allbytes_large.wav", data);
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: threshold);

        var result = await reader.ReadAllBytesAsync(path);

        Assert.Equal(data, result);
    }

    [Fact]
    public async Task ReadAllBytesAsync_MissingFile_ThrowsFileNotFoundException()
    {
        var reader = new MemoryMappedAudioReader();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => reader.ReadAllBytesAsync("/nonexistent/audio.flac"));
    }

    // ── boundary: exactly at threshold ───────────────────────────────────────

    [Fact]
    public void OpenRead_ExactlyAtThreshold_UsesFileStream()
    {
        const int threshold = 1024;
        var data = RandomBytes(threshold); // equal → below the ">" condition → FileStream
        var path = WriteFile("exact_threshold.flac", data);
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: threshold);

        using var stream = reader.OpenRead(path);

        Assert.Equal(data.Length, stream.Length);
        var result = new byte[data.Length];
        stream.Read(result, 0, result.Length);
        Assert.Equal(data, result);
    }

    [Fact]
    public void OpenRead_OneByteLargerThanThreshold_UsesMemoryMappedFile()
    {
        const int threshold = 1024;
        var data = RandomBytes(threshold + 1); // strictly above → MMF path
        var path = WriteFile("one_over.flac", data);
        var reader = new MemoryMappedAudioReader(mmfThresholdBytes: threshold);

        using var stream = reader.OpenRead(path);

        Assert.Equal(data.Length, stream.Length);
        var result = new byte[data.Length];
        stream.Read(result, 0, result.Length);
        Assert.Equal(data, result);
    }
}
