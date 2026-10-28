using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

public sealed class LibraryServiceAudioFeaturesPersistenceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_GridPersistence_{Guid.NewGuid():N}");

    public LibraryServiceAudioFeaturesPersistenceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ManualGridEdit_SurvivesReloadAndDrivesRekordboxTempoAnchor()
    {
        var database = CreateDatabaseService();
        var library = CreateLibraryService(database);
        var hash = $"grid-persistence-{Guid.NewGuid():N}";
        var audioPath = Path.Combine(_tempDir, "track.mp3");
        var outputPath = Path.Combine(_tempDir, "export.xml");
        await File.WriteAllTextAsync(audioPath, "fake audio");

        try
        {
            await using (var db = new AppDbContext())
            {
                db.AudioFeatures.Add(new AudioFeaturesEntity
                {
                    TrackUniqueHash = hash,
                    Bpm = 120,
                    TrackDuration = 8,
                    DownbeatOffsetSeconds = 0.25,
                    BeatGridJson = "[0.25,0.75,1.25]",
                });
                await db.SaveChangesAsync();
            }

            var edited = await library.GetAudioFeaturesByHashAsync(hash);
            Assert.NotNull(edited);
            edited!.Bpm = 128;
            edited.DownbeatOffsetSeconds = 0.75;
            edited.BeatGridJson = "[0.75,1.25,1.75]";

            await library.UpdateAudioFeaturesAsync(edited);

            // A fresh read proves this is persisted state, not just the edited object in memory.
            var reloaded = await library.GetAudioFeaturesByHashAsync(hash);
            Assert.NotNull(reloaded);
            Assert.Equal(128, reloaded!.Bpm);
            Assert.Equal(0.75, reloaded.DownbeatOffsetSeconds, 6);
            Assert.Equal("[0.75,1.25,1.75]", reloaded.BeatGridJson);

            var exporter = new PlaylistExportService(
                NullLogger<PlaylistExportService>.Instance,
                new DefaultDbContextFactory(),
                new Helpers.FakeFileWriteService());
            var track = new PlaylistTrack
            {
                Id = Guid.NewGuid(),
                Artist = "Artist",
                Title = "Manual grid",
                TrackUniqueHash = hash,
                ResolvedFilePath = audioPath,
                Status = TrackStatus.Downloaded,
                BPM = reloaded.Bpm,
                CanonicalDuration = 8000,
            };

            var preflight = await exporter.PreflightRekordboxExportAsync(new[] { track });
            Assert.True(preflight.CanExport, string.Join(Environment.NewLine, preflight.Blockers));
            await exporter.ExportToRekordboxXmlAsync("Persistence", new[] { track }, outputPath);

            var firstTempo = XDocument.Load(outputPath).Descendants("TEMPO").First();
            Assert.Equal("0.750", firstTempo.Attribute("Inizio")?.Value);
            Assert.Equal("128.00", firstTempo.Attribute("Bpm")?.Value);
        }
        finally
        {
            await using var db = new AppDbContext();
            var rows = await db.AudioFeatures.Where(features => features.TrackUniqueHash == hash).ToListAsync();
            db.AudioFeatures.RemoveRange(rows);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task TimeTillDropSeconds_PersistsNullableFirstDropTiming()
    {
        var database = CreateDatabaseService();
        var library = CreateLibraryService(database);
        var hash = $"intro-timing-{Guid.NewGuid():N}";

        try
        {
            await using (var db = new AppDbContext())
            {
                db.AudioFeatures.Add(new AudioFeaturesEntity
                {
                    TrackUniqueHash = hash,
                    Bpm = 174,
                    TrackDuration = 180,
                    TimeTillDropSeconds = null,
                });
                await db.SaveChangesAsync();
            }

            var unknown = await library.GetAudioFeaturesByHashAsync(hash);
            Assert.NotNull(unknown);
            Assert.Null(unknown!.TimeTillDropSeconds);

            unknown.TimeTillDropSeconds = 22.069f;
            await library.UpdateAudioFeaturesAsync(unknown);

            var reloaded = await library.GetAudioFeaturesByHashAsync(hash);
            Assert.NotNull(reloaded);
            Assert.Equal(22.069f, reloaded!.TimeTillDropSeconds!.Value, precision: 3);
        }
        finally
        {
            await using var db = new AppDbContext();
            var rows = await db.AudioFeatures.Where(features => features.TrackUniqueHash == hash).ToListAsync();
            db.AudioFeatures.RemoveRange(rows);
            await db.SaveChangesAsync();
        }
    }

    private static DatabaseService CreateDatabaseService()
    {
        var schemaMigrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        var trackRepository = new TrackRepository(NullLogger<TrackRepository>.Instance);
        var fileWrite = new Mock<IFileWriteService>();
        return new DatabaseService(
            NullLogger<DatabaseService>.Instance,
            schemaMigrator,
            trackRepository,
            fileWrite.Object);
    }

    private static LibraryService CreateLibraryService(DatabaseService databaseService) =>
        new(
            NullLogger<LibraryService>.Instance,
            databaseService,
            new AppConfig(),
            new EventBusService(),
            new LibraryCacheService());

    private sealed class DefaultDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
    }
}
