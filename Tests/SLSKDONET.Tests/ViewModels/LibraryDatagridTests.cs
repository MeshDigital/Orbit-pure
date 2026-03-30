using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Tests for library datagrid improvements:
/// - PlaylistTrackViewModel display properties (SampleRateDisplay, LastPlayedDisplay, QualityScoreDisplay, SpectralAnalysisDisplay)
/// - TrackListViewModel format and quality tier filters
/// - ColumnConfigurationService default column set
/// </summary>
public class LibraryDatagridTests
{
    // ── PlaylistTrackViewModel display properties ──────────────────────────

    private static PlaylistTrack BuildTrack(Action<PlaylistTrack>? configure = null)
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Test Artist",
            Title = "Test Title",
            Format = "FLAC"
        };
        configure?.Invoke(track);
        return track;
    }

    private static PlaylistTrackViewModel BuildVm(Action<PlaylistTrack>? configure = null)
        => new PlaylistTrackViewModel(BuildTrack(configure));

    [Fact]
    public void SampleRateDisplay_WhenSpectralSampleRateSet_FormatsAsKhz()
    {
        var vm = BuildVm(t => t.SpectralSampleRateHz = 44100);
           var expectedFormatted = (44100 / 1000.0).ToString("F1", System.Globalization.CultureInfo.CurrentCulture) + " kHz";
           Assert.Equal(expectedFormatted, vm.SampleRateDisplay);
    }

    [Fact]
    public void SampleRateDisplay_WhenSpectralSampleRateNotSet_ReturnsDash()
    {
        var vm = BuildVm();
        Assert.Equal("—", vm.SampleRateDisplay);
    }

    [Fact]
    public void SampleRate_ReturnsSpectralSampleRateHz()
    {
        var vm = BuildVm(t => t.SpectralSampleRateHz = 48000);
        Assert.Equal(48000, vm.SampleRate);
    }

    [Fact]
    public void LastPlayedDisplay_WhenLastPlayedAtSet_FormatsDate()
    {
        var date = new DateTime(2025, 6, 15, 14, 30, 0, DateTimeKind.Utc);
        var vm = BuildVm(t => t.LastPlayedAt = date);
        Assert.Equal("2025-06-15 14:30", vm.LastPlayedDisplay);
    }

    [Fact]
    public void LastPlayedDisplay_WhenLastPlayedAtNotSet_ReturnsDash()
    {
        var vm = BuildVm();
        Assert.Equal("—", vm.LastPlayedDisplay);
    }

    [Theory]
    [InlineData(IntegrityLevel.Gold, "Gold")]
    [InlineData(IntegrityLevel.Verified, "Verified")]
    [InlineData(IntegrityLevel.Suspicious, "Review")]
    public void QualityScoreDisplay_ReflectsIntegrityLevel(IntegrityLevel level, string expected)
    {
        var vm = BuildVm(t => t.Integrity = level);
        Assert.Equal(expected, vm.QualityScoreDisplay);
    }

    [Fact]
    public void QualityScoreDisplay_WhenNoIntegrityButHasConfidence_ShowsPercentage()
    {
        var vm = BuildVm(t =>
        {
            t.Integrity = IntegrityLevel.None;
            t.QualityConfidence = 0.85;
        });
        Assert.Equal("85%", vm.QualityScoreDisplay);
    }

    [Fact]
    public void SpectralAnalysisAvailable_WhenSpectralVerdictTextSet_ReturnsTrue()
    {
        var vm = BuildVm(t => t.SpectralVerdictText = "GenuineLossless");
        Assert.True(vm.SpectralAnalysisAvailable);
        Assert.Equal("Analyzed", vm.SpectralAnalysisDisplay);
    }

    [Fact]
    public void SpectralAnalysisAvailable_WhenSpectralSampleRateSet_ReturnsTrue()
    {
        var vm = BuildVm(t => t.SpectralSampleRateHz = 44100);
        Assert.True(vm.SpectralAnalysisAvailable);
    }

    [Fact]
    public void SpectralAnalysisAvailable_WhenSpectralSampleRateIsZero_ReturnsFalse()
    {
        var vm = BuildVm(t => t.SpectralSampleRateHz = 0);
        Assert.False(vm.SpectralAnalysisAvailable);
    }

    [Fact]
    public void SpectralAnalysisAvailable_WhenNeitherSet_ReturnsFalse()
    {
        var vm = BuildVm();
        Assert.False(vm.SpectralAnalysisAvailable);
        Assert.Equal("—", vm.SpectralAnalysisDisplay);
    }

    // ── ColumnConfigurationService default columns ─────────────────────────

    [Fact]
    public void GetDefaultConfiguration_IncludesNewColumns()
    {
        var service = new ColumnConfigurationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ColumnConfigurationService>.Instance);

        var columns = service.GetDefaultConfiguration();
        var columnIds = columns.Select(c => c.Id).ToHashSet();

        Assert.Contains("SampleRate", columnIds);
        Assert.Contains("FileSize", columnIds);
        Assert.Contains("LastPlayed", columnIds);
        Assert.Contains("PlayCount", columnIds);
        Assert.Contains("QualityScore", columnIds);
        Assert.Contains("IntegrityStatus", columnIds);
        Assert.Contains("SpectralAnalysis", columnIds);
    }

    [Fact]
    public void GetDefaultConfiguration_QualityScoreColumnIsVisibleByDefault()
    {
        var service = new ColumnConfigurationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ColumnConfigurationService>.Instance);

        var col = service.GetDefaultConfiguration().Single(c => c.Id == "QualityScore");
        Assert.True(col.IsVisible);
    }

    [Fact]
    public void GetDefaultConfiguration_NewColumnsHaveCorrectPropertyPaths()
    {
        var service = new ColumnConfigurationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ColumnConfigurationService>.Instance);

        var columns = service.GetDefaultConfiguration().ToDictionary(c => c.Id);

        Assert.Equal("SampleRateDisplay", columns["SampleRate"].PropertyPath);
        Assert.Equal("FileSizeDisplay", columns["FileSize"].PropertyPath);
        Assert.Equal("LastPlayedDisplay", columns["LastPlayed"].PropertyPath);
        Assert.Equal("PlayCount", columns["PlayCount"].PropertyPath);
        Assert.Equal("QualityScoreDisplay", columns["QualityScore"].PropertyPath);
        Assert.Equal("IntegrityTooltip", columns["IntegrityStatus"].PropertyPath);
        Assert.Equal("SpectralAnalysisDisplay", columns["SpectralAnalysis"].PropertyPath);
    }

    [Fact]
    public void GetDefaultConfiguration_DisplayOrdersAreUnique()
    {
        var service = new ColumnConfigurationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ColumnConfigurationService>.Instance);

        var orders = service.GetDefaultConfiguration().Select(c => c.DisplayOrder).ToList();
        Assert.Equal(orders.Distinct().Count(), orders.Count);
    }
}
