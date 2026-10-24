using SLSKDONET.ViewModels.Library;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.Library;

public class TrackListColumnLayoutTests
{
    private const double Full = TrackListColumnLayout.AlwaysShownWidth + TrackListColumnLayout.EnergyWidth + TrackListColumnLayout.FormatWidth
        + TrackListColumnLayout.ForensicsWidth + TrackListColumnLayout.DurationWidth + TrackListColumnLayout.RatingWidth;

    [Fact]
    public void WideList_ShowsEverythingYouChose()
    {
        Assert.Equal(new TrackListColumns(true, true, true, true, true), TrackListColumnLayout.Fit(Full + 10, true, true, true));
        Assert.Equal(new TrackListColumns(true, false, true, false, true), TrackListColumnLayout.Fit(Full + 10, false, true, false));
    }

    [Fact]
    public void NarrowList_DropsTheLeastImportantColumnsFirst()
    {
        // Just too narrow for everything: Forensics goes first.
        Assert.Equal(new TrackListColumns(true, true, false, true, true), TrackListColumnLayout.Fit(Full - 1, true, true, true));

        // ~820 px (context panel open on a laptop screen): Forensics, Rating, Duration go; Format and Energy stay.
        Assert.Equal(new TrackListColumns(true, true, false, false, false), TrackListColumnLayout.Fit(820, true, true, true));

        // 600 px: every optional data column is gone, artist and the hover actions still fit.
        Assert.Equal(new TrackListColumns(false, false, false, false, false), TrackListColumnLayout.Fit(600, true, true, true));
    }

    [Fact]
    public void VeryNarrowList_DropsHoverActionsThenFoldsArtistUnderTitle()
    {
        // Just under the always-shown width: the smart-insert action goes first.
        var justUnder = TrackListColumnLayout.Fit(TrackListColumnLayout.AlwaysShownWidth - 1, true, true, true);
        Assert.False(justUnder.InsertAction);
        Assert.True(justUnder.QueueAction);
        Assert.True(justUnder.ArtistColumn);

        // ~320 px — both side panels open on a 150%-scaled screen (the reported bug): only the
        // minimum layout is left and the artist moves under the title.
        var tiny = TrackListColumnLayout.Fit(320, true, true, true);
        Assert.Equal(new TrackListColumns(false, false, false, false, false, QueueAction: false, InsertAction: false, ArtistColumn: false), tiny);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(450)]
    [InlineData(600)]
    [InlineData(820)]
    [InlineData(1400)]
    public void Title_AlwaysKeepsItsMinimumWidth_WhenTheListCanHoldTheMinimumLayout(double width)
    {
        var c = TrackListColumnLayout.Fit(width, true, true, true);
        double fixedWidth = TrackListColumnLayout.ArtworkWidth + TrackListColumnLayout.BpmKeyWidth + TrackListColumnLayout.ScrollbarAllowance
            + (c.QueueAction ? TrackListColumnLayout.QueueWidth : 0) + (c.InsertAction ? TrackListColumnLayout.InsertWidth : 0)
            + (c.ArtistColumn ? TrackListColumnLayout.ArtistWidth : 0)
            + (c.Energy ? TrackListColumnLayout.EnergyWidth : 0) + (c.Format ? TrackListColumnLayout.FormatWidth : 0)
            + (c.Forensics ? TrackListColumnLayout.ForensicsWidth : 0) + (c.Duration ? TrackListColumnLayout.DurationWidth : 0)
            + (c.Rating ? TrackListColumnLayout.RatingWidth : 0);

        double titleWidth = width - fixedWidth;
        if (width >= TrackListColumnLayout.MinimumWidth)
            Assert.True(titleWidth >= TrackListColumnLayout.TitleMinWidth, $"Title got {titleWidth} px at {width} px");
        else
            Assert.False(c.ArtistColumn); // below the minimum everything optional is already gone
    }

    [Fact]
    public void AColumnYouHid_FreesRoomForTheOthers()
    {
        // With Forensics switched off by you, the same width now fits Rating and Duration too.
        double width = TrackListColumnLayout.AlwaysShownWidth + TrackListColumnLayout.EnergyWidth + TrackListColumnLayout.FormatWidth
            + TrackListColumnLayout.DurationWidth + TrackListColumnLayout.RatingWidth;
        Assert.Equal(new TrackListColumns(true, true, false, true, true), TrackListColumnLayout.Fit(width, true, false, true));
    }

    [Fact]
    public void UnknownWidth_ShowsWhatYouChose()
    {
        Assert.Equal(new TrackListColumns(true, true, true, true, true), TrackListColumnLayout.Fit(0, true, true, true));
    }

    [Fact]
    public void ContextPanel_KeepsItsSavedWidth_ButNeverTakesMoreThanAThirdOfTheWindow()
    {
        // The reported window: ~1413 logical px with a saved ~587 px panel → capped to 35%.
        Assert.Equal(1413 * MainViewModel.ContextPanelMaxWindowShare, MainViewModel.FitContextPanelWidth(587, 1413), 3);
        // A wide window gives the saved width back.
        Assert.Equal(587, MainViewModel.FitContextPanelWidth(587, 2560));
        // Never below the minimum, never above the absolute maximum, and unknown window = saved width.
        Assert.Equal(MainViewModel.MinContextPanelWidth, MainViewModel.FitContextPanelWidth(587, 600));
        Assert.Equal(MainViewModel.MaxContextPanelWidth, MainViewModel.FitContextPanelWidth(5000, 0));
        Assert.Equal(400, MainViewModel.FitContextPanelWidth(400, 0));
    }

    [Fact]
    public void PlaylistPanel_MaxWidth_IsAShareOfTheLibraryPage()
    {
        Assert.Equal(SLSKDONET.ViewModels.LibraryViewModel.NavPanelMinExpandedWidth, SLSKDONET.ViewModels.LibraryViewModel.FitNavPanelMaxWidth(700));
        Assert.Equal(1000 * SLSKDONET.ViewModels.LibraryViewModel.NavPanelMaxShare, SLSKDONET.ViewModels.LibraryViewModel.FitNavPanelMaxWidth(1000), 3);
        Assert.Equal(SLSKDONET.ViewModels.LibraryViewModel.NavPanelAbsoluteMaxWidth, SLSKDONET.ViewModels.LibraryViewModel.FitNavPanelMaxWidth(4000));
        Assert.Equal(SLSKDONET.ViewModels.LibraryViewModel.NavPanelAbsoluteMaxWidth, SLSKDONET.ViewModels.LibraryViewModel.FitNavPanelMaxWidth(0));
    }
}
