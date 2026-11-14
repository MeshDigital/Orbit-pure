using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Maintenance;
using Xunit;

namespace SLSKDONET.Tests.Maintenance;

/// <summary>GA10 / #255: tracks whose style scores are missing or stale are found and queued (isolated DB).</summary>
public sealed class StyleBackfillServiceTests : IDisposable
{
    private const string Current = "discogs-effnet-bsdynamic-1|ABCDEF12|mel2";
    private readonly EventBusService _bus = new();

    public void Dispose() => _bus.Dispose();

    [Fact]
    public async Task FindsMissingAndStale_ButNotCurrent()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        string H(string n) => $"style-{n}-{tag}";
        await using (var db = new AppDbContext())
        {
            foreach (var n in new[] { "nofeatures", "notopstyle", "oldtag", "current" })
                db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = H(n), Artist = "A", Title = n, FilePath = n + ".flac" });
            db.AudioFeatures.AddRange(
                new AudioFeaturesEntity { TrackUniqueHash = H("notopstyle"), TopStyle = null, StyleModelTag = Current },
                new AudioFeaturesEntity { TrackUniqueHash = H("oldtag"), TopStyle = "Electronic---Noise", StyleModelTag = "discogs-effnet-bsdynamic-1|ABCDEF12" },
                new AudioFeaturesEntity { TrackUniqueHash = H("current"), TopStyle = "Electronic---Drum n Bass", StyleModelTag = Current });
            await db.SaveChangesAsync();
        }

        var service = new StyleBackfillService(new Factory(), _bus, NullLogger<StyleBackfillService>.Instance, () => Current);
        var needing = (await service.FindTracksNeedingStylesAsync()).Where(h => h.EndsWith(tag, StringComparison.Ordinal)).ToHashSet();

        Assert.Contains(H("nofeatures"), needing);
        Assert.Contains(H("notopstyle"), needing);
        Assert.Contains(H("oldtag"), needing);   // pre-#270 preprocessing: stale
        Assert.DoesNotContain(H("current"), needing);
    }

    [Fact]
    public async Task WithoutAModel_OnlyMissingStylesCount()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var old = $"style-old-{tag}";
        await using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = old, Artist = "A", Title = "T", FilePath = "x.flac" });
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = old, TopStyle = "Electronic---Techno", StyleModelTag = "anything" });
            await db.SaveChangesAsync();
        }

        var service = new StyleBackfillService(new Factory(), _bus, NullLogger<StyleBackfillService>.Instance, () => null);
        Assert.DoesNotContain(old, await service.FindTracksNeedingStylesAsync());
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AppDbContext());
    }
}
