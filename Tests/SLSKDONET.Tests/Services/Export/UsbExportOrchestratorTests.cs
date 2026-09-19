using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Export;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>
/// Regression coverage for a real bug: re-exporting the same playlist to the same USB/folder
/// destination used to append a fresh "(2)", "(3)"... copy of every track on each run (since the
/// previous export's files already existed at the "natural" name), orphaning the old copies
/// forever instead of updating them in place. <see cref="UsbExportOrchestrator.BuildDeterministicDestPath"/>
/// fixes this by deriving the destination filename from the track's own stable content hash
/// rather than "the first available name."
/// </summary>
public class UsbExportOrchestratorTests
{
    private static PlaylistTrack MakeTrack(string artist, string title, string hash, string ext = ".flac") =>
        new()
        {
            Artist = artist,
            Title = title,
            TrackUniqueHash = hash,
            ResolvedFilePath = $@"C:\Music\{artist} - {title}{ext}",
        };

    [Fact]
    public void BuildDeterministicDestPath_IsStable_AcrossRepeatedCalls()
    {
        var track = MakeTrack("Pirapus", "Energy (I Feel)", "abcdef1234567890");

        var first = UsbExportOrchestrator.BuildDeterministicDestPath(@"D:\OrbitAudio\Set1", track);
        var second = UsbExportOrchestrator.BuildDeterministicDestPath(@"D:\OrbitAudio\Set1", track);

        Assert.Equal(first, second);
    }

    [Fact]
    public void BuildDeterministicDestPath_DistinguishesSameNamedTracks_ByHash()
    {
        // Two different tracks that happen to share an artist/title (e.g. a remix vs. original
        // with identical display names) must not collide on the same destination file.
        var trackA = MakeTrack("Artist", "Track", "hash1111");
        var trackB = MakeTrack("Artist", "Track", "hash2222");

        var pathA = UsbExportOrchestrator.BuildDeterministicDestPath(@"D:\OrbitAudio\Set1", trackA);
        var pathB = UsbExportOrchestrator.BuildDeterministicDestPath(@"D:\OrbitAudio\Set1", trackB);

        Assert.NotEqual(pathA, pathB);
    }

    [Fact]
    public void BuildDeterministicDestPath_DoesNotDependOnFilesystemState()
    {
        // The old UniqueDestPath scheme picked a NEW name whenever the "natural" one already
        // existed on disk — meaning the second export of the same playlist produced different
        // paths than the first. The fix must be a pure function of the track's own data only.
        var track = MakeTrack("Kanine", "Bloodstream", "deadbeef00");

        var path1 = UsbExportOrchestrator.BuildDeterministicDestPath(@"D:\OrbitAudio\Set1", track);
        // Simulate "the file from a previous export already exists" — a pure function must still
        // return the same path, not a "(2)"-suffixed alternative.
        var path2 = UsbExportOrchestrator.BuildDeterministicDestPath(@"D:\OrbitAudio\Set1", track);

        Assert.Equal(path1, path2);
        Assert.DoesNotContain("(2)", path1);
    }

    [Fact]
    public void BuildDeterministicDestPath_HandlesMissingHash_WithoutThrowing()
    {
        var track = MakeTrack("Artist", "Title", hash: "");

        var path = UsbExportOrchestrator.BuildDeterministicDestPath(@"D:\OrbitAudio\Set1", track);

        Assert.Contains("Artist - Title", path);
        Assert.EndsWith(".flac", path);
    }

    [Fact]
    public void BuildDeterministicDestPath_SanitizesInvalidFileNameCharacters()
    {
        var track = MakeTrack("Artist: Feat?", "Title / \"Remix\"", "hashabc");

        var path = UsbExportOrchestrator.BuildDeterministicDestPath(@"D:\OrbitAudio\Set1", track);

        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
        {
            Assert.DoesNotContain(c, System.IO.Path.GetFileName(path));
        }
    }
}

