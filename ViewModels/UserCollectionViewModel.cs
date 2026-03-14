using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

public class UserCollectionViewModel : ReactiveObject
{
    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "flac", "wav", "m4a", "aac", "ogg", "opus", "aif", "aiff", "ape", "alac", "wma"
    };

    private readonly List<Track> _allShares = new();

    private readonly ILogger<UserCollectionViewModel> _logger;
    private readonly ISoulseekAdapter _soulseek;
    private readonly DownloadManager _downloadManager;

    public ObservableCollection<UserCollectionNodeViewModel> RootNodes { get; } = new();

    private string _currentUsername = string.Empty;
    public string CurrentUsername
    {
        get => _currentUsername;
        private set => this.RaiseAndSetIfChanged(ref _currentUsername, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            this.RaiseAndSetIfChanged(ref _filterText, value);
            ApplyCurrentView();
            this.RaisePropertyChanged(nameof(HasActiveFilter));
            this.RaisePropertyChanged(nameof(FilterSummary));
        }
    }

    private bool _showMusicOnly;
    public bool ShowMusicOnly
    {
        get => _showMusicOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _showMusicOnly, value);
            ApplyCurrentView();
            this.RaisePropertyChanged(nameof(HasActiveFilter));
            this.RaisePropertyChanged(nameof(FilterSummary));
        }
    }

    private string _selectedSortOption = "Name";
    public string SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Name" : value;
            this.RaiseAndSetIfChanged(ref _selectedSortOption, normalized);
            ApplyCurrentView();
            this.RaisePropertyChanged(nameof(HasActiveFilter));
            this.RaisePropertyChanged(nameof(FilterSummary));
        }
    }

    public IReadOnlyList<string> SortOptions { get; } = new[] { "Name", "Format", "Bitrate" };

    private UserCollectionNodeViewModel? _selectedNode;
    public UserCollectionNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedNode, value);
            this.RaisePropertyChanged(nameof(SelectedNodeTitle));
            this.RaisePropertyChanged(nameof(SelectedNodeSubtitle));
            this.RaisePropertyChanged(nameof(SelectedNodeWarning));
            this.RaisePropertyChanged(nameof(HasSelectedNodeWarning));
        }
    }

    public string SelectedNodeTitle => SelectedNode?.DisplayName ?? "No selection";
    public string SelectedNodeSubtitle => SelectedNode?.SecondaryText ?? "Select a folder or track to queue it.";
    public string SelectedNodeWarning => SelectedNode?.WarningText ?? string.Empty;
    public bool HasSelectedNodeWarning => SelectedNode?.HasWarning == true;
    public bool HasVisibleNodes => RootNodes.Count > 0;
    public bool HasActiveFilter => ShowMusicOnly || !string.IsNullOrWhiteSpace(FilterText) || !string.Equals(SelectedSortOption, "Name", StringComparison.OrdinalIgnoreCase);
    public string FilterSummary
    {
        get
        {
            var parts = new List<string>();
            if (ShowMusicOnly) parts.Add("music only");
            if (!string.IsNullOrWhiteSpace(FilterText)) parts.Add($"filter: {FilterText.Trim()}");
            if (!string.Equals(SelectedSortOption, "Name", StringComparison.OrdinalIgnoreCase)) parts.Add($"sort: {SelectedSortOption.ToLowerInvariant()}");
            return parts.Count == 0 ? "Showing all visible shares" : string.Join(" • ", parts);
        }
    }

    public int TotalFiles => RootNodes.SelectMany(n => n.EnumerateAllFiles()).Count();
    public int MusicFileCount => RootNodes.SelectMany(n => n.EnumerateFiles()).Count(f => f.IsMusicFile);
    public int SuspiciousLosslessCount => RootNodes.SelectMany(n => n.EnumerateAllFiles()).Count(f => f.IsSuspiciousLossless);
    public bool HasSuspiciousLossless => SuspiciousLosslessCount > 0;

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> QueueSelectedCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> QueueAllCommand { get; }

    public UserCollectionViewModel(
        ILogger<UserCollectionViewModel> logger,
        ISoulseekAdapter soulseek,
        DownloadManager downloadManager)
    {
        _logger = logger;
        _soulseek = soulseek;
        _downloadManager = downloadManager;

        var canQueueSelected = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node?.QueueableFileCount > 0);

        var canQueueAny = this.WhenAnyValue(x => x.MusicFileCount)
            .Select(count => count > 0);

        QueueSelectedCommand = ReactiveCommand.CreateFromTask(QueueSelectedAsync, canQueueSelected);
        QueueAllCommand = ReactiveCommand.CreateFromTask(QueueAllAsync, canQueueAny);
    }

    public async Task LoadUserAsync(string username)
    {
        CurrentUsername = username;
        SelectedNode = null;
        _allShares.Clear();
        RootNodes.Clear();
        NotifyTreeChanged();

        if (string.IsNullOrWhiteSpace(username))
        {
            StatusText = "Missing username.";
            return;
        }

        IsLoading = true;
        StatusText = $"Loading {username}'s collection...";

        try
        {
            var shares = (await _soulseek.GetUserSharesAsync(username)).ToList();
            _allShares.AddRange(shares);

            if (!shares.Any())
            {
                StatusText = $"No visible shares for {username}.";
                return;
            }

            ApplyCurrentView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load user collection for {Username}", username);
            StatusText = $"Failed to load {username}'s collection: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyCurrentView()
    {
        RootNodes.Clear();

        IEnumerable<Track> filteredShares = _allShares;

        if (ShowMusicOnly)
        {
            filteredShares = filteredShares.Where(IsMusicExtension);
        }

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var filter = FilterText.Trim();
            filteredShares = filteredShares.Where(track => MatchesFilter(track, filter));
        }

        var materializedShares = filteredShares.ToList();
        if (materializedShares.Count > 0)
        {
            BuildTree(materializedShares);
            SortNodes(RootNodes, SelectedSortOption);
        }

        NotifyTreeChanged();

        if (_allShares.Count == 0)
        {
            StatusText = string.IsNullOrWhiteSpace(CurrentUsername)
                ? "Ready"
                : $"No visible shares for {CurrentUsername}.";
            return;
        }

        if (materializedShares.Count == 0)
        {
            StatusText = "No items match the current browser filters.";
            return;
        }

        StatusText = $"Showing {TotalFiles} visible file(s), {MusicFileCount} music file(s), {SuspiciousLosslessCount} suspicious lossless.";
    }

    private static bool MatchesFilter(Track track, string filter)
    {
        return (track.Filename?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
            || (track.Artist?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
            || (track.Title?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
            || (track.Album?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
            || (track.Username?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void BuildTree(IEnumerable<Track> shares)
    {
        foreach (var share in shares)
        {
            var segments = (share.Filename ?? string.Empty)
                .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0)
            {
                continue;
            }

            var nodes = RootNodes;
            UserCollectionFolderNodeViewModel? currentFolder = null;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                currentFolder = GetOrCreateFolder(nodes, segments[i]);
                nodes = currentFolder.ChildNodes;
            }

            var fileNode = new UserCollectionFileNodeViewModel(share, segments[^1], IsMusicExtension(share));
            if (currentFolder == null)
            {
                RootNodes.Add(fileNode);
            }
            else
            {
                currentFolder.ChildNodes.Add(fileNode);
            }
        }
    }

    private UserCollectionFolderNodeViewModel GetOrCreateFolder(ObservableCollection<UserCollectionNodeViewModel> nodes, string name)
    {
        var existing = nodes.OfType<UserCollectionFolderNodeViewModel>()
            .FirstOrDefault(folder => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            return existing;
        }

        var created = new UserCollectionFolderNodeViewModel(name);
        nodes.Add(created);
        return created;
    }

    private static void SortNodes(ObservableCollection<UserCollectionNodeViewModel> nodes, string sortOption)
    {
        var ordered = nodes
            .OrderByDescending(n => n is UserCollectionFolderNodeViewModel)
            .ThenBy(n => GetSortGroup(n, sortOption), StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(n => GetSortBitrate(n, sortOption))
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        nodes.Clear();
        foreach (var node in ordered)
        {
            if (node is UserCollectionFolderNodeViewModel folder)
            {
                SortNodes(folder.ChildNodes, sortOption);
            }

            nodes.Add(node);
        }
    }

    private static string GetSortGroup(UserCollectionNodeViewModel node, string sortOption)
    {
        if (node is not UserCollectionFileNodeViewModel file)
        {
            return string.Empty;
        }

        return sortOption switch
        {
            "Format" => file.Extension,
            _ => file.DisplayName
        };
    }

    private static int GetSortBitrate(UserCollectionNodeViewModel node, string sortOption)
    {
        if (!string.Equals(sortOption, "Bitrate", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return node is UserCollectionFileNodeViewModel file ? file.Bitrate : 0;
    }

    private async Task QueueSelectedAsync()
    {
        if (SelectedNode == null)
        {
            return;
        }

        await QueueFilesAsync(SelectedNode.EnumerateFiles().Where(f => f.IsMusicFile));
    }

    private async Task QueueAllAsync()
    {
        await QueueFilesAsync(RootNodes.SelectMany(n => n.EnumerateFiles()).Where(f => f.IsMusicFile));
    }

    private Task QueueFilesAsync(IEnumerable<UserCollectionFileNodeViewModel> files)
    {
        var queued = 0;
        var attempted = 0;

        foreach (var file in files)
        {
            attempted++;
            if (_downloadManager.EnqueueTrack(file.Track))
            {
                queued++;
            }
        }

        StatusText = queued == 0
            ? "No files were queued. Existing filters or duplicates blocked the selection."
            : $"Queued {queued} of {attempted} selected file(s).";

        return Task.CompletedTask;
    }

    private static bool IsMusicExtension(Track track)
    {
        var extension = (track.Format ?? track.GetExtension())?.Trim().TrimStart('.');
        return !string.IsNullOrWhiteSpace(extension) && MusicExtensions.Contains(extension);
    }

    private void NotifyTreeChanged()
    {
        this.RaisePropertyChanged(nameof(HasVisibleNodes));
        this.RaisePropertyChanged(nameof(FilterSummary));
        this.RaisePropertyChanged(nameof(TotalFiles));
        this.RaisePropertyChanged(nameof(MusicFileCount));
        this.RaisePropertyChanged(nameof(SuspiciousLosslessCount));
        this.RaisePropertyChanged(nameof(HasSuspiciousLossless));
    }
}

public abstract class UserCollectionNodeViewModel : ReactiveObject
{
    protected UserCollectionNodeViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public virtual string DisplayName => Name;
    public virtual string Icon => "📄";
    public virtual string SecondaryText => string.Empty;
    public virtual string WarningText => string.Empty;
    public virtual bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);
    public virtual IEnumerable<UserCollectionNodeViewModel> Children => Enumerable.Empty<UserCollectionNodeViewModel>();
    public virtual IEnumerable<UserCollectionFileNodeViewModel> EnumerateAllFiles() => Enumerable.Empty<UserCollectionFileNodeViewModel>();
    public virtual IEnumerable<UserCollectionFileNodeViewModel> EnumerateFiles() => Enumerable.Empty<UserCollectionFileNodeViewModel>();
    public int QueueableFileCount => EnumerateFiles().Count(f => f.IsMusicFile);
}

public sealed class UserCollectionFolderNodeViewModel : UserCollectionNodeViewModel
{
    public UserCollectionFolderNodeViewModel(string name) : base(name)
    {
    }

    public ObservableCollection<UserCollectionNodeViewModel> ChildNodes { get; } = new();
    public override IEnumerable<UserCollectionNodeViewModel> Children => ChildNodes;
    public override string Icon => "📁";
    public override string SecondaryText => QueueableFileCount > 0 ? $"{QueueableFileCount} track(s)" : "Folder";

    public override IEnumerable<UserCollectionFileNodeViewModel> EnumerateAllFiles()
        => ChildNodes.SelectMany(child => child.EnumerateAllFiles());

    public override IEnumerable<UserCollectionFileNodeViewModel> EnumerateFiles()
        => ChildNodes.SelectMany(child => child.EnumerateFiles());
}

public sealed class UserCollectionFileNodeViewModel : UserCollectionNodeViewModel
{
    public UserCollectionFileNodeViewModel(Track track, string displayName, bool isMusicFile) : base(displayName)
    {
        Track = track;
        IsMusicFile = isMusicFile;
    }

    public Track Track { get; }
    public bool IsMusicFile { get; }
    public string Extension => (Track.Format ?? Track.GetExtension())?.Trim().TrimStart('.').ToUpperInvariant() ?? "—";
    public int Bitrate => Track.Bitrate;
    public bool IsSuspiciousLossless =>
        string.Equals(Track.Format ?? Track.GetExtension(), "flac", StringComparison.OrdinalIgnoreCase) &&
        Track.Bitrate > 0 &&
        Track.Bitrate <= 192;

    public override string Icon => IsSuspiciousLossless ? "⚠️" : (IsMusicFile ? "🎵" : "📄");
    public override string SecondaryText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Extension)) parts.Add(Extension);
            if (Bitrate > 0) parts.Add($"{Bitrate} kbps");
            if (Track.SampleRate.HasValue) parts.Add($"{Track.SampleRate.Value / 1000.0:F1} kHz");
            return string.Join(" • ", parts);
        }
    }

    public override string WarningText => IsSuspiciousLossless ? "Low Quality Transcode: FLAC with low reported bitrate." : string.Empty;

    public override IEnumerable<UserCollectionFileNodeViewModel> EnumerateAllFiles()
    {
        yield return this;
    }

    public override IEnumerable<UserCollectionFileNodeViewModel> EnumerateFiles()
    {
        if (IsMusicFile)
        {
            yield return this;
        }
    }
}
