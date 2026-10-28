using System;
using System.IO;
using SLSKDONET.Configuration;
using Xunit;

namespace SLSKDONET.Tests.Configuration;

// ─────────────────────────────────────────────────────────────────────────
// ConfigManager uses a hand-written INI reader/writer — every AppConfig field
// must be explicitly listed in both Load() and Save(), or it works for the
// rest of the session but silently reverts to its compiled default on the
// next app restart. These settings were confirmed missing from that
// round-trip (Settings audit, 2026-09): EnableLibrarySharing,
// EnableNetworkActivityMonitor, the three SearchPolicy Safety Gates, and
// RankingProfile (which was being written under a "[MusicalIntelligence]"
// section that Load() never reads).
// ─────────────────────────────────────────────────────────────────────────

public class ConfigManagerPersistenceGapTests
{
    [Fact]
    public void SaveLoad_RoundTripsPreviouslyStaleSettings()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"orbit-config-persistence-gap-{Guid.NewGuid():N}.ini");

        try
        {
            var manager = new ConfigManager(tempPath);
            var config = new AppConfig
            {
                EnableLibrarySharing = false,
                EnableNetworkActivityMonitor = false,
                RankingProfile = "DJ Mode",
            };
            config.SearchPolicy.EnforceFileIntegrity = false;
            config.SearchPolicy.EnforceStrictTitleMatch = false;
            config.SearchPolicy.EnforceDurationMatch = false;

            manager.Save(config);
            var loaded = manager.Load();

            Assert.False(loaded.EnableLibrarySharing);
            Assert.False(loaded.EnableNetworkActivityMonitor);
            Assert.Equal("DJ Mode", loaded.RankingProfile);
            Assert.False(loaded.SearchPolicy.EnforceFileIntegrity);
            Assert.False(loaded.SearchPolicy.EnforceStrictTitleMatch);
            Assert.False(loaded.SearchPolicy.EnforceDurationMatch);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void Load_MissingFile_UsesCompiledDefaults()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"orbit-config-persistence-gap-defaults-{Guid.NewGuid():N}.ini");

        try
        {
            var manager = new ConfigManager(tempPath);
            var loaded = manager.Load();

            Assert.True(loaded.EnableLibrarySharing);
            Assert.True(loaded.EnableNetworkActivityMonitor);
            Assert.True(loaded.SearchPolicy.EnforceFileIntegrity);
            Assert.True(loaded.SearchPolicy.EnforceStrictTitleMatch);
            Assert.True(loaded.SearchPolicy.EnforceDurationMatch);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
