using System;
using System.IO;
using SLSKDONET.Configuration;
using Xunit;

namespace SLSKDONET.Tests.Configuration;

public class ConfigManagerFrequentSourcesTests
{
    [Fact]
    public void SaveLoad_RoundTripsFrequentSourcesSettings()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"orbit-config-frequent-sources-{Guid.NewGuid():N}.ini");

        try
        {
            var manager = new ConfigManager(tempPath);
            var config = new AppConfig
            {
                EnableFrequentSources = true,
                FrequentSourcesStagingPath = @"C:\orbit\staging"
            };

            manager.Save(config);
            var loaded = manager.Load();

            Assert.True(loaded.EnableFrequentSources);
            Assert.Equal(@"C:\orbit\staging", loaded.FrequentSourcesStagingPath);
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
    public void Load_MissingFile_UsesFrequentSourcesDefaults()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"orbit-config-frequent-sources-defaults-{Guid.NewGuid():N}.ini");

        try
        {
            var manager = new ConfigManager(tempPath);
            var loaded = manager.Load();

            Assert.False(loaded.EnableFrequentSources);
            Assert.Equal(string.Empty, loaded.FrequentSourcesStagingPath);
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
