using System;
using SLSKDONET.Views;

namespace SLSKDONET.Services
{

    public class NotificationEvent
    {
        public string Title { get; }
        public string Message { get; }
        public NotificationType Type { get; }
        public TimeSpan? Duration { get; }

        /// <summary>Navigation key of the page a click on the toast opens (e.g. "Projects" = the
        /// Download Center), or null for a plain toast.</summary>
        public string? OpenPage { get; init; }

        public NotificationEvent(string title, string message, NotificationType type = NotificationType.Information, TimeSpan? duration = null)
        {
            Title = title;
            Message = message;
            Type = type;
            Duration = duration;
        }
    }
}
