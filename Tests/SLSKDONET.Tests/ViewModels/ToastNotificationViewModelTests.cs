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
}
