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

/// <summary>The on-screen toast stack's rules, kept out of MainViewModel so they can be tested.</summary>
public static class ToastStack
{
    /// <summary>
    /// Shows <paramref name="toast"/>. A visible toast with the same title and type is replaced in
    /// place instead of stacking a new one, so progress reports ("Exporting… [12/200]") update one
    /// toast rather than pushing every other message off screen. Oldest drop off past
    /// <paramref name="maxVisible"/>.
    /// </summary>
    public static void Show(System.Collections.Generic.IList<ToastNotificationViewModel> toasts, ToastNotificationViewModel toast, int maxVisible)
    {
        for (int i = 0; i < toasts.Count; i++)
        {
            if (toasts[i].Type == toast.Type && string.Equals(toasts[i].Title, toast.Title, StringComparison.Ordinal))
            {
                toasts[i] = toast;
                return;
            }
        }
        toasts.Add(toast);
        while (toasts.Count > maxVisible) toasts.RemoveAt(0);
    }
}
