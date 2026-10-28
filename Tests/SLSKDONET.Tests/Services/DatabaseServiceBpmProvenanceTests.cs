using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Regression coverage for the actual root cause behind "even a correct BPM won't stick": before
/// this fix, DatabaseService.SyncDenormalizedFeaturesAsync unconditionally overwrote BPM on every
/// analysis save (LibraryEntries/PlaylistTracks/Tracks), with zero provenance awareness — stronger
/// than, and independent of, TrackRepository.ApplyMetadata's existing Spotify/MusicBrainz guard.
/// A file-tag-sourced or manually-edited BPM would survive exactly until the track was (re-)
/// analysed, at which point Essentia's value (confirmed this session to have a quantization bias
/// on breakbeat/DNB content) silently won again. Uses a synthetic hash against the real DB —
/// established pattern this session (see DownloadManagerDeleteTrackTests) — so it touches zero
/// real user data.
/// </summary>
public class DatabaseServiceBpmProvenanceTests
{
    [Fact]
    public async Task SaveAudioFeaturesAsync_DoesNotOverwriteBpm_WhenTagBpmIsSet()
    {
        var hash = Guid.NewGuid().ToString("N");
        var db = CreateDatabaseService();

        await using (var context = new AppDbContext())
        {
            context.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = hash, Artist = "Test", Title = "Test",
                BPM = 174.0, TagBPM = 174.0, // simulating a trusted tag-sourced BPM already applied
            });
            await context.SaveChangesAsync();
        }

        try
        {
            await db.SaveAudioFeaturesAsync(new AudioFeaturesEntity
            {
                TrackUniqueHash = hash,
                Bpm = 173.0f, // Essentia's (wrong) reading — must NOT win
            });

            await using var verify = new AppDbContext();
            var entry = await verify.LibraryEntries.FindAsync(hash);
            Assert.NotNull(entry);
            Assert.Equal(174.0, entry!.BPM); // untouched — TagBPM outranks analysis
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task SaveAudioFeaturesAsync_DoesNotOverwriteBpm_WhenManualBpmIsSet()
    {
        var hash = Guid.NewGuid().ToString("N");
        var db = CreateDatabaseService();

        await using (var context = new AppDbContext())
        {
            context.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = hash, Artist = "Test", Title = "Test",
                BPM = 140.0, ManualBPM = 140.0, // user hand-typed this in the Mix editor
            });
            await context.SaveChangesAsync();
        }

        try
        {
            await db.SaveAudioFeaturesAsync(new AudioFeaturesEntity
            {
                TrackUniqueHash = hash,
                Bpm = 70.0f, // e.g. a bad re-analysis half-time misdetection — must NOT win
            });

            await using var verify = new AppDbContext();
            var entry = await verify.LibraryEntries.FindAsync(hash);
            Assert.NotNull(entry);
            Assert.Equal(140.0, entry!.BPM);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    [Fact]
    public async Task SaveAudioFeaturesAsync_StillFillsBpm_WhenNeitherManualNorTagIsSet()
    {
        // Regression guard: the new provenance check must not accidentally stop analysis from
        // ever writing BPM — only when something more authoritative already exists.
        var hash = Guid.NewGuid().ToString("N");
        var db = CreateDatabaseService();

        await using (var context = new AppDbContext())
        {
            context.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = hash, Artist = "Test", Title = "Test",
                BPM = null, TagBPM = null, ManualBPM = null,
            });
            await context.SaveChangesAsync();
        }

        try
        {
            await db.SaveAudioFeaturesAsync(new AudioFeaturesEntity
            {
                TrackUniqueHash = hash,
                Bpm = 128.0f,
            });

            await using var verify = new AppDbContext();
            var entry = await verify.LibraryEntries.FindAsync(hash);
            Assert.NotNull(entry);
            Assert.Equal(128.0, entry!.BPM);
        }
        finally
        {
            await CleanupAsync(hash);
        }
    }

    private static async Task CleanupAsync(string hash)
    {
        await using var context = new AppDbContext();
        var entry = await context.LibraryEntries.FindAsync(hash);
        if (entry != null)
        {
            context.LibraryEntries.Remove(entry);
        }
        var features = await context.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == hash);
        if (features != null)
        {
            context.AudioFeatures.Remove(features);
        }
        await context.SaveChangesAsync();
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
}
