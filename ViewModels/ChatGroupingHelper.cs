using System;
using System.Collections.Generic;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Computes message-grouping and date-separator flags for a chat thread. Called after messages
/// are bulk-loaded or appended (see <see cref="UserProfileViewModel"/>/<see cref="RoomViewModel"/>)
/// rather than incrementally at insert time — a single O(n) pass over a ≤500-message thread is
/// simpler and less error-prone than maintaining running state across every call site.
/// </summary>
public static class ChatGroupingHelper
{
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(3);

    public static void Apply(IReadOnlyList<ChatMessageViewModel> messages)
    {
        ChatMessageViewModel? previous = null;
        foreach (var message in messages)
        {
            var isSameGroup = previous != null
                && previous.IsOutgoing == message.IsOutgoing
                && string.Equals(previous.SenderUsername, message.SenderUsername, StringComparison.OrdinalIgnoreCase)
                && (message.TimestampUtc - previous.TimestampUtc) <= GroupWindow
                && previous.TimestampUtc.ToLocalTime().Date == message.TimestampUtc.ToLocalTime().Date;

            message.ShowSenderHeader = !isSameGroup;

            var localDate = message.TimestampUtc.ToLocalTime().Date;
            var previousLocalDate = previous?.TimestampUtc.ToLocalTime().Date;
            if (previousLocalDate != localDate)
            {
                message.ShowDateSeparator = true;
                message.DateSeparatorText = FormatDateSeparator(localDate);
            }
            else
            {
                message.ShowDateSeparator = false;
            }

            previous = message;
        }
    }

    private static string FormatDateSeparator(DateTime localDate)
    {
        var today = DateTime.Now.Date;
        if (localDate == today)
            return "Today";
        if (localDate == today.AddDays(-1))
            return "Yesterday";
        return localDate.Year == today.Year
            ? localDate.ToString("ddd, MMM d")
            : localDate.ToString("ddd, MMM d, yyyy");
    }
}
