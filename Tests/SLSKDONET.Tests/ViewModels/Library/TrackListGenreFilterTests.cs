using System;
using System.IO;
using System.Runtime.CompilerServices;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.Library;

/// <summary>
/// Regression tests for Epic #561 / #563 (GV2: GenreFilter on TrackListViewModel + view-state
/// round-trip + removal of dead StyleFilters).
/// </summary>
public sealed class TrackListGenreFilterTests
{
    [Fact]
    public void ViewState_RoundTripsGenreFilter_BucketSubgenreAndUnclassified()
    {
        var sut = (TrackListViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TrackListViewModel));

        var liquid = new GenreKey(GenreBucket.DrumAndBass, "Liquid");
        var state = new LibraryTrackListViewState();
        state.SetGenreFilter(liquid);

        sut.ApplyViewState(state);
        Assert.Equal(liquid, sut.GenreFilter);
        Assert.True(sut.HasGenreFilter);
        Assert.True(sut.HasActiveFilters);
        Assert.Equal("Genre: Liquid", sut.GenreFilterLabel);

        var captured = sut.CaptureViewState();
        Assert.Equal(liquid, captured.GetGenreFilter());

        // Unclassified round-trip
        var unclassifiedState = new LibraryTrackListViewState();
        unclassifiedState.SetGenreFilter(GenreKey.Unclassified);
        sut.ApplyViewState(unclassifiedState);
        Assert.Equal(GenreKey.Unclassified, sut.GenreFilter);
        Assert.Equal("Genre: Unclassified", sut.GenreFilterLabel);
        Assert.Equal(GenreKey.Unclassified, sut.CaptureViewState().GetGenreFilter());
    }

    [Fact]
    public void TrackListViewModel_AndXaml_HaveNoDeadStyleFilters_AndExposeGenreFilterChip()
    {
        var root = FindRepoRoot();
        var vmCs = File.ReadAllText(Path.Combine(root, "ViewModels", "Library", "TrackListViewModel.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "Views", "Avalonia", "TrackListView.axaml"));

        Assert.DoesNotContain("StyleFilters", vmCs, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadStyleFiltersAsync", vmCs, StringComparison.Ordinal);
        Assert.Contains("ClearGenreFilterCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("GenreFilterLabel", xaml, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root.");
    }
}
