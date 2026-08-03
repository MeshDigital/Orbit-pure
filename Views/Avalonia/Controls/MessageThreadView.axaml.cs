using System;
using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Shared chat message thread — a virtualized message list where <c>IsOutgoing</c> drives
/// left/right bubble alignment. Used by both 1:1 chat (UserProfileViewModel's Chat surface) and
/// room chat (RoomViewModel), since both reduce to the same (sender, message, timestamp,
/// isOutgoing) shape at the view level.
///
/// Owns auto-scroll behavior itself (rather than pushing it into the view models) since it's
/// purely a function of the ScrollViewer's live layout state: land on the newest message when a
/// thread first loads, follow new messages while the user is already near the bottom, surface a
/// "new messages" pill instead of yanking the scroll position if they've scrolled up to read
/// history, and preserve the visual scroll position (rather than jumping) when older messages are
/// prepended above the viewport.
/// </summary>
public partial class MessageThreadView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> MessagesProperty =
        AvaloniaProperty.Register<MessageThreadView, IEnumerable?>(nameof(Messages));

    public static readonly StyledProperty<ICommand?> LoadOlderMessagesCommandProperty =
        AvaloniaProperty.Register<MessageThreadView, ICommand?>(nameof(LoadOlderMessagesCommand));

    public static readonly StyledProperty<bool> IsLoadingOlderMessagesProperty =
        AvaloniaProperty.Register<MessageThreadView, bool>(nameof(IsLoadingOlderMessages));

    public static readonly StyledProperty<bool> HasMoreHistoryProperty =
        AvaloniaProperty.Register<MessageThreadView, bool>(nameof(HasMoreHistory));

    public static readonly StyledProperty<bool> HasNewMessagesBelowProperty =
        AvaloniaProperty.Register<MessageThreadView, bool>(nameof(HasNewMessagesBelow));

    public static readonly StyledProperty<ICommand?> DeleteMessageCommandProperty =
        AvaloniaProperty.Register<MessageThreadView, ICommand?>(nameof(DeleteMessageCommand));

    public IEnumerable? Messages
    {
        get => GetValue(MessagesProperty);
        set => SetValue(MessagesProperty, value);
    }

    public ICommand? LoadOlderMessagesCommand
    {
        get => GetValue(LoadOlderMessagesCommandProperty);
        set => SetValue(LoadOlderMessagesCommandProperty, value);
    }

    public bool IsLoadingOlderMessages
    {
        get => GetValue(IsLoadingOlderMessagesProperty);
        set => SetValue(IsLoadingOlderMessagesProperty, value);
    }

    public bool HasMoreHistory
    {
        get => GetValue(HasMoreHistoryProperty);
        set => SetValue(HasMoreHistoryProperty, value);
    }

    /// <summary>True once the user has scrolled away from the bottom while a new message arrives — drives the "↓ New messages" pill.</summary>
    public bool HasNewMessagesBelow
    {
        get => GetValue(HasNewMessagesBelowProperty);
        private set => SetValue(HasNewMessagesBelowProperty, value);
    }

    /// <summary>Bound directly in the item template (Command="{Binding #Root.DeleteMessageCommand}" CommandParameter="{Binding}") — removes just that one message.</summary>
    public ICommand? DeleteMessageCommand
    {
        get => GetValue(DeleteMessageCommandProperty);
        set => SetValue(DeleteMessageCommandProperty, value);
    }

    private const double NearBottomThreshold = 120;

    private bool _pendingScrollToEnd;
    private bool _pendingOlderMessagesAdjustment;
    private double _extentBeforeOlderLoad;
    private double _offsetBeforeOlderLoad;

    public MessageThreadView()
    {
        InitializeComponent();
        MessageScroll.LayoutUpdated += OnScrollViewerLayoutUpdated;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MessagesProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCollection)
                oldCollection.CollectionChanged -= OnMessagesCollectionChanged;
            if (change.NewValue is INotifyCollectionChanged newCollection)
                newCollection.CollectionChanged += OnMessagesCollectionChanged;

            // A freshly-bound thread (profile/room switch) should land on the newest message.
            _pendingScrollToEnd = true;
            HasNewMessagesBelow = false;
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _pendingScrollToEnd = true;
            return;
        }

        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        var isPrepend = e.NewStartingIndex == 0 && Messages is ICollection { Count: > 1 };
        if (isPrepend)
        {
            // Handled by OnLoadOlderClick's before/after extent snapshot instead — a prepend
            // should preserve the reader's visual position, not scroll at all.
            return;
        }

        var extent = MessageScroll.Extent.Height;
        var viewport = MessageScroll.Viewport.Height;
        var offset = MessageScroll.Offset.Y;
        var distanceFromBottom = extent - viewport - offset;

        if (distanceFromBottom <= NearBottomThreshold)
            _pendingScrollToEnd = true;
        else
            HasNewMessagesBelow = true;
    }

    private void OnScrollViewerLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingOlderMessagesAdjustment)
        {
            var delta = MessageScroll.Extent.Height - _extentBeforeOlderLoad;
            if (delta > 0.5)
            {
                // Clear the flag before mutating Offset — setting it can synchronously
                // re-trigger LayoutUpdated, and a re-entrant call must see this as already handled.
                _pendingOlderMessagesAdjustment = false;
                MessageScroll.Offset = new Vector(MessageScroll.Offset.X, _offsetBeforeOlderLoad + delta);
            }
            return;
        }

        if (_pendingScrollToEnd)
        {
            // Same reentrancy concern: clear before ScrollToEnd(), not after.
            _pendingScrollToEnd = false;
            MessageScroll.ScrollToEnd();
            HasNewMessagesBelow = false;
        }
    }

    private void OnLoadOlderClick(object? sender, RoutedEventArgs e)
    {
        _extentBeforeOlderLoad = MessageScroll.Extent.Height;
        _offsetBeforeOlderLoad = MessageScroll.Offset.Y;
        _pendingOlderMessagesAdjustment = true;
    }

    private void OnJumpToBottomClick(object? sender, RoutedEventArgs e)
    {
        _pendingScrollToEnd = true;
        MessageScroll.ScrollToEnd();
        HasNewMessagesBelow = false;
    }

    private async void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChatMessageViewModel message })
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(message.Message);
    }
}
