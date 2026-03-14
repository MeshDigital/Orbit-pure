using System;
using Microsoft.Extensions.Logging;
using System.Reflection;
using SLSKDONET.Views;

namespace SLSKDONET.Services;

/// <summary>
/// Adapter that implements the view-level INotificationService for Avalonia.
/// Falls back to logging when no UI notification service is available.
/// </summary>
/// </summary>
public class NotificationServiceAdapter : global::SLSKDONET.Views.INotificationService
{
    private readonly ILogger<NotificationServiceAdapter> _logger;

    public NotificationServiceAdapter(ILogger<NotificationServiceAdapter> logger)
    {
        _logger = logger;
    }

    public void Show(string title, string message, NotificationType type = NotificationType.Information, TimeSpan? duration = null)
    {
        // Log the notification (Avalonia doesn't have built-in toast notifications like WPF)
        var logMessage = $"{type}: {title} - {message}";
        
        switch (type)
        {
            case NotificationType.Error:
                _logger.LogError(logMessage);
                break;
            case NotificationType.Warning:
                _logger.LogWarning(logMessage);
                break;
            case NotificationType.Success:
            case NotificationType.Information:
            default:
                _logger.LogInformation(logMessage);
                break;
        }
        
        // TODO: Implement proper Avalonia toast notifications here
        // Can use third-party libraries like Notification.Avalonia or custom toast windows
    }
}
