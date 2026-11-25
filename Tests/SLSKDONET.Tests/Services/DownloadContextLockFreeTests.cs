using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Services.Models;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class DownloadContextLockFreeTests
{
    [Fact]
    public void BlacklistUser_IsCaseInsensitive_AndReturnsFrozenSnapshot()
    {
        var ctx = new DownloadContext(new PlaylistTrack { Artist = "A", Title = "B", TrackUniqueHash = "h1" });

        Assert.True(ctx.BlacklistUser("PeerOne"));
        Assert.False(ctx.BlacklistUser("peerone"));
        Assert.True(ctx.IsUserBlacklisted("PEERONE"));

        var snapshot = ctx.SnapshotBlacklistedUsers();
        Assert.Contains("peerone", snapshot);

        // Mutating the live context does not mutate the previously taken snapshot
        ctx.BlacklistUser("PeerTwo");
        Assert.DoesNotContain("PeerTwo", snapshot);

        ctx.ClearBlacklistedUsers();
        Assert.False(ctx.IsUserBlacklisted("PeerOne"));
        Assert.Empty(ctx.SnapshotBlacklistedUsers());
    }

    [Fact]
    public async Task BlacklistUser_ConcurrentReadWriteClear_IsThreadSafe()
    {
        var ctx = new DownloadContext(new PlaylistTrack { Artist = "A", Title = "B", TrackUniqueHash = "h2" });

        var writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                ctx.BlacklistUser($"peer-{worker}-{i}");
                if (i % 50 == 0) ctx.ClearBlacklistedUsers();
            }
        }));

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                var snap = ctx.SnapshotBlacklistedUsers();
                _ = snap.Count;
            }
        }));

        await Task.WhenAll(writers.Concat(readers));
    }

    [Fact]
    public void DownloadManager_HasNoSecondaryBlacklistLocks()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        var servicesDir = Path.Combine(dir!.FullName, "Services");
        var files = Directory.GetFiles(servicesDir, "DownloadManager*.cs");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("lock (ctx.BlacklistedUsers)", text);
            Assert.DoesNotContain("lock(ctx.BlacklistedUsers)", text);
        }
    }
}
