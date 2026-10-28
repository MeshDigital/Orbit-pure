using System.Reflection;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Avalonia RadioButton groups write false to the old selection before writing true to the new
/// selection. These tests reproduce that exact ordering without requiring a UI test runner.
/// </summary>
public class TrackListStatusFilterTests
{
    [Fact]
    public void DownloadedSelection_AllowsTransientAllFalseBeforeApplyingDownloaded()
    {
        var sut = CreateSut();

        sut.IsFilterAll = false;
        Assert.False(sut.IsFilterAll);

        sut.IsFilterDownloaded = true;

        Assert.False(sut.IsFilterAll);
        Assert.True(sut.IsFilterDownloaded);
        Assert.False(sut.IsFilterPending);
    }

    [Fact]
    public void PendingSelection_AllowsOldRadioToClearBeforeApplyingPending()
    {
        var sut = CreateSut();
        sut.IsFilterAll = false;
        sut.IsFilterDownloaded = true;

        sut.IsFilterDownloaded = false;
        Assert.False(sut.IsFilterAll);

        sut.IsFilterPending = true;

        Assert.False(sut.IsFilterAll);
        Assert.False(sut.IsFilterDownloaded);
        Assert.True(sut.IsFilterPending);
    }

    [Fact]
    public void AllSelection_StillClearsDownloadedAndPending()
    {
        var sut = CreateSut();
        sut.IsFilterAll = false;
        sut.IsFilterPending = true;

        sut.IsFilterAll = true;

        Assert.True(sut.IsFilterAll);
        Assert.False(sut.IsFilterDownloaded);
        Assert.False(sut.IsFilterPending);
    }

    private static TrackListViewModel CreateSut()
    {
        var sut = (TrackListViewModel)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(TrackListViewModel));
        typeof(TrackListViewModel)
            .GetField("_isFilterAll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sut, true);
        return sut;
    }
}
