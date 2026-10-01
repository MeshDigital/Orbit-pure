using SLSKDONET.ViewModels.Library;
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

        // Very narrow: only the always-shown columns remain.
        Assert.Equal(new TrackListColumns(false, false, false, false, false), TrackListColumnLayout.Fit(600, true, true, true));
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
}
