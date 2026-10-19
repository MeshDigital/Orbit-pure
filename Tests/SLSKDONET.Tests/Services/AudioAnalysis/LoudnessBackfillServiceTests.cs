using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>
/// LoudnessBackfillService against a real SQLite database. Each test gets its own file next to the
/// isolated test library (ORBIT_DB_PATH, set by TestDatabaseSetup), so candidate counts aren't
/// polluted by other tests and the user's live library.db is never touched.
/// </summary>
public sealed class LoudnessBackfillServiceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly EventBusService _bus = new();
    private readonly ConcurrentQueue<object> _events = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Guid _playlistId = Guid.NewGuid();

    public LoudnessBackfillServiceTests(ITestOutputHelper output)
    {
        _output = output;
        var isolatedRoot = Path.GetDirectoryName(OrbitPaths.LibraryDbPath)!;
        Assert.Contains("orbit-tests", isolatedRoot); // never the user's AppData library
        _dir = Path.Combine(isolatedRoot, "loudness-backfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "library.db")};Pooling=False")
            .Options;
        _factory = new Factory(options);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Projects.Add(new PlaylistJobEntity { Id = _playlistId, SourceTitle = "Set", CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        _subscriptions.Add(_bus.GetEvent<TrackLoudnessMeasuredEvent>().Subscribe(e => _events.Enqueue(e)));
        _subscriptions.Add(_bus.GetEvent<TrackAnalysisUpdatedEvent>().Subscribe(e => _events.Enqueue(e)));
        _subscriptions.Add(_bus.GetEvent<SLSKDONET.Events.LibraryUpdatedEvent>().Subscribe(e => _events.Enqueue(e)));
        _subscriptions.Add(_bus.GetEvent<ToastRequestedEvent>().Subscribe(e => _events.Enqueue(e)));
        _subscriptions.Add(_bus.GetEvent<LoudnessBackfillProgressEvent>().Subscribe(e => _events.Enqueue(e)));
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _bus.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException ex) { _output.WriteLine($"temp cleanup: {ex.Message}"); }
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public Factory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    /// <summary>A library entry + audio features + playlist row + master track row, every stored
    /// loudness copy holding <paramref name="stored"/>, as a pre-fix analysis left them.</summary>
    private string Seed(string hash, float stored, bool fileExists = true, float dynamicComplexity = 1.5f)
    {
        var path = Path.Combine(_dir, hash + ".flac");
        if (fileExists) File.WriteAllText(path, "audio");
        double? rowValue = stored;
        using var db = _factory.CreateDbContext();
        db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = path, Artist = "A", Title = hash, Loudness = rowValue });
        db.AudioFeatures.Add(new AudioFeaturesEntity
        {
            TrackUniqueHash = hash,
            LoudnessLUFS = stored,
            DynamicComplexity = dynamicComplexity,
            IsDynamicCompressed = dynamicComplexity < 2f && stored > -7f,
        });
        db.PlaylistTracks.Add(new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = _playlistId, TrackUniqueHash = hash, ResolvedFilePath = path, Loudness = rowValue });
        db.Tracks.Add(new TrackEntity { GlobalId = hash, LocalFilePath = path, Loudness = rowValue });
        db.SaveChanges();
        return path;
    }

    private LoudnessBackfillService CreateService(LoudnessFileMeasurer measure, Func<bool>? isAnalysisBusy = null, int parallelism = 2) =>
        new(_factory, _bus, NullLogger<LoudnessBackfillService>.Instance, measure,
            isAnalysisBusy ?? (() => false), parallelism, TimeSpan.FromMilliseconds(10));

    private static LoudnessFileMeasurer Constant(double? lufs) => (_, _) => Task.FromResult(lufs);

    private (float Features, bool Compressed, double? Playlist, double? Library, double? Track) Stored(string hash)
    {
        using var db = _factory.CreateDbContext();
        var f = db.AudioFeatures.AsNoTracking().Single(x => x.TrackUniqueHash == hash);
        return (f.LoudnessLUFS,
                f.IsDynamicCompressed,
                db.PlaylistTracks.AsNoTracking().Single(x => x.TrackUniqueHash == hash).Loudness,
                db.LibraryEntries.AsNoTracking().Single(x => x.UniqueHash == hash).Loudness,
                db.Tracks.AsNoTracking().Single(x => x.GlobalId == hash).Loudness);
    }

    // ── Selection ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindCandidates_PicksLegacyAndUnknownValues_ExcludesValidLufsAndMissingFiles()
    {
        Seed("legacy", 0.8f);          // Essentia average_loudness stored as "LUFS"
        Seed("default", 0f);           // the NOT NULL column's default
        Seed("belowgate", -80f);       // under the absolute gate = unknown
        Seed("valid", -9.5f);          // real LUFS: leave alone
        Seed("gone", 0.8f, fileExists: false);

        var service = CreateService(Constant(-10));
        var candidates = await service.FindCandidatesAsync();

        Assert.Equal(new[] { "belowgate", "default", "legacy" }, candidates.Select(c => c.TrackUniqueHash).ToArray());
        Assert.All(candidates, c => Assert.True(File.Exists(c.FilePath)));
        Assert.Equal(3, await service.CountCandidatesAsync());
    }

    // ── Write-back ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_WritesEveryStoredCopy_AsAFullAnalysisWould_AndPublishesRefreshEvents()
    {
        Seed("t1", 0.8f, dynamicComplexity: 1.5f); // legacy value made it "over-compressed"
        Seed("valid", -11f);

        var result = await CreateService(Constant(-9.25)).RunAsync();

        Assert.Equal((1, 1, 0, 0, false), (result.Total, result.Measured, result.NoLoudness, result.Failed, result.Cancelled));
        var (features, compressed, playlist, library, track) = Stored("t1");
        Assert.Equal(-9.25f, features);
        Assert.False(compressed);                    // -9.25 LUFS is not above -7
        Assert.Equal((double)-9.25f, playlist);      // AsLufs of the stored float, like analysis
        Assert.Equal((double)-9.25f, library);
        Assert.Equal((double)-9.25f, track);
        Assert.Equal(-11f, Stored("valid").Features); // untouched

        Assert.Contains(_events, e => e is TrackLoudnessMeasuredEvent { TrackGlobalId: "t1", Success: true, Lufs: (double)-9.25f });
        Assert.Contains(_events, e => e is TrackAnalysisUpdatedEvent { TrackGlobalId: "t1" });
        Assert.Contains(_events, e => e is SLSKDONET.Events.LibraryUpdatedEvent);
        Assert.Contains(_events, e => e is ToastRequestedEvent t && t.Message.StartsWith("1 of 1 tracks measured"));
        Assert.Contains(_events, e => e is LoudnessBackfillProgressEvent { IsRunning: false, Done: 1, Total: 1 });
    }

    [Fact]
    public async Task Run_LoudCompressedMaster_KeepsTheOverCompressedFlag()
    {
        Seed("loud", 0.9f, dynamicComplexity: 1.2f);
        await CreateService(Constant(-5.5)).RunAsync();
        var stored = Stored("loud");
        Assert.Equal(-5.5f, stored.Features);
        Assert.True(stored.Compressed);
    }

    [Fact]
    public async Task Run_SilentFile_StoresUnknownLikeAnalysis_AndIsNotRetriedThisSession()
    {
        Seed("silent", 0.7f);
        var service = CreateService(Constant(null));

        var result = await service.RunAsync();

        Assert.Equal((1, 0, 1, 0), (result.Total, result.Measured, result.NoLoudness, result.Failed));
        var stored = Stored("silent");
        Assert.Equal(0f, stored.Features);   // analysis's "not measured"
        Assert.Null(stored.Playlist);        // the legacy 0.7 copies are cleared
        Assert.Null(stored.Library);
        Assert.Null(stored.Track);
        Assert.Empty(await service.FindCandidatesAsync());
    }

    [Fact]
    public async Task Run_CopiesAnExistingMeasurementOntoStaleRows_WithoutDecoding()
    {
        Seed("measured", -8f);
        using (var db = _factory.CreateDbContext())
        {
            // A playlist row added after analysis, and the master row analysis never writes.
            db.PlaylistTracks.Add(new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = _playlistId, TrackUniqueHash = "measured", Loudness = 0.8 });
            var track = db.Tracks.Single(t => t.GlobalId == "measured");
            track.Loudness = null;
            db.SaveChanges();
        }

        int decodes = 0;
        var result = await CreateService((_, _) => { Interlocked.Increment(ref decodes); return Task.FromResult<double?>(-1); }).RunAsync();

        Assert.Equal(0, decodes);
        Assert.Equal(2, result.SyncedCopies);
        using var check = _factory.CreateDbContext();
        Assert.All(check.PlaylistTracks.Where(t => t.TrackUniqueHash == "measured").ToList(), t => Assert.Equal(-8.0, t.Loudness));
        Assert.Equal(-8.0, check.Tracks.Single(t => t.GlobalId == "measured").Loudness);
    }

    // ── Failure, cancellation, resume ────────────────────────────────────────

    [Fact]
    public async Task Run_UnreadableFile_IsCountedAndSkipped_WithoutAbortingTheRest()
    {
        Seed("a", 0.8f);
        Seed("b", 0.8f);
        Seed("c", 0.8f);
        LoudnessFileMeasurer measure = (path, _) => Path.GetFileNameWithoutExtension(path) == "b"
            ? throw new InvalidOperationException("FFmpeg exit 1: Invalid data found when processing input")
            : Task.FromResult<double?>(-12);

        var service = CreateService(measure);
        var result = await service.RunAsync();

        Assert.Equal((3, 2, 1, false), (result.Total, result.Measured, result.Failed, result.Cancelled));
        Assert.Equal(-12f, Stored("a").Features);
        Assert.Equal(-12f, Stored("c").Features);
        Assert.Equal(0.8f, Stored("b").Features);
        Assert.Contains(_events, e => e is TrackLoudnessMeasuredEvent { TrackGlobalId: "b", Success: false });
        Assert.Contains(_events, e => e is ToastRequestedEvent t && t.Message.Contains("1 failed"));

        // A failed read is retried by the next run (the drive may be back), nothing else is.
        Assert.Equal(new[] { "b" }, (await service.FindCandidatesAsync()).Select(c => c.TrackUniqueHash));
    }

    [Fact]
    public async Task Run_Cancelled_KeepsWhatWasMeasured_AndARerunMeasuresOnlyTheRest()
    {
        const int count = 40; // more than two write batches
        for (int i = 0; i < count; i++) Seed($"t{i:00}", 0.8f);

        using var cts = new CancellationTokenSource();
        int firstRunCalls = 0;
        LoudnessFileMeasurer cancelling = (_, token) =>
        {
            if (Interlocked.Increment(ref firstRunCalls) == 20) cts.Cancel(); // mid second batch
            token.ThrowIfCancellationRequested();
            return Task.FromResult<double?>(-10);
        };

        var service = CreateService(cancelling, parallelism: 1);
        var first = await service.RunAsync(cts.Token);

        Assert.True(first.Cancelled);
        Assert.Equal(19, first.Measured);   // batch 1 (16) + 3 of batch 2 committed
        Assert.Equal(0, first.Failed);      // cancellation is not a failure
        Assert.False(service.IsRunning);
        Assert.Contains(_events, e => e is ToastRequestedEvent t && t.Message.Contains("Run it again to continue"));

        var remaining = await service.FindCandidatesAsync();
        Assert.Equal(count - 19, remaining.Count);

        var secondPaths = new ConcurrentBag<string>();
        var service2 = CreateService((path, _) => { secondPaths.Add(path); return Task.FromResult<double?>(-10); });
        var second = await service2.RunAsync();

        Assert.Equal((count - 19, count - 19, false), (second.Total, second.Measured, second.Cancelled));
        Assert.Equal(remaining.Select(c => c.FilePath).OrderBy(p => p), secondPaths.OrderBy(p => p));
        Assert.Empty(await service2.FindCandidatesAsync());
    }

    [Fact]
    public async Task Run_WhileTheAnalysisQueueIsBusy_WaitsAndSaysSo()
    {
        Seed("t1", 0.8f);
        int polls = 0;
        bool Busy() => Interlocked.Increment(ref polls) <= 3; // busy for the first three checks

        var result = await CreateService(Constant(-10), Busy).RunAsync();

        Assert.Equal(1, result.Measured);
        Assert.True(polls >= 4);
        Assert.Contains(_events, e => e is LoudnessBackfillProgressEvent { IsRunning: true, IsWaitingForAnalysis: true });
    }

    [Fact]
    public async Task RunAsync_RefusesASecondConcurrentRun()
    {
        Seed("t1", 0.8f);
        var gate = new TaskCompletionSource<double?>();
        var service = CreateService((_, _) => gate.Task);

        var running = service.RunAsync();
        while (!service.IsRunning) await Task.Delay(5);
        Assert.False(service.TryStart());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync());

        gate.SetResult(-10);
        Assert.Equal(1, (await running).Measured);
    }

    // ── Single track (inspector "Measure") ───────────────────────────────────

    [Fact]
    public async Task MeasureTrack_WritesThatTrack_AndPublishesTheResult()
    {
        Seed("one", 0.8f);
        Seed("other", 0.8f);

        var outcome = await CreateService(Constant(-7.5)).MeasureTrackAsync("one");

        Assert.True(outcome.Success);
        Assert.Equal((double)-7.5f, outcome.Lufs);
        Assert.Equal((double)-7.5f, Stored("one").Library);
        Assert.Equal(0.8f, Stored("other").Features);
        Assert.Contains(_events, e => e is TrackAnalysisUpdatedEvent { TrackGlobalId: "one" });
    }

    [Fact]
    public async Task MeasureTrack_MissingFile_ReportsFailureWithAToast()
    {
        Seed("gone", 0.8f, fileExists: false);

        var outcome = await CreateService(Constant(-7.5)).MeasureTrackAsync("gone");

        Assert.False(outcome.Success);
        Assert.Contains(_events, e => e is ToastRequestedEvent { Type: SLSKDONET.Views.NotificationType.Error });
        Assert.Equal(0.8f, Stored("gone").Features);
    }

    // ── End to end: real FFmpeg decode of generated files ────────────────────

    [Fact]
    public async Task EndToEnd_GeneratedWavAndFlac_DecodeThroughFfmpeg_ToTheCalibratedLoudness()
    {
        var ffmpeg = AudioIngestionPipeline.ResolveFfmpegPath();
        if (!File.Exists(ffmpeg)) { _output.WriteLine("ffmpeg not available; skipped"); return; }

        // Meter calibration (IntegratedLoudnessMeterTests): the same 1 kHz sine at −20 dBFS in both
        // channels reads −20.0 LUFS. 48 kHz input also checks the resample to analysis's 44.1 kHz.
        var wav = Path.Combine(_dir, "tone.wav");
        WriteSineWav(wav, sampleRate: 48000, seconds: 20, amplitude: 0.1f);
        var flac = Path.Combine(_dir, "tone.flac");
        if (!RunFfmpeg(ffmpeg, $"-v error -y -i \"{wav}\" -c:a flac \"{flac}\"")) { _output.WriteLine("flac encode failed; skipped"); return; }

        SeedFile("wavtrack", wav);
        SeedFile("flactrack", flac);

        var service = new LoudnessBackfillService(_factory, new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance),
            _bus, NullLogger<LoudnessBackfillService>.Instance);
        var sw = Stopwatch.StartNew();
        var result = await service.RunAsync();
        sw.Stop();
        _output.WriteLine($"2 × 20 s files measured in {sw.ElapsedMilliseconds} ms");

        Assert.Equal((2, 2, 0), (result.Total, result.Measured, result.Failed));
        Assert.InRange(Stored("wavtrack").Features, -20.2f, -19.8f);
        Assert.InRange(Stored("flactrack").Features, -20.2f, -19.8f);
        Assert.InRange(Stored("flactrack").Playlist!.Value, -20.2, -19.8);
    }

    [Fact]
    public async Task EndToEnd_CorruptFile_IsAFailureNotACrash()
    {
        var ffmpeg = AudioIngestionPipeline.ResolveFfmpegPath();
        if (!File.Exists(ffmpeg)) { _output.WriteLine("ffmpeg not available; skipped"); return; }

        var junk = Path.Combine(_dir, "junk.flac");
        File.WriteAllBytes(junk, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        SeedFile("junk", junk);

        var service = new LoudnessBackfillService(_factory, new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance),
            _bus, NullLogger<LoudnessBackfillService>.Instance);
        var result = await service.RunAsync();

        Assert.Equal((1, 0, 1), (result.Total, result.Measured, result.Failed));
    }

    private void SeedFile(string hash, string path)
    {
        using var db = _factory.CreateDbContext();
        db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = path, Loudness = 0.8 });
        db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = hash, LoudnessLUFS = 0.8f });
        db.PlaylistTracks.Add(new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = _playlistId, TrackUniqueHash = hash, ResolvedFilePath = path, Loudness = 0.8 });
        db.Tracks.Add(new TrackEntity { GlobalId = hash, LocalFilePath = path });
        db.SaveChanges();
    }

    private static void WriteSineWav(string path, int sampleRate, int seconds, float amplitude)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        var frame = new float[2];
        for (int i = 0; i < sampleRate * seconds; i++)
        {
            frame[0] = frame[1] = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * i / sampleRate));
            writer.WriteSamples(frame, 0, 2);
        }
    }

    private static bool RunFfmpeg(string ffmpeg, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
        if (p is null) return false;
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return p.ExitCode == 0;
    }
}
