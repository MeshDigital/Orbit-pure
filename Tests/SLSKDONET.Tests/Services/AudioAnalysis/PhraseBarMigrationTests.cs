using System;
using System.IO;
using System.Linq;
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

/// <summary>Bar-indexed phrase migration (issue #225). Uses the isolated DB and a throwaway data dir.</summary>
public class PhraseBarMigrationTests
{
    [Fact]
    public async Task RunAsync_ConvertsSecondsToBars_AndSetsSource()
    {
        var hash = Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext())
        {
            db.AudioFeatures.Add(new AudioFeaturesEntity
            {
                TrackUniqueHash = hash, Bpm = 120, DownbeatOffsetSeconds = 2.0,
                BeatGridJson = "[2.0,2.5,3.0]", PhraseSegmentsSource = "Heuristic",
            });
            db.TrackPhrases.Add(new TrackPhraseEntity
            {
                TrackUniqueHash = hash, Type = PhraseType.Drop,
                StartTimeSeconds = 2.0f, EndTimeSeconds = 18.0f, // 16 s = 8 bars at 120 BPM
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        service.DataDirectory = Path.Combine(Path.GetTempPath(), "phrase-mig-" + Guid.NewGuid().ToString("N"));

        try
        {
            await service.RunAsync(default);

            await using var db = new AppDbContext();
            var phrase = await db.TrackPhrases.SingleAsync(p => p.TrackUniqueHash == hash);
            Assert.Equal(0, phrase.StartBar);
            Assert.Equal(8, phrase.BarCount);
            Assert.Equal("Heuristic", phrase.Source);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task RunAsync_BackfillsFromTrustedLegacyJson_WhenNoPhraseRows()
    {
        var hash = Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext())
        {
            db.AudioFeatures.Add(new AudioFeaturesEntity
            {
                TrackUniqueHash = hash, Bpm = 120, DownbeatOffsetSeconds = 0.0,
                BeatGridJson = "[0.0,0.5,1.0]", PhraseSegmentsSource = "Heuristic",
                PhraseSegmentsJson = "[{\"Label\":\"Intro\",\"Start\":0.0,\"Duration\":8.0,\"Confidence\":0.8},{\"Label\":\"Drop\",\"Start\":8.0,\"Duration\":16.0,\"Confidence\":0.9}]",
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        service.DataDirectory = Path.Combine(Path.GetTempPath(), "phrase-mig-" + Guid.NewGuid().ToString("N"));

        try
        {
            await service.RunAsync(default);

            await using var db = new AppDbContext();
            var phrases = await db.TrackPhrases.Where(p => p.TrackUniqueHash == hash).OrderBy(p => p.OrderIndex).ToListAsync();
            Assert.Equal(2, phrases.Count);
            Assert.Equal(0, phrases[0].StartBar);
            Assert.Equal(4, phrases[0].BarCount); // 8 s = 4 bars
            Assert.Equal(4, phrases[1].StartBar); // 8 s = 4 bars
            Assert.Equal(8, phrases[1].BarCount); // 16 s = 8 bars
            Assert.All(phrases, p => Assert.Equal("Heuristic", p.Source));
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotOverwriteUserPhrases()
    {
        var hash = Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext())
        {
            db.AudioFeatures.Add(new AudioFeaturesEntity
            {
                TrackUniqueHash = hash, Bpm = 120, DownbeatOffsetSeconds = 0.0,
                BeatGridJson = "[0.0,0.5,1.0]", PhraseSegmentsSource = "Heuristic",
            });
            db.TrackPhrases.Add(new TrackPhraseEntity
            {
                TrackUniqueHash = hash, Type = PhraseType.Drop,
                StartTimeSeconds = 3.0f, EndTimeSeconds = 9.0f, Source = "User",
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        service.DataDirectory = Path.Combine(Path.GetTempPath(), "phrase-mig-" + Guid.NewGuid().ToString("N"));

        try
        {
            await service.RunAsync(default);

            await using var db = new AppDbContext();
            var phrase = await db.TrackPhrases.SingleAsync(p => p.TrackUniqueHash == hash);
            Assert.Equal(0, phrase.StartBar); // untouched
            Assert.Equal(0, phrase.BarCount);
            Assert.Equal("User", phrase.Source);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    private static PhraseBarMigrationService CreateService()
    {
        var database = new DatabaseService(
            NullLogger<DatabaseService>.Instance,
            new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance),
            new TrackRepository(NullLogger<TrackRepository>.Instance),
            new Mock<IFileWriteService>().Object);
        return new PhraseBarMigrationService(NullLogger<PhraseBarMigrationService>.Instance, database, new EventBusService());
    }

    private static async Task CleanupAsync(string hash)
    {
        await using var db = new AppDbContext();
        db.AudioFeatures.RemoveRange(db.AudioFeatures.Where(f => f.TrackUniqueHash == hash));
        db.TrackPhrases.RemoveRange(db.TrackPhrases.Where(p => p.TrackUniqueHash == hash));
        await db.SaveChangesAsync();
    }
}
