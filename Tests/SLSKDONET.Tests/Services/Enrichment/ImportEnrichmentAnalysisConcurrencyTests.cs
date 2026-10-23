using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.ViewModels.Activity;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Services.Enrichment;

/// <summary>Records every log entry (to assert that no database-locked error escaped).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, exception, formatter(state, exception)));
}

/// <summary>A web provider with latency and an occasional 429, like Deezer under load. No network.</summary>
internal sealed class SlowFlakyProvider : IMetadataProvider
{
    private int _calls;
    private int _limited;
    private readonly ConcurrentDictionary<string, byte> _limitedTracks = new();
    private readonly int _limitEvery;
    private readonly TimeSpan _latency;

    public SlowFlakyProvider(string name, int limitEvery, TimeSpan latency)
    {
        Name = name;
        _limitEvery = limitEvery;
        _latency = latency;
        Runtime = new ProviderRuntime(name, RatePolicy.Unlimited);
    }

    public string Name { get; }
    public string DisplayName => Name;
    public string Description => "fake";
    public MetadataField Supplies => MetadataField.Artwork | MetadataField.Album | MetadataField.Label | MetadataField.ReleaseDate | MetadataField.Genre | MetadataField.Isrc;
    public ProviderRuntime Runtime { get; }
    public int Calls => Volatile.Read(ref _calls);
    public int RateLimited => Volatile.Read(ref _limited);
    public ProviderAvailability GetAvailability() => ProviderAvailability.Ready;

    public async Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _calls);
        Runtime.CountRequest();
        await Task.Delay(_latency + TimeSpan.FromMilliseconds(n % 7), ct).ConfigureAwait(false);
        // 429 at most once per track, so no track runs out of deferrals.
        if (_limitEvery > 0 && n % _limitEvery == 0 && _limitedTracks.TryAdd(track.Hash, 0))
        {
            Interlocked.Increment(ref _limited);
            return ProviderLookup.Limited(TimeSpan.FromMilliseconds(150), "429");
        }
        Runtime.CountMatch();
        var c = new MetadataCandidate
        {
            Album = "Provider Album", Label = "Provider Label", ReleaseDate = new DateTime(2019, 5, 1),
            Isrc = "GBAAA" + (Math.Abs(track.Hash.GetHashCode()) % 10_000_000).ToString("D7"), Confidence = 0.95,
            BpmHint = 87, KeyHint = "3A", // web tempo/key hints: must never reach BPM/Key
        };
        c.Genres.Add("Drum & Bass");
        return ProviderLookup.Match(c);
    }
}

/// <summary>The app's own DbContext (isolated test DB via ORBIT_DB_PATH), as a factory.</summary>
internal sealed class GlobalTestDbFactory : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new();
}

/// <summary>
/// The realistic first-run sequence against ONE database: a folder import of generated audio
/// files, enrichment started by the user mid-import (fake providers with latency and 429s) and
/// an analysis stub writing BPM/key/label into the same rows at the same time.
/// </summary>
[Collection(NonParallelCollection.Name)]
public sealed class ImportEnrichmentAnalysisConcurrencyTests : IDisposable
{
    private const int TrackCount = 300;
    private readonly string _folder;
    private readonly EventBusService _bus = new();
    private readonly ITestOutputHelper _output;

    public ImportEnrichmentAnalysisConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
        Assert.Contains("orbit-tests", Path.GetDirectoryName(OrbitPaths.LibraryDbPath)!); // never the user's library
        _folder = Path.Combine(Path.GetTempPath(), "orbit-concurrency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        using var db = new AppDbContext();
        db.EnrichmentTasks.RemoveRange(db.EnrichmentTasks.ToList()); // a leftover queue from another test would join the run
        db.SaveChanges();
    }

    public void Dispose()
    {
        try
        {
            using var db = new AppDbContext();
            var prefix = _folder + Path.DirectorySeparatorChar;
            var hashes = db.LibraryEntries.Where(e => e.FilePath.StartsWith(prefix)).Select(e => e.UniqueHash).ToList();
            db.LibraryEntries.RemoveRange(db.LibraryEntries.Where(e => e.FilePath.StartsWith(prefix)).ToList());
            db.EnrichmentTasks.RemoveRange(db.EnrichmentTasks.Where(t => hashes.Contains(t.TrackId)).ToList());
            db.MetadataProvenance.RemoveRange(db.MetadataProvenance.Where(p => hashes.Contains(p.TrackUniqueHash)).ToList());
            var normalized = _folder.TrimEnd(Path.DirectorySeparatorChar);
            db.LibraryFolders.RemoveRange(db.LibraryFolders.ToList()
                .Where(f => string.Equals(f.FolderPath.TrimEnd(Path.DirectorySeparatorChar), normalized, StringComparison.OrdinalIgnoreCase)));
            db.SaveChanges();
        }
        finally
        {
            _bus.Dispose();
            try { Directory.Delete(_folder, recursive: true); }
            catch (IOException) { /* temp folder */ }
        }
    }

