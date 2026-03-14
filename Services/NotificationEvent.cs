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

        public NotificationEvent(string title, string message, NotificationType type = NotificationType.Information, TimeSpan? duration = null)
        {
            Title = title;
            Message = message;
            Type = type;
            Duration = duration;
        }
    }
}
