using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

public sealed class TimeTillDropBackfillServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "orbit-intro-backfill-" + Guid.NewGuid().ToString("N"));
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly string _markerPath;

    public TimeTillDropBackfillServiceTests()
    {
        Directory.CreateDirectory(_directory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory, "library.db")};Pooling=False")
            .Options;
        _factory = new Factory(options);
        _markerPath = Path.Combine(_directory, "backfill.done");
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task RunAsync_BackfillsOnlyMissingFirstDropTiming_AndPreservesOtherAnalysis()
    {
        var energy = MultiDropEnergy();
        Seed("early", JsonSerializer.Serialize(energy), dropTimeSeconds: 320);
        Seed("flat", JsonSerializer.Serialize(Enumerable.Repeat(0.4f, 120).ToArray()));
        Seed("already", JsonSerializer.Serialize(energy), timeTillDropSeconds: 7);
        Seed("invalid", "not-json");
        Seed("no-curve", "[]");

        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(3, result.Candidates);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.NoDrop);
        Assert.Equal(1, result.Invalid);
        Assert.Equal(32, Read("early").TimeTillDropSeconds!.Value, precision: 3);
        Assert.Equal(320, Read("early").DropTimeSeconds!.Value, precision: 3);
        Assert.Null(Read("flat").TimeTillDropSeconds);
        Assert.Equal(7, Read("already").TimeTillDropSeconds!.Value, precision: 3);
        Assert.Null(Read("no-curve").TimeTillDropSeconds);
        Assert.True(File.Exists(_markerPath));

        // A completed marker makes later startup checks a no-op.
        var second = await service.RunAsync();
        Assert.Equal(0, second.Candidates);
    }

    private TimeTillDropBackfillService CreateService() =>
        new(_factory, new EventBusService(), NullLogger<TimeTillDropBackfillService>.Instance, _markerPath);

    private void Seed(string hash, string energyJson, float? dropTimeSeconds = null, float? timeTillDropSeconds = null)
    {
        using var db = _factory.CreateDbContext();
        db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, Artist = "A", Title = hash, FilePath = "" });
        db.AudioFeatures.Add(new AudioFeaturesEntity
        {
            TrackUniqueHash = hash,
            Bpm = 120,
            TrackDuration = 420,
            EnergyCurveJson = energyJson,
            DropTimeSeconds = dropTimeSeconds,
            TimeTillDropSeconds = timeTillDropSeconds,
        });
        db.SaveChanges();
    }

    private AudioFeaturesEntity Read(string hash)
    {
        using var db = _factory.CreateDbContext();
        return db.AudioFeatures.AsNoTracking().Single(features => features.TrackUniqueHash == hash);
    }

    private static float[] MultiDropEnergy()
    {
        const int duration = 420;
        var energy = Enumerable.Repeat(0.2f, duration).ToArray();
        void SetEnergy(int start, int end, float value)
        {
            for (int i = start; i < end; i++) energy[i] = value;
        }
        SetEnergy(32, 112, 0.65f);
        SetEnergy(128, 208, 0.80f);
        SetEnergy(224, 304, 0.90f);
        SetEnergy(320, duration, 1.00f);
        return energy;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public Factory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
