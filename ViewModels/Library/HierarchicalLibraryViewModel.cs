using System.Linq;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views; // For RelayCommand and AsyncRelayCommand

namespace SLSKDONET.ViewModels.Library;

public class HierarchicalLibraryViewModel
{
    private readonly ObservableCollection<ILibraryNode> _albums = new();
    private readonly AppConfig _config;
    private readonly DownloadManager? _downloadManager;
    private readonly Dictionary<IColumn<ILibraryNode>, string> _columnToIdMap = new();
    private CancellationTokenSource? _saveDebounceCts;
    public HierarchicalTreeDataGridSource<ILibraryNode> Source { get; }
    public ITreeDataGridRowSelectionModel<ILibraryNode>? Selection => Source.RowSelection;

    private readonly ArtworkCacheService? _artworkCacheService;

    public HierarchicalLibraryViewModel(AppConfig config, DownloadManager? downloadManager = null, ArtworkCacheService? artworkCacheService = null)
    {
        _config = config;
        _downloadManager = downloadManager;
        _artworkCacheService = artworkCacheService;
        Source = new HierarchicalTreeDataGridSource<ILibraryNode>(_albums);
        Source.RowSelection!.SingleSelect = false;

        InitializeColumns();

        // Persist reordering when it happens (with debounce)
        Source.Columns.CollectionChanged += (s, e) => ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts = new CancellationTokenSource();
        var token = _saveDebounceCts.Token;

        Task.Delay(500, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                SaveColumnLayout();
            }
        }, TaskScheduler.Default);
    }

    private void InitializeColumns()
    {
        var columns = new List<(string Id, IColumn<ILibraryNode> Column)>
        {
            ("Art", new TemplateColumn<ILibraryNode>("🎨", CreateArtTemplate(), width: new GridLength(40))),
            ("Status", new TemplateColumn<ILibraryNode>("Status", CreateStatusTemplate(), width: new GridLength(100))),
            ("Metadata", new TemplateColumn<ILibraryNode>(" ✨", CreateMetadataTemplate(), width: new GridLength(40))),
            ("Title", new HierarchicalExpanderColumn<ILibraryNode>(
                new TextColumn<ILibraryNode, string>("Title", x => x.Title ?? "Unknown"),
                x => x is AlbumNode album ? album.Tracks : null)),
            ("Number", new TextColumn<ILibraryNode, int>("#", x => x.SortOrder, width: new GridLength(30))),
            ("Artist", new TextColumn<ILibraryNode, string>("Artist", x => x.Artist ?? string.Empty, width: new GridLength(1, GridUnitType.Star))),
            ("Album", new TextColumn<ILibraryNode, string>("Album", x => x.Album ?? string.Empty, width: new GridLength(1, GridUnitType.Star))),
            ("Duration", new TextColumn<ILibraryNode, string>("Duration", x => x.Duration ?? string.Empty, width: new GridLength(70))),
            ("Released", new TextColumn<ILibraryNode, string>("Released", x => x.ReleaseYear ?? "", width: new GridLength(60))),
            ("Popularity", new TextColumn<ILibraryNode, int>("🔥", x => x.Popularity, width: new GridLength(40))),
            ("Bitrate", new TextColumn<ILibraryNode, string>("Bitrate", x => x.Bitrate ?? string.Empty, width: new GridLength(60))),
            ("Genres", new TextColumn<ILibraryNode, string>("Genres", x => x.Genres ?? string.Empty, width: new GridLength(100))),
            ("Added", new TextColumn<ILibraryNode, DateTime>(
                "Added",
                x => x.AddedAt,
                width: new GridLength(90),
                options: new TextColumnOptions<ILibraryNode> { StringFormat = "{0:d}" })),
            ("Actions", new TemplateColumn<ILibraryNode>("Actions", CreateActionsTemplate(), width: new GridLength(120)))
        };

        // Apply saved order if available
        var savedOrder = _config.LibraryColumnOrder?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var orderedColumns = new List<IColumn<ILibraryNode>>();

        if (savedOrder != null && savedOrder.Length > 0)
        {
            foreach (var id in savedOrder)
            {
                var match = columns.FirstOrDefault(c => c.Id == id);
                if (match.Column != null)
                {
                    orderedColumns.Add(match.Column);
                    _columnToIdMap[match.Column] = match.Id;
                    columns.Remove(match);
                }
            }
        }
        
        // Add remaining columns (either defaults or ones not in saved set)
        foreach (var remaining in columns)
        {
            orderedColumns.Add(remaining.Column);
            _columnToIdMap[remaining.Column] = remaining.Id;
        }

        Source.Columns.AddRange(orderedColumns);
    }

    public void SaveColumnLayout()
    {
        var ids = Source.Columns.Cast<IColumn<ILibraryNode>>()
                               .Select(c => _columnToIdMap.TryGetValue(c, out var id) ? id : null)
                               .Where(id => id != null);
        
        _config.LibraryColumnOrder = string.Join(",", ids);
        // In a real app we'd also save widths here if we could extract them easily from Source.Columns
    }

    private IDataTemplate CreateArtTemplate() => new FuncDataTemplate<object>((item, _) => 
    {
        if (item is not ILibraryNode node) return new Panel();

        return new Border { 
            Width = 32, Height = 32, CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Background = Brush.Parse("#2D2D2D"),
            Margin = new Thickness(4),
            Child = new Image { 
                [!Image.SourceProperty] = new Binding("Artwork.Image"),
                Stretch = Stretch.UniformToFill
            }
        };
    }, false);

    private IDataTemplate CreateStatusTemplate() => new FuncDataTemplate<object>((item, _) => 
    {
        if (item is not PlaylistTrackViewModel track) return new Panel();

        var border = new Border {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3)
        };

        var textBlock = new TextBlock { 
            FontSize = 10,
            Foreground = Brushes.White
        };

        border.Bind(Border.BackgroundProperty, new Binding(nameof(PlaylistTrackViewModel.StatusColor)) { Converter = new FuncValueConverter<string, IBrush>(c => Brush.Parse(c ?? "#333333")) });
        textBlock.Bind(TextBlock.TextProperty, new Binding(nameof(PlaylistTrackViewModel.StatusText)));

        border.Child = textBlock;
        return border;
    }, false);

    private IDataTemplate CreateMetadataTemplate() => new FuncDataTemplate<object>((item, _) => 
    {
        if (item is not PlaylistTrackViewModel track) return new Panel();

        var textBlock = new TextBlock { 
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 14
        };

        textBlock.Bind(TextBlock.TextProperty, new Binding(nameof(PlaylistTrackViewModel.MetadataStatusSymbol)));
        textBlock.Bind(TextBlock.ForegroundProperty, new Binding(nameof(PlaylistTrackViewModel.MetadataStatusColor)) { Converter = new FuncValueConverter<string, IBrush>(c => Brush.Parse(c ?? "#FFFFFF")) });
        textBlock.Bind(ToolTip.TipProperty, new Binding(nameof(PlaylistTrackViewModel.MetadataStatus)));

        return textBlock;
    }, false);

    private IDataTemplate CreateActionsTemplate() => new FuncDataTemplate<object>((item, _) => 
    {
        if (item is not PlaylistTrackViewModel track) return new Panel();

        var panel = new StackPanel { 
            Orientation = Orientation.Horizontal, 
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var searchBtn = new Button {
            Content = "🔍",
            Command = track.FindNewVersionCommand,
            Padding = new Thickness(6, 2),
            FontSize = 11
        };
        ToolTip.SetTip(searchBtn, "Search for this track");
        searchBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PlaylistTrackViewModel.State))
        {
            Converter = new FuncValueConverter<PlaylistTrackState, bool>(s => 
                s == PlaylistTrackState.Pending || s == PlaylistTrackState.Failed)
        });
        panel.Children.Add(searchBtn);

        var pauseBtn = new Button {
            Content = "⏸",
            Command = track.PauseCommand,
            Padding = new Thickness(6, 2),
            FontSize = 11
        };
        ToolTip.SetTip(pauseBtn, "Pause download");
        pauseBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PlaylistTrackViewModel.CanPause)));
        panel.Children.Add(pauseBtn);

        var resumeBtn = new Button {
            Content = "▶",
            Command = track.ResumeCommand,
            Padding = new Thickness(6, 2),
            FontSize = 11
        };
        ToolTip.SetTip(resumeBtn, "Resume download");
        resumeBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PlaylistTrackViewModel.CanResume)));
        panel.Children.Add(resumeBtn);

        var cancelBtn = new Button {
            Content = "✕",
            Command = track.CancelCommand,
            Padding = new Thickness(6, 2),
            FontSize = 11,
            Foreground = Brush.Parse("#F44336")
        };
        ToolTip.SetTip(cancelBtn, "Cancel");
        cancelBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PlaylistTrackViewModel.CanCancel)));
        panel.Children.Add(cancelBtn);

        return panel;
    }, false);

    public void UpdateTracks(IEnumerable<PlaylistTrackViewModel> tracks)
    {
        _albums.Clear();
        var grouped = tracks.GroupBy(t => new { AlbumTitle = t.Model.Album ?? "Unknown Album", t.Artist });
        
        foreach (var g in grouped)
        {
            if (g.Count() == 1)
            {
                // Single track? Don't group it. Show as flat item.
                _albums.Add(g.First());
            }
            else
            {
                var firstTrack = g.First();
                var albumNode = new AlbumNode(g.Key.AlbumTitle, g.Key.Artist, _downloadManager, _artworkCacheService)
                {
                    AlbumArtPath = firstTrack.AlbumArtPath
                };
                foreach (var track in g)
                {
                    albumNode.Tracks.Add(track);
                }
                _albums.Add(albumNode);
            }
        }

        // Auto-expand all albums by default
        for (int i = 0; i < _albums.Count; i++)
        {
            Source.Expand(new IndexPath(i));
        }
    }
}
