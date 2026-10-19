using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// The folder import pipeline (LibraryFolderScannerService.ImportFoldersAsync) on real temp folders
/// with generated audio files, against the isolated test database: counts per category, dedupe,
/// batched writes with one completion event, throttled progress, cancel-keeps-what-was-added, and
/// the folder watcher holding back its own events while the folder is under import.
/// </summary>
public class LibraryImportPipelineTests : IDisposable
{
    private readonly string _folder;
    private readonly EventBusService _bus = new();
    private readonly LibraryFolderScannerService _scanner;

    public LibraryImportPipelineTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "orbit-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _scanner = new LibraryFolderScannerService(NullLogger<LibraryFolderScannerService>.Instance, null!, null!, _bus);
    }

    public void Dispose()
    {
        try
        {
            using var context = new AppDbContext();
            var prefix = _folder + Path.DirectorySeparatorChar;
            context.LibraryEntries.RemoveRange(context.LibraryEntries.Where(e => e.FilePath.StartsWith(prefix)).ToList());
            var normalized = _folder.TrimEnd(Path.DirectorySeparatorChar);
            context.LibraryFolders.RemoveRange(context.LibraryFolders.ToList()
                .Where(f => string.Equals(f.FolderPath.TrimEnd(Path.DirectorySeparatorChar), normalized, StringComparison.OrdinalIgnoreCase)));
            context.SaveChanges();
        }
        finally
        {
            _bus.Dispose();
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Import_CountsNewDuplicateUnsupportedAndCorruptFiles()
    {
        var unique = Guid.NewGuid().ToString("N");
        WriteWav("a.wav", $"Track A {unique}", "Artist");
        WriteWav(Path.Combine("sub", "b.wav"), $"Track B {unique}", "Artist");
        WriteWav("b copy.wav", $"Track B {unique}", "Artist");               // same track identity as b.wav
        File.WriteAllBytes(Path.Combine(_folder, "song.opus"), new byte[64]);   // audio, but not importable
        File.WriteAllBytes(Path.Combine(_folder, "cover.jpg"), new byte[64]);   // not audio: ignored entirely
        File.WriteAllBytes(Path.Combine(_folder, "broken.flac"), RandomBytes(4096)); // corrupt
        File.WriteAllBytes(Path.Combine(_folder, "empty.mp3"), Array.Empty<byte>()); // 0 bytes

        var (folderId, isNew) = await _scanner.RegisterFolderAsync(_folder);
        Assert.True(isNew);

        var result = await _scanner.ImportFoldersAsync(new LibraryImportRequest { FolderIds = new[] { folderId } });

        Assert.Null(result.Error);
        Assert.False(result.WasCancelled);
        Assert.Equal(5, result.FilesFound);          // 3 wav + flac + mp3
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(0, result.AlreadyInLibrary);
        Assert.Equal(1, result.Unsupported);
        Assert.Equal(2, result.Unreadable);
        Assert.Equal(0, result.Failed);
        Assert.Contains(result.Issues, i => i.Kind == LibraryImportIssueKind.Unsupported && i.Path.EndsWith("song.opus"));
        Assert.Contains(result.Issues, i => i.Kind == LibraryImportIssueKind.Unreadable && i.Path.EndsWith("broken.flac"));
        Assert.Contains(result.Issues, i => i.Kind == LibraryImportIssueKind.Unreadable && i.Path.EndsWith("empty.mp3"));
        Assert.Equal(2, CountEntriesInFolder());

        // Second pass: the two added files are matched by path without opening them again.
        var again = await _scanner.ImportFoldersAsync(new LibraryImportRequest { FolderIds = new[] { folderId } });
        Assert.Equal(0, again.Added);
        Assert.Equal(2, again.AlreadyInLibrary);
        Assert.Equal(1, again.Duplicates);
        Assert.Equal(2, CountEntriesInFolder());
    }

    [Fact]
    public async Task Import_WritesInBatches_PublishesOneCompletionEvent_AndThrottlesProgress()
    {
        var unique = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 30; i++) WriteWav($"t{i:D2}.wav", $"Song {i} {unique}", "Batch Artist");
        var (folderId, _) = await _scanner.RegisterFolderAsync(_folder);

        var completed = new List<LibraryImportCompletedEvent>();
        var analysisRequests = 0;
        using var s1 = _bus.GetEvent<LibraryImportCompletedEvent>().Subscribe(completed.Add);
        using var s2 = _bus.GetEvent<TrackAnalysisRequestedEvent>().Subscribe(_ => Interlocked.Increment(ref analysisRequests));
        var progress = new SyncProgress();

        var result = await _scanner.ImportFoldersAsync(new LibraryImportRequest
        {
            FolderIds = new[] { folderId },
            BatchSize = 7,
            ProgressInterval = TimeSpan.FromHours(1), // only the forced phase reports get through
        }, progress);

        Assert.Equal(30, result.Added);
        Assert.Equal(30, CountEntriesInFolder());
        var evt = Assert.Single(completed);              // one refresh signal, not 30
        Assert.Same(result, evt.Result);
        Assert.True(evt.Result.ChangedLibrary);
        Assert.Equal(0, analysisRequests);               // the sheet offers "Analyse N" instead
        Assert.InRange(progress.Reports.Count, 1, 10);   // phase transitions only, independent of file count
        Assert.Equal(LibraryImportPhase.Done, progress.Reports.Last().Phase);
        Assert.Equal(30, progress.Reports.Last().Added);
    }

    [Fact]
    public async Task Import_QueueAnalysis_RequestsAnalysisForEachAddedTrack()
    {
        var unique = Guid.NewGuid().ToString("N");
        WriteWav("x.wav", $"X {unique}", "A");
        WriteWav("y.wav", $"Y {unique}", "A");
        var (folderId, _) = await _scanner.RegisterFolderAsync(_folder);
        var requested = new ConcurrentBag<string>();
        using var sub = _bus.GetEvent<TrackAnalysisRequestedEvent>().Subscribe(e => requested.Add(e.TrackGlobalId));

        var result = await _scanner.ImportFoldersAsync(new LibraryImportRequest { FolderIds = new[] { folderId }, QueueAnalysis = true, Trigger = LibraryImportTrigger.Watch });

        Assert.Equal(2, result.Added);
        Assert.Equal(result.AddedTrackHashes.OrderBy(h => h), requested.OrderBy(h => h));
    }

    [Fact]
    public async Task Cancel_WhileAdding_StopsAfterTheBatch_AndKeepsWhatWasAdded()
    {
        var unique = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 20; i++) WriteWav($"c{i:D2}.wav", $"Cancel {i} {unique}", "C");
        var (folderId, _) = await _scanner.RegisterFolderAsync(_folder);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == LibraryImportPhase.Adding && p.Added >= 5) cts.Cancel();
        });

        var result = await _scanner.ImportFoldersAsync(new LibraryImportRequest
        {
            FolderIds = new[] { folderId },
            BatchSize = 5,
            ProgressInterval = TimeSpan.Zero,
        }, progress, cts.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(5, result.Added);
        Assert.Equal(5, CountEntriesInFolder());
        Assert.Equal(LibraryImportPhase.Done, progress.Reports.Last().Phase);
    }

    [Fact]
    public async Task Cancel_WhileReadingTags_StillAddsTheFilesAlreadyChecked()
    {
        var unique = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 40; i++) WriteWav($"r{i:D2}.wav", $"Read {i} {unique}", "R");
        var (folderId, _) = await _scanner.RegisterFolderAsync(_folder);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == LibraryImportPhase.Reading && p.Processed >= 3) cts.Cancel();
        });

        var result = await _scanner.ImportFoldersAsync(new LibraryImportRequest
        {
            FolderIds = new[] { folderId },
            ProgressInterval = TimeSpan.Zero,
        }, progress, cts.Token);

        Assert.True(result.WasCancelled);
        Assert.InRange(result.Added, 3, 39);
        Assert.Equal(result.Added, CountEntriesInFolder());
    }

    [Fact]
    public async Task Cancel_WhileScanning_AddsNothing()
    {
        WriteWav("s.wav", $"S {Guid.NewGuid():N}", "S");
        var (folderId, _) = await _scanner.RegisterFolderAsync(_folder);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _scanner.ImportFoldersAsync(new LibraryImportRequest { FolderIds = new[] { folderId } }, null, cts.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, CountEntriesInFolder());
    }

    [Fact]
    public async Task Watcher_HoldsBackEventsDuringTheInitialImport_ThenImportsThemAfterwards()
    {
        var unique = Guid.NewGuid().ToString("N");
        WriteWav("first.wav", $"First {unique}", "W");
        var (folderId, _) = await _scanner.RegisterFolderAsync(_folder, watch: true);

        using var watcher = new LibraryFolderWatchService(
            NullLogger<LibraryFolderWatchService>.Instance, null, _scanner, _bus, settleDelayMs: 50);
        watcher.SetFolderPathForTests(folderId, _folder);
        var toasts = new ConcurrentBag<ToastRequestedEvent>();
        using var toastSub = _bus.GetEvent<ToastRequestedEvent>().Subscribe(toasts.Add);

        var lateFile = string.Empty;
        int deferredDuringImport = -1, pendingDuringImport = -1;
        var progress = new SyncProgress(p =>
        {
            if (p.Phase != LibraryImportPhase.Reading || lateFile.Length > 0) return;
            // A file lands while the folder is being imported (after discovery).
            lateFile = WriteWav("late.wav", $"Late {unique}", "W");
            watcher.OnFileEvent(folderId, lateFile);
            deferredDuringImport = watcher.DeferredCount(folderId);
            pendingDuringImport = watcher.PendingCount(folderId);
        });

        Assert.False(_scanner.IsFolderImporting(folderId));
        var result = await _scanner.ImportFoldersAsync(new LibraryImportRequest { FolderIds = new[] { folderId }, ProgressInterval = TimeSpan.Zero }, progress);

        Assert.Equal(1, result.Added);          // only first.wav: the watcher didn't ingest in parallel
        Assert.Equal(1, deferredDuringImport);  // held back...
        Assert.Equal(0, pendingDuringImport);   // ...not scheduled
        Assert.False(_scanner.IsFolderImporting(folderId));

        // Replayed after the import's completion event, as a small background import with a toast.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (CountEntriesInFolder() < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(2, CountEntriesInFolder());
        Assert.Equal(0, watcher.DeferredCount(folderId));
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!toasts.Any(t => t.Message.Contains("1 new track added from Watch folder")) && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.Contains(toasts, t => t.Message.Contains("1 new track added from Watch folder"));
    }

    [Fact]
    public async Task Watcher_BatchesABurstOfFilesIntoOneImport()
    {
        var unique = Guid.NewGuid().ToString("N");
        var (folderId, _) = await _scanner.RegisterFolderAsync(_folder, watch: true);
        using var watcher = new LibraryFolderWatchService(
            NullLogger<LibraryFolderWatchService>.Instance, null, _scanner, _bus, settleDelayMs: 300);
        watcher.SetFolderPathForTests(folderId, _folder);
        var imports = new ConcurrentBag<LibraryImportResult>();
        using var sub = _bus.GetEvent<LibraryImportCompletedEvent>().Subscribe(e => imports.Add(e.Result));

        for (var i = 0; i < 5; i++) watcher.OnFileEvent(folderId, WriteWav($"burst{i}.wav", $"Burst {i} {unique}", "B"));
        watcher.OnFileEvent(folderId, Path.Combine(_folder, "notes.txt")); // ignored
        Assert.Equal(5, watcher.PendingCount(folderId));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (imports.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50);
        await Task.Delay(400);

        var import = Assert.Single(imports);
        Assert.Equal(LibraryImportTrigger.Watch, import.Trigger);
        Assert.Equal(5, import.Added);
    }

    [Fact]
    public void ThrottledReporter_ForwardsAtMostOnePerInterval_ButAlwaysForced()
    {
        var target = new SyncProgress();
        var reporter = new ThrottledProgressReporter(target, TimeSpan.FromHours(1));
        var snapshot = new LibraryImportProgress(LibraryImportPhase.Reading, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0);

        for (var i = 0; i < 1000; i++) reporter.Report(() => snapshot);
        reporter.Report(() => snapshot with { Phase = LibraryImportPhase.Done }, force: true);

        Assert.Equal(2, target.Reports.Count);
        Assert.Equal(LibraryImportPhase.Done, target.Reports[1].Phase);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private int CountEntriesInFolder()
    {
        using var context = new AppDbContext();
        var prefix = _folder + Path.DirectorySeparatorChar;
        return context.LibraryEntries.Count(e => e.FilePath.StartsWith(prefix));
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A minimal valid PCM WAV (one silent sample) with RIFF INFO title/artist tags.</summary>
    private string WriteWav(string relativePath, string title, string artist)
    {
        var path = Path.Combine(_folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int sampleRate = 44100;
        const short bitsPerSample = 16;
        const short channels = 1;
        const int dataSize = 2;
        using (var stream = new FileStream(path, FileMode.Create))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write((short)0);
        }

        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = title;
            file.Tag.Performers = new[] { artist };
            file.Save();
        }
        return path;
    }

    /// <summary>Synchronous, thread-safe IProgress (Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class SyncProgress : IProgress<LibraryImportProgress>
    {
        private readonly Action<LibraryImportProgress>? _onReport;
        private readonly object _lock = new();
        public SyncProgress(Action<LibraryImportProgress>? onReport = null) => _onReport = onReport;
        public List<LibraryImportProgress> Reports { get; } = new();

        public void Report(LibraryImportProgress value)
        {
            lock (_lock)
            {
                Reports.Add(value);
                _onReport?.Invoke(value);
            }
        }
    }
}
