using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Views.Avalonia.Controls;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.Library;

public class LibraryTrackListViewStateTests
{
    [Fact]
    public void Normalize_ClampsWidthsAndScroll_AndRepairsUnknownSort()
    {
        var state = new LibraryTrackListViewState
        {
            SortColumn = (TrackSortColumn)999,
            ArtistColumnWidth = 5000,
            EnergyColumnWidth = double.NaN,
            PreparationColumnWidth = 20,
            ScrollOffset = -50,
        };
        state.SelectedStyles.AddRange(new[] { "House", "house", "" });
        state.DuplicateHashes.AddRange(new[] { "a", "a", "" });

        state.Normalize();

        Assert.Equal(TrackSortColumn.Default, state.SortColumn);
        Assert.Equal(320, state.ArtistColumnWidth);
        Assert.Equal(TrackListColumnLayout.EnergyWidth, state.EnergyColumnWidth);
        Assert.Equal(96, state.PreparationColumnWidth);
        Assert.Equal(0, state.ScrollOffset);
        Assert.Equal(new[] { "House" }, state.SelectedStyles);
        Assert.Equal(new[] { "a" }, state.DuplicateHashes);
    }

    [Fact]
    public void ViewModelSnapshot_RoundTripsFiltersSortColumnsAndScroll()
    {
        var sut = (TrackListViewModel)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(TrackListViewModel));
        SetField(sut, "<StyleFilters>k__BackingField", new ObservableCollection<StyleFilterItem>());

        var expected = new LibraryTrackListViewState
        {
            SearchText = "artist: honey",
            FilterArtist = "honey",
            FilterTitle = "love",
            IsFilterAll = false,
            IsFilterDownloaded = true,
            IsFilterLiked = true,
            CamelotKeyFilter = "8A",
            QualityTierFilter = "Gold",
            IsBouncerActive = true,
            VibeFilter = "Energetic",
            IsFilterFlac = true,
            SortColumn = TrackSortColumn.Duration,
            SortDescending = true,
            IsColumnFilterStripVisible = true,
            IsFormatColumnVisible = false,
            IsForensicsColumnVisible = false,
            IsDurationColumnVisible = true,
            ArtistColumnWidth = 206,
            EnergyColumnWidth = 104,
            FormatColumnWidth = 122,
            ForensicsColumnWidth = 148,
            DurationColumnWidth = 92,
            RatingColumnWidth = 90,
            PreparationColumnWidth = 136,
            ScrollOffset = 840,
        };
        expected.SelectedStyles.Add("Techno");
        expected.DuplicateHashes.Add("same-hash");

        sut.ApplyViewState(expected);
        var actual = sut.CaptureViewState();

        Assert.Equal(expected.SearchText, actual.SearchText);
        Assert.Equal(expected.FilterArtist, actual.FilterArtist);
        Assert.Equal(expected.FilterTitle, actual.FilterTitle);
        Assert.True(actual.IsFilterDownloaded);
        Assert.True(actual.IsFilterLiked);
        Assert.Equal("8A", actual.CamelotKeyFilter);
        Assert.Equal("Gold", actual.QualityTierFilter);
        Assert.True(actual.IsBouncerActive);
        Assert.Equal("Energetic", actual.VibeFilter);
        Assert.True(actual.IsFilterFlac);
        Assert.Equal(TrackSortColumn.Duration, actual.SortColumn);
        Assert.True(actual.SortDescending);
        Assert.True(actual.IsColumnFilterStripVisible);
        Assert.False(actual.IsFormatColumnVisible);
        Assert.False(actual.IsForensicsColumnVisible);
        Assert.True(actual.IsDurationColumnVisible);
        Assert.Equal(206, actual.ArtistColumnWidth);
        Assert.Equal(104, actual.EnergyColumnWidth);
        Assert.Equal(122, actual.FormatColumnWidth);
        Assert.Equal(148, actual.ForensicsColumnWidth);
        Assert.Equal(92, actual.DurationColumnWidth);
        Assert.Equal(90, actual.RatingColumnWidth);
        Assert.Equal(136, actual.PreparationColumnWidth);
        Assert.Equal(840, actual.ScrollOffset);
        Assert.Equal(new[] { "Techno" }, actual.SelectedStyles);
        Assert.Equal(new[] { "same-hash" }, actual.DuplicateHashes);
    }

    [Fact]
    public void ResponsiveLayout_UsesCustomWidthsWhenChoosingColumnsToFold()
    {
        const double available = TrackListColumnLayout.AlwaysShownWidth
            + TrackListColumnLayout.EnergyWidth
            + TrackListColumnLayout.FormatWidth
            + TrackListColumnLayout.ForensicsWidth
            + TrackListColumnLayout.DurationWidth
            + TrackListColumnLayout.RatingWidth
            + TrackListColumnLayout.PreparationWidth;

        var defaults = TrackListColumnLayout.Fit(available, true, true, true);
        var widerPreparation = TrackListColumnLayout.Fit(
            available,
            true,
            true,
            true,
            preparationWidth: TrackListColumnLayout.PreparationWidth + 40);

        Assert.True(defaults.Forensics);
        Assert.True(defaults.Preparation);
        Assert.False(widerPreparation.Forensics);
        Assert.True(widerPreparation.Preparation);
        Assert.True(widerPreparation.Rating);
    }

    [Theory]
    [InlineData(1200, 2000, 500, 1200)]
    [InlineData(1800, 2000, 500, 1500)]
    [InlineData(-20, 2000, 500, 0)]
    [InlineData(200, 0, 0, 0)]
    public void ScrollRestore_ClampsToMeasuredExtent(double requested, double extent, double viewport, double expected)
    {
        Assert.Equal(expected, ScrollPositionRestorer.ClampOffset(requested, extent, viewport));
    }

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }
}