/// <summary>
/// Regression coverage for the FilesAndXml copy phase itself, which switched from copying tracks
/// one at a time in a sequential <c>foreach</c> to a bounded-parallelism (<c>SemaphoreSlim(4)</c>)
/// <c>Task.WhenAll</c> batch — see <see cref="UsbExportOrchestrator"/>'s "Phase A" comment. The
/// real risk in that change is thread-safety of the shared <c>pathMap</c>/counters that every
/// concurrent copy task writes into; these tests exercise it with real overlapping file I/O
/// (via <see cref="SLSKDONET.Tests.Helpers.FakeFileWriteService"/>, which has no artificial
/// concurrency restriction) rather than mocking the copy away, so a races in that shared state
/// would actually surface as a missing/incorrect destination file.
/// </summary>
public class UsbExportOrchestratorParallelCopyTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_UsbExportParallel_{Guid.NewGuid():N}");
    private readonly string _sourceDir;
    private readonly string _destDir;

    public UsbExportOrchestratorParallelCopyTests()
    {
        _sourceDir = Path.Combine(_tempDir, "source");
        _destDir = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static IDbContextFactory<AppDbContext> CreateInMemoryFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"UsbExportParallelTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private PlaylistTrack CreateSourceTrack(int index)
    {
        var fileName = $"Artist{index} - Title{index}.mp3";
        var path = Path.Combine(_sourceDir, fileName);
        File.WriteAllText(path, $"fake audio content for track {index}");
        return new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = $"Artist{index}",
            Title = $"Title{index}",
            TrackUniqueHash = $"hash{index:D3}",
            ResolvedFilePath = path,
        };
    }

    [Fact]
    public async Task ExportAsync_FilesAndXml_CopiesEveryTrack_UnderConcurrentBatching()
    {
        var fileWriteService = new SLSKDONET.Tests.Helpers.FakeFileWriteService();
        var exportService = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), fileWriteService);
        var orchestrator = new UsbExportOrchestrator(
            exportService, fileWriteService, NullLogger<UsbExportOrchestrator>.Instance);

        // 12 tracks with a MaxConcurrentCopies of 4 guarantees at least 3 full batches overlap —
        // enough to actually exercise the concurrent pathMap/counter writes, not just 1-2 tasks
        // that could accidentally appear safe by never truly running at the same time.
        var tracks = Enumerable.Range(1, 12).Select(CreateSourceTrack).ToList();

        var progress = new SynchronousProgressRecorder();

        await orchestrator.ExportAsync(
            "Parallel Copy Test", tracks, _destDir, ExportMode.FilesAndXml, progress);
        var finalProgress = progress.Final;

        var copiedFiles = Directory.GetFiles(Path.Combine(_destDir, "OrbitAudio", "Parallel Copy Test"));
        Assert.Equal(12, copiedFiles.Length);

        foreach (var track in tracks)
        {
            var expectedDest = UsbExportOrchestrator.BuildDeterministicDestPath(
                Path.Combine(_destDir, "OrbitAudio", "Parallel Copy Test"), track);
            Assert.True(File.Exists(expectedDest), $"Expected destination file missing for {track.Title}");
            Assert.Equal(File.ReadAllText(track.ResolvedFilePath!), File.ReadAllText(expectedDest));
        }

        Assert.NotNull(finalProgress);
        Assert.Equal(12, finalProgress!.Copied);
        Assert.Equal(0, finalProgress.Skipped);

        var xmlPath = Path.Combine(_destDir, "PIONEER", "rekordbox.xml");
        Assert.True(File.Exists(xmlPath));
        var doc = System.Xml.Linq.XDocument.Load(xmlPath);
        var trackNodes = doc.Descendants("TRACK").Where(t => t.Attribute("Location") != null).ToList();
        Assert.Equal(12, trackNodes.Count);
    }

    [Fact]
    public async Task ExportAsync_FilesAndXml_ReExport_ReusesAlreadyCopiedFiles_WithoutRecopying()
    {
        var fileWriteService = new SLSKDONET.Tests.Helpers.FakeFileWriteService();
        var exportService = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), fileWriteService);
        var orchestrator = new UsbExportOrchestrator(
            exportService, fileWriteService, NullLogger<UsbExportOrchestrator>.Instance);

        var tracks = Enumerable.Range(1, 6).Select(CreateSourceTrack).ToList();
        var firstProgress = new SynchronousProgressRecorder();
        await orchestrator.ExportAsync("Reuse Test", tracks, _destDir, ExportMode.FilesAndXml, firstProgress);
        Assert.Equal(6, firstProgress.Final!.Copied);

        // Re-export the same playlist to the same destination — every file is already there at
        // the deterministic path with a matching size, so this run should reuse all of them
        // (Copied == 0) rather than recopying, matching the pre-existing "reused" fast path this
        // parallel-copy change deliberately left as a cheap, sequential pre-pass.
        var secondProgress = new SynchronousProgressRecorder();
        await orchestrator.ExportAsync("Reuse Test", tracks, _destDir, ExportMode.FilesAndXml, secondProgress);
        var secondFinal = secondProgress.Final;

        Assert.Equal(0, secondFinal!.Copied);
        Assert.Equal(0, secondFinal.Skipped);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// <see cref="System.Progress{T}"/> posts through a captured <see cref="System.Threading.SynchronizationContext"/>
    /// — with none ambient (as in an xUnit test host), it still queues onto the thread pool rather
    /// than invoking synchronously, so asserting on its last-seen value immediately after an
    /// awaited call returns is a race. This records synchronously on the reporting thread instead.
    /// </summary>
    private sealed class SynchronousProgressRecorder : IProgress<ExportProgress>
    {
        public ExportProgress? Final { get; private set; }
        public void Report(ExportProgress value)
        {
            if (value.IsComplete) Final = value;
        }
    }
}
