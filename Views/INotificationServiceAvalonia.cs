using System;

namespace SLSKDONET.Views;

/// <summary>
/// Avalonia-compatible notification service interface.
/// Implementations display toast/dialog notifications to the user.
/// </summary>
public interface INotificationService
{
    void Show(string title, string message, NotificationType type = NotificationType.Information, TimeSpan? duration = null);
}

/// <summary>
/// Notification type enumeration.
/// </summary>
public enum NotificationType
{
    Information = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}
