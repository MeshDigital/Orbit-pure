using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Configuration;
using Xunit;

namespace SLSKDONET.Tests.Configuration;

/// <summary>
/// Regression: many callers fire-and-forget SaveAsync. Two overlapping saves opened config.ini at
/// once ("file is being used by another process"), and the unobserved failure surfaced in the
/// Error Stream (seen during the first-run setup wizard, 2026-10-19).
/// </summary>
public class ConfigManagerConcurrentSaveTests
{
    [Fact]
    public async Task OverlappingSaves_DoNotThrow_AndLeaveAValidConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"orbit-config-concurrent-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "config.ini");
        try
        {
            var manager = new ConfigManager(path);
            var saves = Enumerable.Range(0, 40).Select(i =>
                manager.SaveAsync(new AppConfig { RankingProfile = $"Profile {i}" }));

            await Task.WhenAll(saves); // SaveAsync must never throw

            var loaded = manager.Load();
            Assert.StartsWith("Profile ", loaded.RankingProfile);
            Assert.False(File.Exists(path + ".tmp")); // the temp file is swapped in, not left behind
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
