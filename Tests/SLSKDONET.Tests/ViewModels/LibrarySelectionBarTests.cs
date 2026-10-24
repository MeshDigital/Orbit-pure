using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// The Library's "N selected" bar showed "1 selected" with nothing selected: the shared
/// "Loading…" placeholder row (one instance for every not-yet-loaded row) could be selected and
/// was counted. The bar now counts real rows only, and the placeholder can't be selected at all.
/// </summary>
public class LibrarySelectionBarTests
{
    private static PlaylistTrackViewModel Real(string title) => new(new PlaylistTrack { Artist = "A", Title = title, TrackUniqueHash = "h-" + title });

    [Fact]
    public void RealSelection_IgnoresThePlaceholderAndNulls()
    {
        var placeholder = PlaylistTrackViewModel.Placeholder;

        Assert.Empty(TrackListViewModel.RealSelection(new[] { placeholder }));
        Assert.Empty(TrackListViewModel.RealSelection(new PlaylistTrackViewModel?[] { null, placeholder, placeholder }));
    }

    [Fact]
    public void RealSelection_CountsEachRealRowOnce()
    {
        var a = Real("a");
        var b = Real("b");

        var selection = TrackListViewModel.RealSelection(new[] { a, PlaylistTrackViewModel.Placeholder, b, a });

        Assert.Equal(new[] { a, b }, selection);
    }

    [Fact]
    public void Placeholder_CannotBecomeSelected()
    {
        var placeholder = PlaylistTrackViewModel.Placeholder;

        placeholder.IsSelected = true;

        Assert.False(placeholder.IsSelected);
        Assert.True(((IPlaceholderRow)placeholder).IsPlaceholder);
    }

    [Fact]
    public void RealRow_SelectsNormally()
    {
        var a = Real("a");
        a.IsSelected = true;
        Assert.True(a.IsSelected);
        Assert.False(((IPlaceholderRow)a).IsPlaceholder);
    }
}
