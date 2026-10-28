using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Regression coverage for the Mix transition editor's inline BPM edit — modeled on the existing
/// UpdateRatingAsync/UpdateColorTagAsync pattern (LibraryEntries row, master Tracks row, every
/// matching PlaylistTracks row). Uses a synthetic hash against the real DB (this codebase's
/// established test pattern — see e.g. DownloadManagerDeleteTrackTests), so it touches zero real
/// user data.
/// </summary>
public class TrackRepositoryUpdateBpmTests
{
    private readonly TrackRepository _sut = new(NullLogger<TrackRepository>.Instance);

    [Fact]
    public async Task UpdateBpmAsync_UpdatesLibraryEntry_WhenOneExists()
    {
        var hash = Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, Artist = "Test", Title = "Test", BPM = 120 });
            await db.SaveChangesAsync();
        }

        try
        {
            await _sut.UpdateBpmAsync(hash, 145.5);

            await using var verify = new AppDbContext();
            var entry = await verify.LibraryEntries.FindAsync(hash);
            Assert.NotNull(entry);
            Assert.Equal(145.5, entry!.BPM);
        }
        finally
        {
            await using var cleanup = new AppDbContext();
            var entry = await cleanup.LibraryEntries.FindAsync(hash);
            if (entry != null)
            {
                cleanup.LibraryEntries.Remove(entry);
                await cleanup.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task UpdateBpmAsync_NoMatchingRows_DoesNotThrow()
    {
        var hash = Guid.NewGuid().ToString("N"); // never persisted anywhere
        var ex = await Record.ExceptionAsync(() => _sut.UpdateBpmAsync(hash, 128.0));
        Assert.Null(ex);
    }

    [Fact]
    public async Task UpdateBpmAsync_AlsoSetsManualBpm_SoItSurvivesReanalysis()
    {
        // A manual edit must outrank a future Essentia re-analysis — see
        // DatabaseService.SyncDenormalizedFeaturesAsync's ManualBPM/TagBPM guard, which this
        // field's presence (not just the primary BPM) is what protects against.
        var hash = Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, Artist = "Test", Title = "Test" });
            await db.SaveChangesAsync();
        }

        try
        {
            await _sut.UpdateBpmAsync(hash, 145.5);

            await using var verify = new AppDbContext();
            var entry = await verify.LibraryEntries.FindAsync(hash);
            Assert.NotNull(entry);
            Assert.Equal(145.5, entry!.BPM);
            Assert.Equal(145.5, entry.ManualBPM);
        }
        finally
        {
            await using var cleanup = new AppDbContext();
            var entry = await cleanup.LibraryEntries.FindAsync(hash);
            if (entry != null)
            {
                cleanup.LibraryEntries.Remove(entry);
                await cleanup.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task UpdateTagBpmAsync_SetsTagBpmAndPrimaryBpm_WhenNoManualOverride()
    {
        var hash = Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, Artist = "Test", Title = "Test", BPM = 173 });
            await db.SaveChangesAsync();
        }

        try
        {
            await _sut.UpdateTagBpmAsync(hash, 174.0);

            await using var verify = new AppDbContext();
            var entry = await verify.LibraryEntries.FindAsync(hash);
            Assert.NotNull(entry);
            Assert.Equal(174.0, entry!.TagBPM);
            Assert.Equal(174.0, entry.BPM);
        }
        finally
        {
            await using var cleanup = new AppDbContext();
            var entry = await cleanup.LibraryEntries.FindAsync(hash);
            if (entry != null)
            {
                cleanup.LibraryEntries.Remove(entry);
                await cleanup.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task UpdateTagBpmAsync_NeverDowngradesAManualOverride()
    {
        var hash = Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = hash, Artist = "Test", Title = "Test",
                BPM = 140, ManualBPM = 140, // user already hand-corrected this
            });
            await db.SaveChangesAsync();
        }

        try
        {
            await _sut.UpdateTagBpmAsync(hash, 174.0); // a re-scan finds a (different) tag value

            await using var verify = new AppDbContext();
            var entry = await verify.LibraryEntries.FindAsync(hash);
            Assert.NotNull(entry);
            Assert.Equal(174.0, entry!.TagBPM); // recorded for provenance...
            Assert.Equal(140.0, entry.BPM); // ...but the manual value still wins on the primary field
        }
        finally
        {
            await using var cleanup = new AppDbContext();
            var entry = await cleanup.LibraryEntries.FindAsync(hash);
            if (entry != null)
            {
                cleanup.LibraryEntries.Remove(entry);
                await cleanup.SaveChangesAsync();
            }
        }
    }
}
