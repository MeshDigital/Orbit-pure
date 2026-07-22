using System;
using Microsoft.Extensions.Logging;
using SLSKDONET.Views;

namespace SLSKDONET.Services;

/// <summary>
/// Published whenever <see cref="NotificationServiceAdapter.Show"/> is called, so the UI layer
/// (a toast host bound in MainViewModel) can render an actual on-screen notification.
/// </summary>
public record ToastRequestedEvent(string Title, string Message, NotificationType Type, TimeSpan? Duration);

/// <summary>
/// Adapter that implements the view-level INotificationService for Avalonia.
/// Publishes a <see cref="ToastRequestedEvent"/> for the UI to render, and always logs too.
/// </summary>
public class NotificationServiceAdapter : global::SLSKDONET.Views.INotificationService
{
    private readonly ILogger<NotificationServiceAdapter> _logger;
    private readonly IEventBus _eventBus;

    public NotificationServiceAdapter(ILogger<NotificationServiceAdapter> logger, IEventBus eventBus)
    {
        _logger = logger;
        _eventBus = eventBus;
    }

    public void Show(string title, string message, NotificationType type = NotificationType.Information, TimeSpan? duration = null)
    {
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

        _eventBus.Publish(new ToastRequestedEvent(title, message, type, duration));
    }
}
