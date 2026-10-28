using System;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Display data for a single on-screen toast, rendered by the toast host in MainWindow.axaml.
/// </summary>
public class ToastNotificationViewModel
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public string Message { get; }
    public NotificationType Type { get; }

    /// <summary>Page a click opens (navigation key), or null.</summary>
    public string? OpenPage { get; }

    /// <summary>External URL a click opens (e.g. the matching GitHub release), or null.</summary>
    public string? OpenUrl { get; }

    public bool IsClickable => OpenPage != null || !string.IsNullOrWhiteSpace(OpenUrl);

    public string ClickHint => !string.IsNullOrWhiteSpace(OpenUrl)
        ? "Click to open the release page →"
        : OpenPage == "Projects"
            ? "Click to open the Download Center →"
            : "Click to open →";

    public ToastNotificationViewModel(
        string title,
        string message,
        NotificationType type,
        string? openPage = null,
        string? openUrl = null)
    {
        Title = title;
        Message = message;
        Type = type;
        OpenPage = openPage;
        OpenUrl = openUrl;
    }
}
