using SLSKDONET.ViewModels;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class ToastNotificationViewModelTests
{
    [Fact]
    public void OpenUrl_MakesTheToastClickable()
    {
        var toast = new ToastNotificationViewModel(
            "Update available",
            "ORBIT 0.3.0 is available.",
            NotificationType.Information,
            openUrl: "https://github.com/MeshDigital/Orbit-pure/releases/tag/v0.3.0");

        Assert.True(toast.IsClickable);
        Assert.Contains("release page", toast.ClickHint);
        Assert.Null(toast.OpenPage);
    }

    [Fact]
    public void OpenPage_StillUsesTheInAppHint()
    {
        var toast = new ToastNotificationViewModel(
            "Queued",
            "Track queued for download.",
            NotificationType.Success,
            openPage: "Projects");

        Assert.True(toast.IsClickable);
        Assert.Contains("Download Center", toast.ClickHint);
        Assert.Null(toast.OpenUrl);
    }

    [Fact]
    public void PlainToast_IsNotClickable()
    {
        var toast = new ToastNotificationViewModel("Saved", "Settings saved.", NotificationType.Success);

        Assert.False(toast.IsClickable);
    }

    [Fact]
    public void ToastStack_ProgressWithTheSameTitle_UpdatesOneToast_InsteadOfFloodingTheStack()
    {
        var toasts = new System.Collections.Generic.List<ToastNotificationViewModel>
        {
            new("Library scan finished", "Done.", NotificationType.Success),
        };

        for (int i = 1; i <= 200; i++)
            ToastStack.Show(toasts, new ToastNotificationViewModel("Exporting…", $"[{i}/200] track{i}.flac", NotificationType.Information), 4);

        Assert.Equal(2, toasts.Count);
        Assert.Equal("Library scan finished", toasts[0].Title); // the earlier message is not pushed off
        Assert.Equal("[200/200] track200.flac", toasts[1].Message);
    }

    [Fact]
    public void ToastStack_DifferentTitlesOrTypes_Stack_AndTheOldestDropsOffPastTheCap()
    {
        var toasts = new System.Collections.Generic.List<ToastNotificationViewModel>();
        ToastStack.Show(toasts, new("Export", "a", NotificationType.Information), 2);
        ToastStack.Show(toasts, new("Export", "b", NotificationType.Error), 2);
        ToastStack.Show(toasts, new("Saved", "c", NotificationType.Success), 2);

        Assert.Equal(new[] { "b", "c" }, toasts.ConvertAll(t => t.Message));
    }
}
