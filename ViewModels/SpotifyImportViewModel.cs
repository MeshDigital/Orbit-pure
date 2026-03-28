using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public class SpotifyImportViewModel : INotifyPropertyChanged
{
    private readonly ILogger<SpotifyImportViewModel> _logger;
    private readonly SpotifyAuthService _authService;
    
    private readonly DownloadManager _downloadManager;
    private readonly ImportOrchestrator _importOrchestrator;
    private readonly ISpotifyMetadataService _spotifyMetadataService;
    private readonly SpotifyInputSource _spotifyInputSource;
    private readonly Services.ImportProviders.SpotifyImportProvider _spotifyProvider;
    private readonly Services.ImportProviders.SpotifyLikedSongsImportProvider _likedSongsProvider;
    private readonly Services.ImportProviders.CsvImportProvider _csvProvider;
    private readonly Services.ImportProviders.TracklistImportProvider _tracklistProvider;
    private readonly INavigationService _navigationService;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly IClipboardService _clipboardService;
    private readonly ConfigManager _configManager;
    private readonly AppConfig _appConfig;
    
    // Properties
    public ObservableCollection<SelectableTrack> Tracks { get; } = new();
    
    private string _playlistUrl = "";
    public string PlaylistUrl
    {
        get => _playlistUrl;
        set { _playlistUrl = value; OnPropertyChanged(); }
    }

    private string _playlistTitle = "Spotify Playlist";
    public string PlaylistTitle
    {
        get => _playlistTitle;
        set { _playlistTitle = value; OnPropertyChanged(); }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _isCsvDragging;
    public bool IsCsvDragging
    {
        get => _isCsvDragging;
        set { _isCsvDragging = value; OnPropertyChanged(); }
    }

    public int TrackCount => Tracks.Count;
    public int SelectedCount => Tracks.Count(t => t.IsSelected);

    public ObservableCollection<WebShortcutItemViewModel> WebShortcuts { get; } = new();
    public ObservableCollection<WebShortcutPresetViewModel> SubgenrePresets { get; } = new();

    private WebShortcutPresetViewModel? _selectedSubgenrePreset;
    public WebShortcutPresetViewModel? SelectedSubgenrePreset
    {
        get => _selectedSubgenrePreset;
        set { _selectedSubgenrePreset = value; OnPropertyChanged(); }
    }

    private string _newShortcutName = "";
    public string NewShortcutName
    {
        get => _newShortcutName;
        set { _newShortcutName = value; OnPropertyChanged(); }
    }

    private string _newShortcutUrl = "https://";
    public string NewShortcutUrl
    {
        get => _newShortcutUrl;
        set { _newShortcutUrl = value; OnPropertyChanged(); }
    }

    private string _genreSlugOrUrlInput = "melodic-house-techno";
    public string GenreSlugOrUrlInput
    {
        get => _genreSlugOrUrlInput;
        set { _genreSlugOrUrlInput = value; OnPropertyChanged(); }
    }

    // User Playlists
    public ObservableCollection<SpotifyPlaylistViewModel> UserPlaylists { get; } = new();
    
    private bool _isAuthenticated;
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        set 
        { 
            _isAuthenticated = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(ShowLoginButton));
            OnPropertyChanged(nameof(ShowPlaylists));
            OnPropertyChanged(nameof(ShowSearchResults));
        }
    }

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set { _searchQuery = value; OnPropertyChanged(); }
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set { _isSearching = value; OnPropertyChanged(); }
    }

    public ObservableCollection<SpotifyPlaylistViewModel> SearchResults { get; } = new();

    private int _selectedTabIndex = 0;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set 
        { 
            _selectedTabIndex = value; 
            OnPropertyChanged(); 
            if (value == 0 && UserPlaylists.Count == 0 && IsAuthenticated)
                _ = RefreshPlaylistsAsync();
        }
    }

    public bool ShowLoginButton => !IsAuthenticated;
    public bool ShowPlaylists => IsAuthenticated; // Controlled by TabIndex in View
    public bool ShowSearchResults => IsAuthenticated; // Controlled by TabIndex in View

    public ICommand ImportLikedSongsCommand { get; }
    public ICommand SelectCsvFileCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand SyncAllPlaylistsCommand { get; }
    public ICommand SyncFilteredPlaylistsCommand { get; }
    public ICommand LoadPlaylistCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand RefreshPlaylistsCommand { get; }
    public ICommand ImportPlaylistCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand ImportTracklistCommand { get; }
    public ICommand PasteTracklistFromClipboardCommand { get; }
    public ICommand CopyTracklistToClipboardCommand { get; }
    public ICommand ImportTracklistFromClipboardCommand { get; }
    public ICommand OpenTracklistsWebsiteCommand { get; }
    public ICommand AddWebShortcutCommand { get; }
    public ICommand AddSelectedSubgenrePresetCommand { get; }
    public ICommand AddGenreShortcutCommand { get; }

    private string _pastedTracklist = "";
    public string PastedTracklist
    {
        get => _pastedTracklist;
        set { _pastedTracklist = value; OnPropertyChanged(); }
    }

    private string _playlistFilter = "";
    public string PlaylistFilter
    {
        get => _playlistFilter;
        set
        {
            _playlistFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilteredUserPlaylists));
            OnPropertyChanged(nameof(HasFilteredPlaylists));
        }
    }

    public IEnumerable<SpotifyPlaylistViewModel> FilteredUserPlaylists => string.IsNullOrWhiteSpace(PlaylistFilter)
        ? UserPlaylists
        : UserPlaylists.Where(p => p.Name.Contains(PlaylistFilter, StringComparison.OrdinalIgnoreCase) || 
                                  p.Owner.Contains(PlaylistFilter, StringComparison.OrdinalIgnoreCase));

    public bool HasFilteredPlaylists => FilteredUserPlaylists.Any();
    public SpotifyImportViewModel(
        ILogger<SpotifyImportViewModel> logger,
        DownloadManager downloadManager,
        SpotifyAuthService authService,
        ImportOrchestrator importOrchestrator,
        ISpotifyMetadataService spotifyMetadataService,
        SpotifyInputSource spotifyInputSource,
        Services.ImportProviders.SpotifyImportProvider spotifyProvider,
        Services.ImportProviders.SpotifyLikedSongsImportProvider likedSongsProvider,
        Services.ImportProviders.CsvImportProvider csvProvider,
        Services.ImportProviders.TracklistImportProvider tracklistProvider,
        INavigationService navigationService,
        IFileInteractionService fileInteractionService,
        IClipboardService clipboardService,
        ConfigManager configManager,
        AppConfig appConfig)
    {
        _logger = logger;
        _downloadManager = downloadManager;
        _authService = authService;
        _importOrchestrator = importOrchestrator;
        _spotifyMetadataService = spotifyMetadataService;
        _spotifyInputSource = spotifyInputSource;
        _spotifyProvider = spotifyProvider;
        _likedSongsProvider = likedSongsProvider;
        _csvProvider = csvProvider;
        _tracklistProvider = tracklistProvider;
        _navigationService = navigationService;
        _fileInteractionService = fileInteractionService;
        _clipboardService = clipboardService;
        _configManager = configManager;
        _appConfig = appConfig;
        
        // Subscribe to auth changes
        _authService.AuthenticationChanged += (s, e) => 
        {
            IsAuthenticated = e;
            if (e) _ = RefreshPlaylistsAsync();
        };

        // Initial check
        Task.Run(async () => IsAuthenticated = await _authService.IsAuthenticatedAsync());

        LoadPlaylistCommand = new AsyncRelayCommand(LoadPlaylistAsync);
        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        CancelCommand = new RelayCommand(Cancel);
        
        ConnectCommand = new AsyncRelayCommand(ConnectSpotifyAsync);
        RefreshPlaylistsCommand = new AsyncRelayCommand(RefreshPlaylistsAsync);
        ImportPlaylistCommand = new AsyncRelayCommand<SpotifyPlaylistViewModel>(ImportUserPlaylistAsync);
        
        ImportLikedSongsCommand = new AsyncRelayCommand(ExecuteImportLikedSongsAsync, () => IsAuthenticated);
        SelectCsvFileCommand = new AsyncRelayCommand(ExecuteSelectCsvFileAsync);
        SearchCommand = new AsyncRelayCommand(ExecuteSearchAsync, () => IsAuthenticated);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        SyncAllPlaylistsCommand = new AsyncRelayCommand(ExecuteSyncAllPlaylistsAsync, () => IsAuthenticated && UserPlaylists.Count > 0);
        SyncFilteredPlaylistsCommand = new AsyncRelayCommand(ExecuteSyncFilteredPlaylistsAsync, () => IsAuthenticated && HasFilteredPlaylists);
        ImportTracklistCommand = new AsyncRelayCommand(ExecuteImportTracklistAsync);
        PasteTracklistFromClipboardCommand = new AsyncRelayCommand(ExecutePasteTracklistFromClipboardAsync);
        CopyTracklistToClipboardCommand = new AsyncRelayCommand(ExecuteCopyTracklistToClipboardAsync);
        ImportTracklistFromClipboardCommand = new AsyncRelayCommand(ExecuteImportTracklistFromClipboardAsync);
        OpenTracklistsWebsiteCommand = new RelayCommand(ExecuteOpenTracklistsWebsite);
        AddWebShortcutCommand = new AsyncRelayCommand(ExecuteAddWebShortcutAsync);
        AddSelectedSubgenrePresetCommand = new AsyncRelayCommand(ExecuteAddSelectedSubgenrePresetAsync);
        AddGenreShortcutCommand = new AsyncRelayCommand(ExecuteAddGenreShortcutAsync);

        // Disable unused commands
        DownloadCommand = new RelayCommand(() => {}, () => false);

        LoadWebShortcuts();
        LoadSubgenrePresets();
    }

    private async Task ExecuteImportLikedSongsAsync()
    {
        _logger.LogInformation("Starting 'Liked Songs' import from Spotify...");
        await _importOrchestrator.StartImportWithPreviewAsync(_likedSongsProvider, "spotify:liked");
    }

    public async Task ProcessCsvFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        
        try
        {
             _logger.LogInformation("Processing dropped CSV file: {Path}", filePath);
             await _importOrchestrator.StartImportWithPreviewAsync(_csvProvider, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process dropped CSV");
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private async Task ExecuteSelectCsvFileAsync()
    {
        try
        {
            var filters = new System.Collections.Generic.List<Services.FileDialogFilter>
            {
                new Services.FileDialogFilter("CSV Files", new System.Collections.Generic.List<string> { "csv" }),
                new Services.FileDialogFilter("All Files", new System.Collections.Generic.List<string> { "*" })
            };

            var filePath = await _fileInteractionService.OpenFileDialogAsync("Select CSV File", filters);

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                await ProcessCsvFileAsync(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to select CSV file");
            StatusMessage = $"Error selecting file: {ex.Message}";
        }
    }

    private async Task ExecuteSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        try
        {
            IsSearching = true;
            StatusMessage = $"Searching Spotify for '{SearchQuery}'...";
            SearchResults.Clear();
            OnPropertyChanged(nameof(ShowPlaylists));
            OnPropertyChanged(nameof(ShowSearchResults));

            var playlists = await _spotifyInputSource.SearchPlaylistsAsync(SearchQuery);
            foreach (var p in playlists) SearchResults.Add(p);

            var albums = await _spotifyInputSource.SearchAlbumsAsync(SearchQuery);
            foreach (var a in albums) SearchResults.Add(a);

            StatusMessage = $"Found {SearchResults.Count} results";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify search failed");
            StatusMessage = "Search failed: " + ex.Message;
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(ShowPlaylists));
            OnPropertyChanged(nameof(ShowSearchResults));
        }
    }

    private void ClearSearch()
    {
        SearchQuery = "";
        SearchResults.Clear();
        StatusMessage = "";
        OnPropertyChanged(nameof(ShowPlaylists));
        OnPropertyChanged(nameof(ShowSearchResults));
    }

    private async Task ExecuteImportTracklistAsync()
    {
        if (string.IsNullOrWhiteSpace(PastedTracklist))
        {
            StatusMessage = "Please paste a tracklist first";
            return;
        }

        try
        {
            _logger.LogInformation("Importing pasted tracklist...");
            await _importOrchestrator.StartImportWithPreviewAsync(_tracklistProvider, PastedTracklist);
            
            // Clear if successful navigation happened
            PastedTracklist = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import tracklist");
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private async Task ExecuteImportTracklistFromClipboardAsync()
    {
        try
        {
            var ok = await TryPasteTracklistFromClipboardAsync();
            if (!ok) return;
            await ExecuteImportTracklistAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import tracklist from clipboard");
            StatusMessage = $"Clipboard import failed: {ex.Message}";
        }
    }

    private async Task ExecutePasteTracklistFromClipboardAsync()
    {
        try
        {
            await TryPasteTracklistFromClipboardAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to paste tracklist from clipboard");
            StatusMessage = $"Clipboard paste failed: {ex.Message}";
        }
    }

    private async Task ExecuteCopyTracklistToClipboardAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(PastedTracklist))
            {
                StatusMessage = "Nothing to copy yet.";
                return;
            }

            await _clipboardService.SetTextAsync(PastedTracklist);
            StatusMessage = "Tracklist copied to clipboard.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy tracklist to clipboard");
            StatusMessage = $"Clipboard copy failed: {ex.Message}";
        }
    }

    private async Task<bool> TryPasteTracklistFromClipboardAsync()
    {
        var text = await _clipboardService.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "Clipboard is empty. Copy a tracklist first.";
            return false;
        }

        PastedTracklist = text;
        StatusMessage = "Pasted tracklist from clipboard.";
        return true;
    }

    private void ExecuteOpenTracklistsWebsite()
    {
        const string url = "https://www.1001tracklists.com/";

        try
        {
            OpenUrl(url);
            StatusMessage = "Opened 1001Tracklists in your browser.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open 1001Tracklists website");
            StatusMessage = $"Could not open browser automatically. Open this URL manually: {url}";
        }
    }

    private async Task ExecuteAddWebShortcutAsync()
    {
        var name = (NewShortcutName ?? string.Empty).Trim();
        var url = (NewShortcutUrl ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
        {
            StatusMessage = "Shortcut name and URL are required.";
            return;
        }

        if (!TryNormalizeAbsoluteUrl(url, out var normalizedUrl))
        {
            StatusMessage = "Shortcut URL must start with http:// or https://";
            return;
        }

        if (WebShortcuts.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "A shortcut with that name already exists.";
            return;
        }

        AddShortcutVm(name, normalizedUrl);
        await PersistWebShortcutsAsync();

        NewShortcutName = "";
        NewShortcutUrl = "https://";
        StatusMessage = $"Added shortcut '{name}'.";
    }

    private async Task ExecuteRemoveWebShortcutAsync(WebShortcutItemViewModel? shortcut)
    {
        if (shortcut == null) return;

        WebShortcuts.Remove(shortcut);
        await PersistWebShortcutsAsync();
        StatusMessage = $"Removed shortcut '{shortcut.Name}'.";
    }

    private async Task ExecuteAddSelectedSubgenrePresetAsync()
    {
        var preset = SelectedSubgenrePreset;
        if (preset == null)
        {
            StatusMessage = "Select a 1001 subgenre preset first.";
            return;
        }

        if (WebShortcuts.Any(s => s.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase) ||
                                  s.Url.Equals(preset.Url, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"Shortcut for '{preset.Name}' already exists.";
            return;
        }

        AddShortcutVm(preset.Name, preset.Url);
        await PersistWebShortcutsAsync();
        StatusMessage = $"Added preset '{preset.Name}'.";
    }

    private async Task ExecuteAddGenreShortcutAsync()
    {
        var input = (GenreSlugOrUrlInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            StatusMessage = "Enter a 1001 genre URL or slug first.";
            return;
        }

        if (!TryNormalizeGenreInput(input, out var slug))
        {
            StatusMessage = "Invalid genre input. Use a slug like 'techno' or a full 1001 genre URL.";
            return;
        }

        var url = BuildGenreUrl(slug);
        var name = $"1001 {ToDisplayName(slug)}";

        if (WebShortcuts.Any(s => s.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"Shortcut for '{name}' already exists.";
            return;
        }

        AddShortcutVm(name, url);
        await PersistWebShortcutsAsync();
        StatusMessage = $"Added genre shortcut '{name}'.";
    }

    private void ExecuteOpenWebShortcut(WebShortcutItemViewModel? shortcut)
    {
        if (shortcut == null) return;

        try
        {
            OpenUrl(shortcut.Url);
            StatusMessage = $"Opened '{shortcut.Name}'.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open shortcut {ShortcutName}", shortcut.Name);
            StatusMessage = $"Could not open '{shortcut.Name}'.";
        }
    }

    private void LoadWebShortcuts()
    {
        WebShortcuts.Clear();

        var shortcutSpecs = _appConfig.ImportWebShortcuts ?? new System.Collections.Generic.List<string>();
        foreach (var spec in shortcutSpecs)
        {
            if (TryParseShortcutSpec(spec, out var name, out var url))
            {
                AddShortcutVm(name, url);
            }
        }

        if (WebShortcuts.Count == 0)
        {
            AddShortcutVm("1001Tracklists", "https://www.1001tracklists.com/");
            AddShortcutVm("Beatport", "https://www.beatport.com/");
            AddShortcutVm("SoundCloud", "https://soundcloud.com/");
            _ = PersistWebShortcutsAsync();
        }
    }

    private void LoadSubgenrePresets()
    {
        SubgenrePresets.Clear();

        AddSubgenrePreset("Mainstage", "mainstage");
        AddSubgenrePreset("Trance", "trance");
        AddSubgenrePreset("Melodic House/Techno", "melodic-house-techno");
        AddSubgenrePreset("Techno", "techno");
        AddSubgenrePreset("House", "house");
        AddSubgenrePreset("Progressive House", "progressive-house");
        AddSubgenrePreset("Afro House", "afro-house");
        AddSubgenrePreset("Bass House", "bass-house");
        AddSubgenrePreset("Tech House", "tech-house");
        AddSubgenrePreset("Dance / Electro Pop", "dance-electro-pop");
        AddSubgenrePreset("Hard Dance", "hard-dance");
        AddSubgenrePreset("Trap", "trap");
        AddSubgenrePreset("Dubstep", "dubstep");
        AddSubgenrePreset("Drum & Bass", "drum-n-bass");
        AddSubgenrePreset("Goa / Psy-Trance", "goa-psy-trance");

        SelectedSubgenrePreset = SubgenrePresets.FirstOrDefault();
    }

    private void AddSubgenrePreset(string name, string slug)
    {
        SubgenrePresets.Add(new WebShortcutPresetViewModel
        {
            Name = $"1001 {name}",
            Url = BuildGenreUrl(slug)
        });
    }

    private static string BuildGenreUrl(string slug)
    {
        return $"https://www.1001tracklists.com/genre/{slug}/index.html";
    }

    private static bool TryNormalizeGenreInput(string input, out string slug)
    {
        slug = string.Empty;

        var raw = input.Trim();

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.Contains("1001tracklists.com", StringComparison.OrdinalIgnoreCase))
                return false;

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Expected path: genre/{slug}/index.html
            if (segments.Length < 2 || !segments[0].Equals("genre", StringComparison.OrdinalIgnoreCase))
                return false;

            raw = segments[1];
        }

        raw = raw.Trim('/').ToLowerInvariant();
        raw = raw.Replace(" ", "-")
                 .Replace("/", "-")
                 .Replace("_", "-")
                 .Replace("&", "-n-");

        while (raw.Contains("--", StringComparison.Ordinal))
            raw = raw.Replace("--", "-", StringComparison.Ordinal);

        raw = raw.Trim('-');

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // Keep slug strict and predictable
        if (!raw.All(c => char.IsLetterOrDigit(c) || c == '-'))
            return false;

        slug = raw;
        return true;
    }

    private static string ToDisplayName(string slug)
    {
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Equals("n", StringComparison.OrdinalIgnoreCase) ? "&" : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(" ", words);
    }

    private void AddShortcutVm(string name, string url)
    {
        var vm = new WebShortcutItemViewModel
        {
            Name = name,
            Url = url
        };

        vm.OpenCommand = new RelayCommand(() => ExecuteOpenWebShortcut(vm));
        vm.RemoveCommand = new AsyncRelayCommand(() => ExecuteRemoveWebShortcutAsync(vm));

        WebShortcuts.Add(vm);
    }

    private async Task PersistWebShortcutsAsync()
    {
        _appConfig.ImportWebShortcuts = WebShortcuts
            .Select(s => $"{s.Name}|{s.Url}")
            .ToList();

        await _configManager.SaveAsync(_appConfig);
    }

    private static bool TryParseShortcutSpec(string spec, out string name, out string url)
    {
        name = string.Empty;
        url = string.Empty;

        if (string.IsNullOrWhiteSpace(spec)) return false;

        var parts = spec.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;

        if (string.IsNullOrWhiteSpace(parts[0])) return false;
        if (!TryNormalizeAbsoluteUrl(parts[1], out var normalizedUrl)) return false;

        name = parts[0];
        url = normalizedUrl;
        return true;
    }

    private static bool TryNormalizeAbsoluteUrl(string input, out string normalized)
    {
        normalized = string.Empty;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        normalized = uri.ToString();
        return true;
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public async Task LoadPlaylistAsync()
    {
        if (string.IsNullOrWhiteSpace(PlaylistUrl))
        {
            StatusMessage = "Please enter a Spotify playlist URL";
            return;
        }

        _logger.LogInformation("Starting unified Spotify import: {Url}", PlaylistUrl);
        await _importOrchestrator.StartImportWithPreviewAsync(_spotifyProvider, PlaylistUrl);
    }

    private void SelectAll()
    {
        foreach (var track in Tracks)
        {
            track.IsSelected = true;
        }
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void DeselectAll()
    {
        foreach (var track in Tracks)
        {
            track.IsSelected = false;
        }
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void Cancel()
    {
        _navigationService.GoBack();
        _logger.LogInformation("Spotify import cancelled");
    }

    public void ReorderTrack(SelectableTrack source, SelectableTrack target)
    {
        if (source == null || target == null || source == target)
            return;

        int oldIndex = Tracks.IndexOf(source);
        int newIndex = Tracks.IndexOf(target);

        if (oldIndex == -1 || newIndex == -1)
            return;

        Tracks.Move(oldIndex, newIndex);

        // Renumber all tracks
        for (int i = 0; i < Tracks.Count; i++)
        {
            Tracks[i].TrackNumber = i + 1;
        }

        _logger.LogDebug("Reordered track from position {Old} to {New}", oldIndex + 1, newIndex + 1);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private async Task ConnectSpotifyAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Connecting to Spotify...";
            await _authService.StartAuthorizationAsync();
            // AuthenticationChanged event will handle the rest
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Spotify");
            StatusMessage = "Connection failed: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshPlaylistsAsync()
    {
        if (!IsAuthenticated) return;

        try
        {
            IsLoading = true;
            StatusMessage = "Fetching your playlists...";
            UserPlaylists.Clear();

            var client = await _authService.GetAuthenticatedClientAsync();
            var page = await client.Playlists.CurrentUsers();
            
            await foreach (var playlist in client.Paginate(page))
            {
                UserPlaylists.Add(new SpotifyPlaylistViewModel
                {
                    Id = playlist.Id ?? "",
                    Name = playlist.Name ?? "Unnamed Playlist",
                    ImageUrl = playlist.Images?.FirstOrDefault()?.Url ?? "",
                    TrackCount = playlist.Tracks?.Total ?? 0,
                    Owner = playlist.Owner?.DisplayName ?? "Unknown",
                    Url = (playlist.ExternalUrls != null && playlist.ExternalUrls.ContainsKey("spotify")) ? playlist.ExternalUrls["spotify"] : ""
                });
            }

            // Also try to get "My Library" (Liked Songs)
            // Note: Liked songs is a separate endpoint "Library.GetTracks", not a playlist.
            // We can add a fake "Liked Songs" entry if we want, handled by specific provider.
            UserPlaylists.Insert(0, new SpotifyPlaylistViewModel
            {
                Id = "me/tracks",
                Name = "Liked Songs",
                ImageUrl = "https://t.scdn.co/images/3099b3803ad9496896c43f22fe9be8c4.png", // Generic heart icon
                TrackCount = 0, // Hard to get total without a call
                Owner = "You",
                Url = "spotify:user:me:collection" // Special case handled by provider?
            });

            StatusMessage = $"Found {UserPlaylists.Count} playlists";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch playlists");
            StatusMessage = "Failed to load playlists";
            
            // If it's an auth error, we might want to reset auth
            if (ex.Message.Contains("Unauthorized"))
            {
                await _authService.SignOutAsync();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteSyncAllPlaylistsAsync()
    {
        if (UserPlaylists.Count == 0) return;

        try
        {
            IsLoading = true;
            StatusMessage = $"Syncing {UserPlaylists.Count} playlists...";
            
             // Use the direct provider call check
             var provider = (Services.ImportProviders.SpotifyImportProvider)_importOrchestrator.GetType()
                .GetField("_spotifyProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_importOrchestrator)!;

            foreach (var playlist in UserPlaylists.ToList())
            {
                if (playlist.Id == "me/tracks") continue; // Liked songs usually too big for silent bulk sync at once?
                
                StatusMessage = $"Syncing '{playlist.Name}'...";
                await _importOrchestrator.SilentImportAsync(provider, playlist.Url);
            }

        StatusMessage = "All playlists queued for sync!";
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to sync all playlists");
        StatusMessage = "Sync failed: " + ex.Message;
    }
    finally
    {
        IsLoading = false;
    }
}

public class WebShortcutItemViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ICommand OpenCommand { get; set; } = null!;
    public ICommand RemoveCommand { get; set; } = null!;
}

public class WebShortcutPresetViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

private async Task ExecuteSyncFilteredPlaylistsAsync()
{
    var filtered = FilteredUserPlaylists.ToList();
    if (filtered.Count == 0) return;

    try
    {
        IsLoading = true;
        StatusMessage = $"Syncing {filtered.Count} filtered playlists...";
        
         // Use the direct provider call check
         var provider = (Services.ImportProviders.SpotifyImportProvider)_importOrchestrator.GetType()
            .GetField("_spotifyProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(_importOrchestrator)!;

        foreach (var playlist in filtered)
        {
            if (playlist.Id == "me/tracks") continue;
            
            StatusMessage = $"Syncing '{playlist.Name}'...";
            await _importOrchestrator.SilentImportAsync(provider, playlist.Url);
        }

        StatusMessage = $"{filtered.Count} playlists queued for sync!";
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to sync filtered playlists");
        StatusMessage = "Sync failed: " + ex.Message;
    }
    finally
    {
        IsLoading = false;
    }
}

    private async Task ImportUserPlaylistAsync(SpotifyPlaylistViewModel? playlist)
    {
        if (playlist == null) return;

        _logger.LogInformation("Importing user playlist: {Name} ({Id})", playlist.Name, playlist.Id);
        
        if (playlist.Id == "me/tracks")
        {
             await _importOrchestrator.StartImportWithPreviewAsync(_likedSongsProvider, "spotify:liked");
        }
        else
        {
             // Use the provider from orchestrator
             var provider = (Services.ImportProviders.SpotifyImportProvider)_importOrchestrator.GetType()
                .GetField("_spotifyProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_importOrchestrator)!;
                
             await _importOrchestrator.StartImportWithPreviewAsync(provider, playlist.Url);
        }
    }
}

