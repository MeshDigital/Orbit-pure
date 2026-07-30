using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Shared chat message thread — a virtualized message list where <c>IsOutgoing</c> drives
/// left/right bubble alignment. Used by both 1:1 chat (UserProfileViewModel's Chat tab) and
/// room chat (RoomViewModel), since both reduce to the same (sender, message, timestamp,
/// isOutgoing) shape at the view level.
/// </summary>
public partial class MessageThreadView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> MessagesProperty =
        AvaloniaProperty.Register<MessageThreadView, IEnumerable?>(nameof(Messages));

    public IEnumerable? Messages
    {
        get => GetValue(MessagesProperty);
        set => SetValue(MessagesProperty, value);
    }

    public MessageThreadView()
    {
        InitializeComponent();
    }
}
