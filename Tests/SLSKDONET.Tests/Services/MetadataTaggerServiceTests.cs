using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.IO;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Exercises the real tag-writing logic in <see cref="MetadataTaggerService"/> against real
/// (minimal) audio files on disk, using a fake <see cref="IFileWriteService"/> that performs the
/// same write-temp/verify/swap contract without the real <c>SafeWriteService</c>'s dependency on
/// a <c>CrashRecoveryJournal</c> backed by the user's actual AppData database — this service was
/// previously registered in DI with zero callers (dead code) and had no test coverage at all; it's
/// now the only tag-write path reachable from the Library batch-edit dialog.
/// </summary>
public class MetadataTaggerServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_TaggerTest_{Guid.NewGuid():N}");

    public MetadataTaggerServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static readonly List<string> _debugLog = new();

    private MetadataTaggerService CreateService() =>
        new(new CapturingLogger<MetadataTaggerService>(_debugLog), new FakeFileWriteService());

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly List<string> _log;
        public CapturingLogger(List<string> log) => _log = log;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _log.Add($"[{logLevel}] {formatter(state, exception)} {exception}");
        }
    }

    private string CreateMinimalWavFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        WriteMinimalWav(path);
        return path;
    }

    private static void WriteMinimalWav(string path)
    {
        const int sampleRate = 8000;
        var samples = new short[sampleRate / 10]; // 100ms of silence — just needs to be a valid WAV
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataSize = samples.Length * 2;

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        foreach (var s in samples) bw.Write(s);
    }

    [Fact]
    public async Task TagFileAsync_WritesBasicFieldsToRealFile()
    {
        var path = CreateMinimalWavFile("basic.wav");
        var service = CreateService();
        var track = new Track { Title = "New Title", Artist = "New Artist", Album = "New Album" };

        var success = await service.TagFileAsync(track, path);

        Assert.True(success, string.Join(" | ", _debugLog));
        using var tagged = TagLib.File.Create(path);
        Assert.Equal("New Title", tagged.Tag.Title);
        Assert.Equal("New Artist", tagged.Tag.FirstPerformer);
        Assert.Equal("New Album", tagged.Tag.Album);
    }

    [Fact]
    public async Task TagFileAsync_KeyAndTrackNumberFromMetadataDict_AreWritten()
    {
        // These two fields are exactly what the old inline TagLib path in
        // LibraryViewModel.Commands.cs never wrote — pinning the fix.
        var path = CreateMinimalWavFile("key_tracknum.wav");
        var service = CreateService();
        var track = new Track
        {
            Metadata = new Dictionary<string, object>
            {
                ["MusicalKey"] = "8A",
                ["TrackNumber"] = "5",
            }
        };

        var success = await service.TagFileAsync(track, path);

        Assert.True(success);
        using var tagged = TagLib.File.Create(path);
        Assert.Equal("8A", tagged.Tag.InitialKey);
        Assert.Equal(5u, tagged.Tag.Track);
    }

    [Fact]
    public async Task TagFileAsync_BpmFromMetadataDict_IsWritten()
    {
        var path = CreateMinimalWavFile("bpm.wav");
        var service = CreateService();
        var track = new Track { Metadata = new Dictionary<string, object> { ["BPM"] = 128.0 } };

        var success = await service.TagFileAsync(track, path);

        Assert.True(success);
        using var tagged = TagLib.File.Create(path);
        Assert.Equal(128u, tagged.Tag.BeatsPerMinute);
    }

    [Fact]
    public async Task TagFileAsync_UnsupportedExtension_ReturnsFalseWithoutThrowing()
    {
        var path = Path.Combine(_tempDir, "notes.xyz");
        File.WriteAllText(path, "not audio");
        var service = CreateService();

        var success = await service.TagFileAsync(new Track { Title = "X" }, path);

        Assert.False(success);
    }

    [Fact]
    public async Task TagFileAsync_MissingFile_ReturnsFalseWithoutThrowing()
    {
        var service = CreateService();
        var path = Path.Combine(_tempDir, "does-not-exist.mp3");

        var success = await service.TagFileAsync(new Track { Title = "X" }, path);

        Assert.False(success);
    }

    /// <summary>
    /// Mirrors SafeWriteService's write-temp/verify/atomic-swap contract without its dependency on
    /// CrashRecoveryJournal (which hardcodes the user's real AppData library.db — unsafe to touch
    /// from a test).
    /// </summary>
    private sealed class FakeFileWriteService : IFileWriteService
    {
        public async Task<bool> WriteAtomicAsync(
            string targetPath, Func<string, Task> writeAction, Func<string, Task<bool>>? verifyAction = null,
            CancellationToken cancellationToken = default)
        {
            var tempPath = targetPath + ".tmp";
            try
            {
                await writeAction(tempPath);
                if (verifyAction != null && !await verifyAction(tempPath))
                {
                    File.Delete(tempPath);
                    return false;
                }
                File.Copy(tempPath, targetPath, overwrite: true);
                File.Delete(tempPath);
                return true;
            }
            catch
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                return false;
            }
        }

        public Task<bool> WriteAllBytesAtomicAsync(string targetPath, byte[] data, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> CopyFileAtomicAsync(string sourcePath, string targetPath, bool preserveTimestamps = true, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> MoveAtomicAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
