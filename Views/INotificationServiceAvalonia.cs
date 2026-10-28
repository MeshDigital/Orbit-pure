using System;

namespace SLSKDONET.Views;

/// <summary>
/// Avalonia-compatible notification service interface.
/// Implementations display toast/dialog notifications to the user.
/// </summary>
public interface INotificationService
{
    /// <param name="openUrl">When set, the toast becomes clickable and opens this URL (used by the
    /// update check to point at the matching GitHub release).</param>
    void Show(string title, string message, NotificationType type = NotificationType.Information, TimeSpan? duration = null, string? openUrl = null);
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
