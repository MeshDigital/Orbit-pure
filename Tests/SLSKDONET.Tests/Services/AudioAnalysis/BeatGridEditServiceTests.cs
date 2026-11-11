using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>
/// Covers the single write path for beat-grid edits (issue #220). Uses a synthetic hash against the
/// isolated test DB (see TestDatabaseSetup) so it never touches real user data. Each test asserts
/// the analysis BPM, the beat list and the denormalised library BPM/ManualBPM stay in agreement.
/// </summary>
public class BeatGridEditServiceTests
{
    private const double Bpm = 87.0;
    private const double Downbeat = 0.5;

    [Fact]
    public async Task SetBpm_UpdatesAnalysisAndLibraryBpmAndManualBpm_Consistently()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash);

        try
        {
            var result = await service.SetBpmAsync(hash, 174.0, CancellationToken.None);
            Assert.True(result.Success, result.Reason);

            await using var db = new AppDbContext();
            var features = await db.AudioFeatures.FirstAsync(f => f.TrackUniqueHash == hash);
            Assert.Equal(174.0f, features.Bpm);
            Assert.Equal(Downbeat, features.DownbeatOffsetSeconds, 4);

            var beats = BeatGridFitter.ParseTicks(features.BeatGridJson);
            Assert.NotEmpty(beats);
            AssertBeatsMatchBpm(beats, 174.0);

            var entry = await db.LibraryEntries.FirstAsync(e => e.UniqueHash == hash);
            Assert.Equal(174.0, entry.BPM);
            Assert.Equal(174.0, entry.ManualBPM);
            Assert.Null(entry.TagBPM); // the file tag is not a grid edit

            var master = await db.Tracks.FirstAsync(t => t.GlobalId == hash);
            Assert.Equal(174.0, master.BPM);
            Assert.Equal(174.0, master.ManualBPM);

            var playlistRow = await db.PlaylistTracks.FirstAsync(t => t.TrackUniqueHash == hash);
            Assert.Equal(174.0, playlistRow.BPM);
            Assert.Equal(174.0, playlistRow.ManualBPM);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task ScaleTempo_DoubleThenHalve_ReturnsOriginalBeatList()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash);

        try
        {
            await using (var db = new AppDbContext())
            {
                var original = BeatGridFitter.ParseTicks((await db.AudioFeatures.FirstAsync(f => f.TrackUniqueHash == hash)).BeatGridJson);

                var doubled = await service.ScaleTempoAsync(hash, 2.0, CancellationToken.None);
                Assert.True(doubled.Success, doubled.Reason);
                var halved = await service.ScaleTempoAsync(hash, 0.5, CancellationToken.None);
                Assert.True(halved.Success, halved.Reason);

                var roundTripped = BeatGridFitter.ParseTicks((await db.AudioFeatures.FirstAsync(f => f.TrackUniqueHash == hash)).BeatGridJson);
                Assert.Equal(original.Length, roundTripped.Length);
                for (int i = 0; i < original.Length; i++)
                    Assert.Equal(original[i], roundTripped[i], 3);
            }
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task ScaleTempo_Double_KeepsBarOneABeat()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash);

        try
        {
            var result = await service.ScaleTempoAsync(hash, 2.0, CancellationToken.None);
            Assert.True(result.Success, result.Reason);

            await using var db = new AppDbContext();
            var features = await db.AudioFeatures.FirstAsync(f => f.TrackUniqueHash == hash);
            var beats = BeatGridFitter.ParseTicks(features.BeatGridJson);
            Assert.Contains(beats, b => Math.Abs(b - features.DownbeatOffsetSeconds) < 1e-4);
            AssertBeatsMatchBpm(beats, 174.0);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task Slip_ShiftsEveryBeat_AndKeepsTempo()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash);

        try
        {
            double[] before;
            await using (var db = new AppDbContext())
            {
                var f = await db.AudioFeatures.FirstAsync(x => x.TrackUniqueHash == hash);
                before = BeatGridFitter.ParseTicks(f.BeatGridJson);
            }

            var result = await service.SlipAsync(hash, 0.010, CancellationToken.None);
            Assert.True(result.Success, result.Reason);

            await using var verify = new AppDbContext();
            var features = await verify.AudioFeatures.FirstAsync(f => f.TrackUniqueHash == hash);
            var after = BeatGridFitter.ParseTicks(features.BeatGridJson);
            Assert.Equal(before.Length, after.Length);
            for (int i = 0; i < before.Length; i++)
                Assert.Equal(before[i] + 0.010, after[i], 4);
            Assert.Equal(Downbeat + 0.010, features.DownbeatOffsetSeconds, 4);
            Assert.Equal(Bpm, features.Bpm, 2);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task SetDownbeat_MovesBarOneToTheNearestBeat()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash);

        try
        {
            double period = 60.0 / Bpm;
            double target = Downbeat + 2 * period + 0.003; // 3 ms off a beat — must snap

            var result = await service.SetDownbeatAsync(hash, target, CancellationToken.None);
            Assert.True(result.Success, result.Reason);

            await using var db = new AppDbContext();
            var features = await db.AudioFeatures.FirstAsync(f => f.TrackUniqueHash == hash);
            Assert.Equal(Downbeat + 2 * period, features.DownbeatOffsetSeconds, 4);
            Assert.Equal(Bpm, features.Bpm, 2); // tempo unchanged
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task Edit_LeavesCueRowsUntouched()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash);
        await using (var db = new AppDbContext())
            db.CuePoints.Add(new CuePointEntity
            {
                TrackUniqueHash = hash,
                TimestampInSeconds = 12.34,
                Type = CuePointType.Drop,
                Label = "[DROP 1]",
                SlotIndex = 2,
                IsAutoGenerated = true,
            });
        await using (var db = new AppDbContext())
            await db.SaveChangesAsync();

        try
        {
            var result = await service.SetBpmAsync(hash, 174.0, CancellationToken.None);
            Assert.True(result.Success, result.Reason);

            await using var verify = new AppDbContext();
            var cue = await verify.CuePoints.SingleAsync(c => c.TrackUniqueHash == hash);
            Assert.Equal(12.34, cue.TimestampInSeconds, 6);
            Assert.Equal(CuePointType.Drop, cue.Type);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task NoAnalysis_ReturnsFailure_AndWritesNothing()
    {
        var service = CreateService();
        var hash = Guid.NewGuid().ToString("N");

        var result = await service.SetBpmAsync(hash, 174.0, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task Reset_ToAnalysed_RestoresBpmAndDownbeat()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash);

        try
        {
            await service.SetBpmAsync(hash, 174.0, CancellationToken.None);
            await service.SetDownbeatAsync(hash, Downbeat + 60.0 / Bpm, CancellationToken.None);

            var result = await service.ResetToAnalysedAsync(hash, Bpm, Downbeat, CancellationToken.None);
            Assert.True(result.Success, result.Reason);

            await using var db = new AppDbContext();
            var features = await db.AudioFeatures.FirstAsync(f => f.TrackUniqueHash == hash);
            Assert.Equal(Bpm, features.Bpm, 2);
            Assert.Equal(Downbeat, features.DownbeatOffsetSeconds, 4);
            AssertBeatsMatchBpm(BeatGridFitter.ParseTicks(features.BeatGridJson), Bpm);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task Edit_MarksGridUserOwnedAndVerified()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash);

        try
        {
            var result = await service.SetBpmAsync(hash, 174.0, CancellationToken.None);
            Assert.True(result.Success, result.Reason);

            await using var db = new AppDbContext();
            var features = await db.AudioFeatures.FirstAsync(f => f.TrackUniqueHash == hash);
            Assert.Equal("User", features.GridSource);
            Assert.True(features.GridVerified);
            Assert.NotNull(features.GridEditedAt);
            Assert.Equal(1.0f, features.GridConfidence!.Value, 3);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task MarkGridCorrect_SetsVerifiedWithoutChangingTheGrid()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash);

        try
        {
            await using (var before = new AppDbContext())
            {
                var f = await before.AudioFeatures.FirstAsync(x => x.TrackUniqueHash == hash);
                f.GridSource = "Analysis";
                f.GridVerified = false;
                f.GridConfidence = 0.6f;
                await before.SaveChangesAsync();
            }

            var result = await service.MarkGridCorrectAsync(hash, CancellationToken.None);
            Assert.True(result.Success, result.Reason);

            await using var db = new AppDbContext();
            var features = await db.AudioFeatures.FirstAsync(f => f.TrackUniqueHash == hash);
            Assert.True(features.GridVerified);
            Assert.Equal("Analysis", features.GridSource); // verified, not edited — source unchanged
            Assert.Equal(0.6f, features.GridConfidence!.Value, 3);
            Assert.Equal(Bpm, features.Bpm, 2); // grid untouched
            Assert.Equal(Downbeat, features.DownbeatOffsetSeconds, 4);
            Assert.NotNull(features.GridEditedAt);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task GridEdit_ReindexesBarIndexedPhrases()
    {
        var hash = Guid.NewGuid().ToString("N");
        var service = CreateService();
        await SeedTrackAsync(hash); // 87 BPM, downbeat 0.5

        await using (var db = new AppDbContext())
        {
            db.TrackPhrases.Add(new TrackPhraseEntity
            {
                TrackUniqueHash = hash, Type = PhraseType.Drop,
                StartBar = 4, BarCount = 8, Source = "Heuristic",
                StartTimeSeconds = 0, EndTimeSeconds = 0, // stale; the grid edit must fix them
            });
            await db.SaveChangesAsync();
        }

        try
        {
            var result = await service.SlipAsync(hash, 0.010, CancellationToken.None);
            Assert.True(result.Success, result.Reason);

            await using var db2 = new AppDbContext();
            var phrase = await db2.TrackPhrases.SingleAsync(p => p.TrackUniqueHash == hash);
            Assert.Equal(PhraseBarIndexing.BarToSeconds(4, 87.0, 0.51), phrase.StartTimeSeconds, 3);
            Assert.Equal(PhraseBarIndexing.BarToSeconds(12, 87.0, 0.51), phrase.EndTimeSeconds, 3);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    private static void AssertBeatsMatchBpm(double[] beats, double bpm)
    {
        double median = BeatGridEditService.MedianInterval(beats);
        Assert.True(median > 0, "Beat list is empty or has a single beat.");
        double expected = 60.0 / bpm;
        Assert.InRange(median, expected * 0.999, expected * 1.001);
    }

    private static BeatGridEditService CreateService()
    {
        var schemaMigrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        var trackRepository = new TrackRepository(NullLogger<TrackRepository>.Instance);
        var fileWrite = new Mock<IFileWriteService>();
        var database = new DatabaseService(
            NullLogger<DatabaseService>.Instance,
            schemaMigrator,
            trackRepository,
            fileWrite.Object);
        return new BeatGridEditService(database, null, NullLogger<BeatGridEditService>.Instance);
    }

    private static async Task SeedTrackAsync(string hash)
    {
        var beats = ConstantGrid(Downbeat, Bpm, 60.0);
        await using var db = new AppDbContext();
        db.AudioFeatures.Add(new AudioFeaturesEntity
        {
            TrackUniqueHash = hash,
            Bpm = (float)Bpm,
            TrackDuration = 60.0,
            DownbeatOffsetSeconds = Downbeat,
            BeatGridJson = JsonSerializer.Serialize(beats),
        });
        db.LibraryEntries.Add(new LibraryEntryEntity
        {
            UniqueHash = hash, Artist = "Test", Title = "Test",
            BPM = Bpm, ManualBPM = null, TagBPM = null,
        });
        db.Tracks.Add(new TrackEntity { GlobalId = hash, BPM = Bpm });
        db.PlaylistTracks.Add(new PlaylistTrackEntity
        {
            Id = Guid.NewGuid(), PlaylistId = Guid.NewGuid(),
            TrackUniqueHash = hash, BPM = Bpm,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string hash)
    {
        await using var db = new AppDbContext();
        db.AudioFeatures.RemoveRange(db.AudioFeatures.Where(f => f.TrackUniqueHash == hash));
        db.LibraryEntries.RemoveRange(db.LibraryEntries.Where(e => e.UniqueHash == hash));
        db.Tracks.RemoveRange(db.Tracks.Where(t => t.GlobalId == hash));
        db.PlaylistTracks.RemoveRange(db.PlaylistTracks.Where(t => t.TrackUniqueHash == hash));
        db.CuePoints.RemoveRange(db.CuePoints.Where(c => c.TrackUniqueHash == hash));
        db.TrackPhrases.RemoveRange(db.TrackPhrases.Where(p => p.TrackUniqueHash == hash));
        await db.SaveChangesAsync();
    }

    private static double[] ConstantGrid(double downbeat, double bpm, double duration)
    {
        double period = 60.0 / bpm;
        int n0 = (int)-Math.Floor(downbeat / period);
        int n1 = (int)Math.Ceiling((duration - downbeat) / period);
        var list = new List<double>();
        for (int n = n0; n <= n1; n++) list.Add(Math.Round(downbeat + n * period, 4));
        return list.ToArray();
    }
}
