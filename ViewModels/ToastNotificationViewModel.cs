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

    public ToastNotificationViewModel(string title, string message, NotificationType type)
    {
        Title = title;
        Message = message;
        Type = type;
    }
}
