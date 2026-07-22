using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// A node in the playlist sidebar tree: either a folder (which can nest other folders
/// and playlists) or a leaf wrapping a single playlist card.
/// </summary>
public abstract class PlaylistTreeNodeViewModel
{
    public abstract string DisplayName { get; }
    public abstract string Icon { get; }
    public virtual IEnumerable<PlaylistTreeNodeViewModel> Children => Enumerable.Empty<PlaylistTreeNodeViewModel>();
}

public sealed class PlaylistTreeFolderNodeViewModel : PlaylistTreeNodeViewModel
{
    public PlaylistTreeFolderNodeViewModel(PlaylistFolder folder)
    {
        Folder = folder;
    }

    public PlaylistFolder Folder { get; }
    public ObservableCollection<PlaylistTreeNodeViewModel> ChildNodes { get; } = new();

    public override string DisplayName => Folder.Name;
    public override string Icon => "📁";
    public override IEnumerable<PlaylistTreeNodeViewModel> Children => ChildNodes;
}

public sealed class PlaylistTreeCardNodeViewModel : PlaylistTreeNodeViewModel
{
    public PlaylistTreeCardNodeViewModel(LibraryPlaylistCardViewModel card)
    {
        Card = card;
    }

    public LibraryPlaylistCardViewModel Card { get; }

    public override string DisplayName => Card.Name;
    public override string Icon => "🎵";
}