    [Fact]
    public async Task ImportEnrichmentAndAnalysis_RunTogether_WithoutLostUpdatesLocksOrDuplicates()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < TrackCount; i++)
            WriteWav($"{i:D4}.wav", $"Song {i} {unique}", "Concurrency Artist");

        var scannerLog = new ListLogger<LibraryFolderScannerService>();
        var orchestratorLog = new ListLogger<MetadataEnrichmentOrchestrator>();
        var storeLog = new ListLogger<EnrichmentStore>();
        var scanner = new LibraryFolderScannerService(scannerLog, null!, null!, _bus);
        var factory = new GlobalTestDbFactory();
        var store = new EnrichmentStore(factory, storeLog);
        var deezer = new SlowFlakyProvider("deezer", limitEvery: 17, latency: TimeSpan.FromMilliseconds(4));
        var analysisRunning = 1;
        var orchestrator = new MetadataEnrichmentOrchestrator(factory, store, new IMetadataProvider[] { deezer }, _bus, orchestratorLog,
            () => EnrichmentSettingsFor.Providers("deezer"), artwork: null,
            isAnalysisBusy: () => Volatile.Read(ref analysisRunning) == 1, isPlaying: () => false, workers: 4, busyPoll: TimeSpan.FromMilliseconds(20),
            startupResumeDelay: null, isImportRunning: () => scanner.IsAnyImportRunning);

        // Events a UI would see.
        var enrichedEvents = 0;
        var progressEvents = 0;
        var batchEvents = 0;
        using var s1 = _bus.GetEvent<TracksEnrichedEvent>().Subscribe(_ => Interlocked.Increment(ref enrichedEvents));
        using var s2 = _bus.GetEvent<EnrichmentProgressEvent>().Subscribe(_ => Interlocked.Increment(ref progressEvents));
        var hub = new BackgroundActivityHubViewModel(_bus, null, new OrchestratorEnrichmentActivityControl(orchestrator), null,
            postToUi: a => a(), throttle: TimeSpan.FromMilliseconds(100));

        // The user presses "Start now" as soon as the first batch is in (mid-import).
        var started = 0;
        Task? manualStart = null;
        using var s3 = _bus.GetEvent<LibraryEntriesAddedEvent>().Subscribe(_ =>
        {
            Interlocked.Increment(ref batchEvents);
            if (Interlocked.Exchange(ref started, 1) == 0) manualStart = orchestrator.EnrichLibraryAsync();
        });

        // Analysis stub: writes analysis-owned BPM/key (and a label a user typed) into the same rows.
        var prefix = _folder + Path.DirectorySeparatorChar;
        var analysed = new ConcurrentDictionary<string, byte>();
        var stubErrors = new ConcurrentQueue<Exception>();
        using var stopStub = new CancellationTokenSource();
        var stub = Task.Run(async () =>
        {
            while (!stopStub.IsCancellationRequested)
            {
                try
                {
                    await SqliteBusyRetry.RunAsync(async () =>
                    {
                        await using var db = new AppDbContext();
                        var rows = await db.LibraryEntries.Where(e => e.FilePath.StartsWith(prefix) && e.BPM == null)
                            .OrderBy(e => e.FilePath).Take(10).ToListAsync();
                        foreach (var r in rows)
                        {
                            r.BPM = 174;
                            r.MusicalKey = "8A";
                            r.Energy = 0.8;
                            if (r.FilePath.EndsWith("3.wav")) r.Label = "Typed By User";
                        }
                        await db.SaveChangesAsync();
                        foreach (var r in rows) analysed[r.UniqueHash] = 0;
                    }, null, "analysis stub");
                }
                catch (Exception ex) { stubErrors.Enqueue(ex); }
                await Task.Delay(15);
            }
        });

        var sw = Stopwatch.StartNew();
        var (folderId, _) = await scanner.RegisterFolderAsync(_folder);
        var result = await scanner.ImportFoldersAsync(new LibraryImportRequest { FolderIds = new[] { folderId }, BatchSize = 50 });

        // Wait for analysis to cover every row, then for the queue to drain.
        await WaitUntilAsync(() => analysed.Count >= TrackCount, TimeSpan.FromSeconds(60));
        stopStub.Cancel();
        await stub;
        Volatile.Write(ref analysisRunning, 0);
        if (manualStart != null) await manualStart;
        await WaitUntilAsync(() => !orchestrator.IsRunning && store.CountPendingAsync().GetAwaiter().GetResult() == 0, TimeSpan.FromSeconds(90));
        sw.Stop();
        hub.ApplyNow();

        // ── The import ──
        Assert.Null(result.Error);
        Assert.Equal(TrackCount, result.Added);
        Assert.Equal(0, result.Failed);
        Assert.True(batchEvents >= TrackCount / 50, $"one LibraryEntriesAddedEvent per committed batch ({batchEvents})");

        await using var check = new AppDbContext();
        var entries = await check.LibraryEntries.AsNoTracking().Where(e => e.FilePath.StartsWith(prefix)).ToListAsync();
        Assert.Equal(TrackCount, entries.Count);
        Assert.All(entries, e => Assert.StartsWith("Song ", e.Title));           // tag titles, never placeholders
        Assert.All(entries, e => Assert.Equal("Concurrency Artist", e.Artist));

        // ── Enrichment filled the rows, analysis-owned fields untouched, user label kept ──
        Assert.All(entries, e =>
        {
            Assert.Equal("Provider Album", e.Album);
            Assert.Equal(174, e.BPM);
            Assert.Equal("8A", e.MusicalKey);
            Assert.Equal(0.8, e.Energy);
            Assert.Null(e.TagBPM);                         // a web BPM hint never lands anywhere
            Assert.Equal(e.FilePath.EndsWith("3.wav") ? "Typed By User" : "Provider Label", e.Label);
        });

        // ── No duplicate tasks: one per track, even with the manual start + the completion trigger ──
        var hashes = entries.Select(e => e.UniqueHash).ToHashSet();
        var tasks = await check.EnrichmentTasks.AsNoTracking().Where(t => hashes.Contains(t.TrackId)).ToListAsync();
        var perTrack = tasks.GroupBy(t => t.TrackId).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(TrackCount, perTrack.Count);
        Assert.All(perTrack.Values, n => Assert.Equal(1, n));
        Assert.All(tasks, t => Assert.Equal(EnrichmentStatus.Completed, t.Status));

        // ── No database-locked error escaped anywhere ──
        var allLogs = scannerLog.Entries.Concat(orchestratorLog.Entries).Concat(storeLog.Entries).ToList();
        Assert.DoesNotContain(allLogs, l => l.Level >= LogLevel.Error);
        Assert.Empty(stubErrors);
        var busyRetries = allLogs.Count(l => l.Message.Contains("Database busy"));

        // ── Bounded UI traffic ──
        Assert.True(enrichedEvents <= TrackCount / 5, $"TracksEnrichedEvent is batched ({enrichedEvents} for {TrackCount} tracks)");
        var maxHubApplies = (int)(sw.Elapsed.TotalMilliseconds / 100) + 20;
        Assert.True(hub.ApplyCount <= maxHubApplies, $"hub applied {hub.ApplyCount} times in {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.True(deezer.RateLimited > 0, "the fake provider answered 429 at least once");

        _output.WriteLine($"{TrackCount} tracks · {sw.Elapsed.TotalSeconds:0.0} s · provider calls {deezer.Calls} ({deezer.RateLimited} × 429) · " +
                          $"import batches {batchEvents} · TracksEnriched events {enrichedEvents} · progress events {progressEvents} · " +
                          $"hub applies {hub.ApplyCount} · busy retries {busyRetries}");
        hub.Dispose();
        orchestrator.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("Condition not met within " + timeout);
            await Task.Delay(50);
        }
    }

    private void WriteWav(string relativePath, string title, string artist)
    {
        var path = Path.Combine(_folder, relativePath);
        using (var stream = new FileStream(path, FileMode.Create))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(44100);
            writer.Write(44100 * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(2);
            writer.Write((short)0);
        }
        using var file = TagLib.File.Create(path);
        file.Tag.Title = title;
        file.Tag.Performers = new[] { artist };
        file.Save();
    }
}
