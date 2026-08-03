using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SLSKDONET.Views.Avalonia.Controls;

public enum RemoveTrackChoice
{
    Cancel,
    RemoveFromPlaylist,
    DeletePermanently,
}

public partial class RemoveTrackDialog : Window
{
    public RemoveTrackChoice Result { get; private set; } = RemoveTrackChoice.Cancel;

    public RemoveTrackDialog()
    {
        InitializeComponent();

        var removeFromPlaylistBtn = this.FindControl<Button>("RemoveFromPlaylistButton");
        var deletePermanentlyBtn = this.FindControl<Button>("DeletePermanentlyButton");
        var cancelBtn = this.FindControl<Button>("CancelButton");

        if (removeFromPlaylistBtn != null) removeFromPlaylistBtn.Click += RemoveFromPlaylistButton_Click;
        if (deletePermanentlyBtn != null) deletePermanentlyBtn.Click += DeletePermanentlyButton_Click;
        if (cancelBtn != null) cancelBtn.Click += CancelButton_Click;
    }

    /// <param name="trackLabel">Display name of the track (or count, for future multi-select).</param>
    /// <param name="canRemoveFromPlaylist">False when there's no specific playlist selected (e.g.
    /// viewing All Tracks) — in that case "remove from playlist" is meaningless, so the whole
    /// option is hidden and only Delete/Cancel remain.</param>
    /// <param name="playlistName">Name of the playlist the track would be removed from.</param>
    public RemoveTrackDialog(string trackLabel, bool canRemoveFromPlaylist, string? playlistName) : this()
    {
        var titleText = this.FindControl<TextBlock>("TitleText");
        var messageText = this.FindControl<TextBlock>("MessageText");
        var removeFromPlaylistBtn = this.FindControl<Button>("RemoveFromPlaylistButton");
        var removeFromPlaylistTitle = this.FindControl<TextBlock>("RemoveFromPlaylistTitle");

        if (titleText != null) titleText.Text = $"Remove \"{trackLabel}\"";
        if (messageText != null) messageText.Text = "What do you want to do with this track?";

        if (!canRemoveFromPlaylist && removeFromPlaylistBtn != null)
        {
            removeFromPlaylistBtn.IsVisible = false;
        }
        else if (removeFromPlaylistTitle != null && !string.IsNullOrWhiteSpace(playlistName))
        {
            removeFromPlaylistTitle.Text = $"Remove from \"{playlistName}\"";
        }
    }

    private void RemoveFromPlaylistButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = RemoveTrackChoice.RemoveFromPlaylist;
        Close();
    }

    private void DeletePermanentlyButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = RemoveTrackChoice.DeletePermanently;
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = RemoveTrackChoice.Cancel;
        Close();
    }
}
