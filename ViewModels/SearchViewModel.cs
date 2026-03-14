// CS0618: CustomWeights is obsolete but still used for migration stability
#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;

using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.ImportProviders;
using SLSKDONET.Views;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using System.Reactive.Linq;
using SLSKDONET.Configuration;
using System.Reactive.Disposables;

namespace SLSKDONET.ViewModels;

public partial class SearchViewModel : ReactiveObject, IDisposable
{
    private readonly ILogger<SearchViewModel> _logger;
    private readonly SoulseekAdapter _soulseek;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ImportOrchestrator _importOrchestrator;
    private readonly IEnumerable<IImportProvider> _importProviders;
    private readonly DownloadManager _downloadManager;
    private readonly INavigationService _navigationService;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly IClipboardService _clipboardService;
    private readonly SearchOrchestrationService _searchOrchestration;
    private readonly IBulkOperationCoordinator _bulkCoordinator;

    private readonly FileNameFormatter _fileNameFormatter;
    private readonly HashSet<string> _runtimeExcludedPhrases = new(StringComparer.OrdinalIgnoreCase);
    
    // Cleanup
    private readonly CompositeDisposable _disposables = new();
    private bool _isDisposed;

    public IEnumerable<string> PreferredFormats => new[] { "mp3", "flac", "m4a", "wav" }; // TODO: Load from config

    // Child ViewModels
    public ImportPreviewViewModel ImportPreviewViewModel { get; }
    public SearchFilterViewModel FilterViewModel { get; } = new();
    public UserCollectionViewModel UserCollectionBrowser { get; }

    // Hidden Results Counter
    private int _hiddenResultsCount;
    public int HiddenResultsCount
    {
        get => _hiddenResultsCount;
        set => this.RaiseAndSetIfChanged(ref _hiddenResultsCount, value);
    }
    
    // Selected items for Batch Actions
    public ObservableCollection<AnalyzedSearchResultViewModel> SelectedResults { get; } = new();
    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set 
        { 
            if (SetProperty(ref _searchQuery, value))
            {
                this.RaisePropertyChanged(nameof(CanSearch));
            }
        }
    }
    
    public bool CanSearch => !string.IsNullOrWhiteSpace(SearchQuery);

    private bool _isAlbumSearch;
    public bool IsAlbumSearch
    {
        get => _isAlbumSearch;
        set
        {
            if (SetProperty(ref _isAlbumSearch, value))
            {
                _searchResults.Clear();
            }
        }
    }
    

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }
    
    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private bool _isUserCollectionBrowserOpen;
    public bool IsUserCollectionBrowserOpen
    {
        get => _isUserCollectionBrowserOpen;
        set => SetProperty(ref _isUserCollectionBrowserOpen, value);
    }

    // Reactive State
    private readonly SourceList<AnalyzedSearchResultViewModel> _searchResults = new();
    private readonly ReadOnlyObservableCollection<AnalyzedSearchResultViewModel> _publicSearchResults;
    private readonly ObservableCollection<AnalyzedSearchResultViewModel> _searchResultsView = new();
    public ReadOnlyObservableCollection<AnalyzedSearchResultViewModel> SearchResults => _publicSearchResults;
    public ObservableCollection<AnalyzedSearchResultViewModel> SearchResultsView => _searchResultsView;
    
    // Phase 19: Search 2.0 Dense Grid Source


    // Search Parameters (Pre-Search)
    private int _minBitrate = 320;
    public int MinBitrate 
    {
        get => _minBitrate;
        set => this.RaiseAndSetIfChanged(ref _minBitrate, value);
    }
    
    private int _maxBitrate = 3000;
    public int MaxBitrate 
    {
        get => _maxBitrate;
        set => this.RaiseAndSetIfChanged(ref _maxBitrate, value);
    }

    // Phase 5: Ranking Weights (Control Surface)
    public double BitrateWeight
    {
        get => _config.CustomWeights.QualityWeight;
        set 
        { 
            _config.CustomWeights.QualityWeight = value;
            this.RaisePropertyChanged();
            OnRankingWeightsChanged();
            _configManager.Save(_config);
        }
    }

    public double ReliabilityWeight
    {
        get => _config.CustomWeights.AvailabilityWeight;
        set 
        { 
            _config.CustomWeights.AvailabilityWeight = value;
            this.RaisePropertyChanged(); // Notify UI
            OnRankingWeightsChanged();
            _configManager.Save(_config);
        }
    }

    public double MatchWeight
    {
        get => _config.CustomWeights.MusicalWeight;
        set 
        { 
            _config.CustomWeights.MusicalWeight = value;
            _config.CustomWeights.MetadataWeight = value;
            _config.CustomWeights.StringWeight = value;
            
            this.RaisePropertyChanged();
            OnRankingWeightsChanged();
            _configManager.Save(_config);
        }
    }

    // Format Toggles (Zone C)
    public bool IsFlacEnabled
    {
        get => FilterViewModel.FilterFlac;
        set { FilterViewModel.FilterFlac = value; this.RaisePropertyChanged(); OnRankingWeightsChanged(); }
    }
    
    public bool IsMp3Enabled
    {
        get => FilterViewModel.FilterMp3;
        set { FilterViewModel.FilterMp3 = value; this.RaisePropertyChanged(); OnRankingWeightsChanged(); }
    }

    public bool IsWavEnabled
    {
        get => FilterViewModel.FilterWav;
        set { FilterViewModel.FilterWav = value; this.RaisePropertyChanged(); OnRankingWeightsChanged(); }
    }


    // Commands
    public ICommand UnifiedSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand BrowseCsvCommand { get; }
    public ICommand PasteTracklistCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand AddToDownloadsCommand { get; }
    public ReactiveCommand<object?, System.Reactive.Unit> DownloadSelectedCommand { get; }
    public ICommand ApplyPresetCommand { get; } // Phase 5: Search Presets
    public ICommand BrowseUserSharesCommand { get; } // Phase 5: Directory Browsing
    public ICommand CloseUserCollectionBrowserCommand { get; }

    public SearchViewModel(
        ILogger<SearchViewModel> logger,
        SoulseekAdapter soulseek,
        AppConfig config,
        ConfigManager configManager,
        ImportOrchestrator importOrchestrator,
        IEnumerable<IImportProvider> importProviders,
        ImportPreviewViewModel importPreviewViewModel,
        UserCollectionViewModel userCollectionBrowser,
        DownloadManager downloadManager,
        INavigationService navigationService,
        IFileInteractionService fileInteractionService,
        IClipboardService clipboardService,
        SearchOrchestrationService searchOrchestration,
        FileNameFormatter fileNameFormatter,
        IEventBus eventBus,
        IBulkOperationCoordinator bulkCoordinator)
    {
        _logger = logger;
        _soulseek = soulseek;
        _config = config;
        _configManager = configManager;
        _importOrchestrator = importOrchestrator;
        _importProviders = importProviders;
        ImportPreviewViewModel = importPreviewViewModel;
        UserCollectionBrowser = userCollectionBrowser;
        _downloadManager = downloadManager;
        _navigationService = navigationService;
        _fileInteractionService = fileInteractionService;
        _clipboardService = clipboardService;
        _searchOrchestration = searchOrchestration;
        _fileNameFormatter = fileNameFormatter;
        _bulkCoordinator = bulkCoordinator;

        // Reactive Status Updates
        eventBus.GetEvent<TrackStateChangedEvent>()
            .Subscribe(OnTrackStateChanged)
            .DisposeWith(_disposables);
            
        eventBus.GetEvent<TrackAddedEvent>()
            .Subscribe(OnTrackAdded)
            .DisposeWith(_disposables);

        eventBus.GetEvent<ExcludedSearchPhrasesUpdatedEvent>()
            .Subscribe(OnExcludedSearchPhrasesUpdated)
            .DisposeWith(_disposables);
            
        // Phase 6: Removed FindSimilarRequestEvent

        // --- Reactive Pipeline Setup ---
        var filterPredicate = FilterViewModel.FilterChanged;

        _searchResults.Connect()
            .Filter(FilterViewModel.FilterChanged.Select(f => new Func<AnalyzedSearchResultViewModel, bool>(vm => f(vm.RawResult))))
            .Sort(SortExpressionComparer<AnalyzedSearchResultViewModel>.Descending(t => t.TrustScore))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _publicSearchResults)
            .DisposeMany() 
            .Subscribe(_ => 
            {
                HiddenResultsCount = _searchResults.Count - _publicSearchResults.Count;
                SyncSearchResultsView();
                this.RaisePropertyChanged(nameof(SearchResults)); 
            })
            .DisposeWith(_disposables);




        // Commands
        var canSearch = this.WhenAnyValue(x => x.SearchQuery, query => !string.IsNullOrWhiteSpace(query));
        
        UnifiedSearchCommand = ReactiveCommand.CreateFromTask(ExecuteUnifiedSearchAsync, canSearch);
        ClearSearchCommand = ReactiveCommand.Create(() => { SearchQuery = ""; _searchResults.Clear(); });
        BrowseCsvCommand = ReactiveCommand.CreateFromTask(ExecuteBrowseCsvAsync);
        PasteTracklistCommand = ReactiveCommand.CreateFromTask(ExecutePasteTracklistAsync);
        CancelSearchCommand = ReactiveCommand.Create(ExecuteCancelSearch);
        AddToDownloadsCommand = ReactiveCommand.CreateFromTask(ExecuteAddToDownloadsAsync);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask<object?>(ExecuteDownloadSelectedAsync);
        ApplyPresetCommand = ReactiveCommand.Create<string>(ExecuteApplyPreset);
        BrowseUserSharesCommand = ReactiveCommand.CreateFromTask<AnalyzedSearchResultViewModel>(ExecuteBrowseUserSharesAsync);
        CloseUserCollectionBrowserCommand = ReactiveCommand.Create(() => IsUserCollectionBrowserOpen = false);
        
        FilterViewModel.OnTokenSyncRequested = HandleTokenSync;
    }

    private void HandleTokenSync(string token, bool shouldAdd)
    {
        if (shouldAdd)
            InjectToken(token);
        else
            RemoveToken(token);
    }

    private void InjectToken(string token)
    {
        if (token.EndsWith("+") || token.StartsWith(">"))
        {
            RemoveBitrateTokens();
        }
        
        var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b";
        if (System.Text.RegularExpressions.Regex.IsMatch(SearchQuery ?? "", pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return;
        
        SearchQuery = $"{SearchQuery} {token}".Trim();
    }

    private void RemoveToken(string token)
    {
        var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b";
        var clean = System.Text.RegularExpressions.Regex.Replace(SearchQuery ?? "", pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        SearchQuery = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private void RemoveBitrateTokens()
    {
        var pattern = @"\b(\d{2,4}\+?|>\d{2,4})\b";
        var clean = System.Text.RegularExpressions.Regex.Replace(SearchQuery ?? "", pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        SearchQuery = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private async Task ExecuteUnifiedSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        var processedQuery = new List<string>();
        bool filtersModified = false;
        
        FilterViewModel.SetFromQueryParsing(() =>
        {
            FilterViewModel.Reset();

            var tokens = SearchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var token in tokens)
            {
                var lower = token.ToLowerInvariant();
                
                if (lower == "flac") { FilterViewModel.FilterFlac = true; FilterViewModel.FilterMp3 = false; FilterViewModel.FilterWav = false; filtersModified = true; continue; }
                if (lower == "wav") { FilterViewModel.FilterWav = true; FilterViewModel.FilterMp3 = false; FilterViewModel.FilterFlac = false; filtersModified = true; continue; }
                if (lower == "mp3") { FilterViewModel.FilterMp3 = true; FilterViewModel.FilterFlac = false; FilterViewModel.FilterWav = false; filtersModified = true; continue; }
                
                if (lower.StartsWith(">") && int.TryParse(lower.TrimStart('>'), out int minQ))
                {
                    FilterViewModel.MinBitrate = minQ; 
                    filtersModified = true;
                    continue;
                }
                if (lower.EndsWith("+") && int.TryParse(lower.TrimEnd('+'), out int minQ2))
                {
                    FilterViewModel.MinBitrate = minQ2;
                    filtersModified = true;
                    continue;
                }

                processedQuery.Add(token);
            }
        });

        string effectiveQuery = filtersModified ? string.Join(" ", processedQuery) : SearchQuery;
        if (string.IsNullOrWhiteSpace(effectiveQuery)) effectiveQuery = SearchQuery; 

        effectiveQuery = StripExcludedPhrases(effectiveQuery);
        if (string.IsNullOrWhiteSpace(effectiveQuery))
        {
            StatusText = "Search blocked: query only contains server-excluded phrases.";
            return;
        }

        if (!string.Equals(effectiveQuery, SearchQuery, StringComparison.Ordinal))
        {
            SearchQuery = effectiveQuery;
        }

        IsSearching = true;
        StatusText = "Searching...";
        await RunOnUiThreadAsync(() =>
        {
            _searchResults.Clear();
        });
        HiddenResultsCount = 0; 

        try
        {
            var provider = _importProviders.FirstOrDefault(p => p.CanHandle(SearchQuery));
            if (provider != null)
            {
                StatusText = $"Importing via {provider.Name}...";
                IsSearching = false;
                await _importOrchestrator.StartImportWithPreviewAsync(provider, SearchQuery);
                StatusText = "Ready";
                return;
            }

            StatusText = $"Listening for vibes: {effectiveQuery}...";
            
            var cts = new System.Threading.CancellationTokenSource();
            
            var buffer = new List<AnalyzedSearchResultViewModel>();
            var lastUpdate = DateTime.UtcNow;
            int totalFound = 0;

            try 
            {
                await foreach (var track in _searchOrchestration.SearchAsync(
                    effectiveQuery,
                    string.Join(",", PreferredFormats),
                    MinBitrate, 
                    MaxBitrate,
                    IsAlbumSearch,
                    cts.Token))
                {
                    var result = new SearchResult(track);
                    
                    var existing = _downloadManager.ActiveDownloads.FirstOrDefault(d => d.GlobalId == track.UniqueHash);
                    if (existing != null)
                    {
                        result.Status = (existing.State == PlaylistTrackState.Completed) ? TrackStatus.Downloaded :
                                        (existing.State == PlaylistTrackState.Failed) ? TrackStatus.Failed : 
                                        TrackStatus.Missing;
                    }
                    
                    // WRAP IN VIEWMODEL
                    var vm = new AnalyzedSearchResultViewModel(result);
                    
                    buffer.Add(vm);
                    totalFound++;

                    if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds > 250 || buffer.Count >= 50)
                    {
                        var toAdd = buffer.ToList();
                        buffer.Clear();
                        await RunOnUiThreadAsync(() => _searchResults.AddRange(toAdd));
                        lastUpdate = DateTime.UtcNow;
                        StatusText = $"Found {totalFound} tracks...";
                    }
                }

                if (buffer.Any())
                {
                    var toAdd = buffer.ToList();
                    buffer.Clear();
                    await RunOnUiThreadAsync(() => _searchResults.AddRange(toAdd));
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            finally
            {
                IsSearching = false;
                if (totalFound > 0)
                {
                    ApplyPercentileScoring(); // Updated for AnalyzedSearchResultViewModel
                    StatusText = $"Found {totalFound} items";
                }
                else StatusText = "No results found";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            StatusText = $"Error: {ex.Message}";
            IsSearching = false;
        }
    }

    private void ApplyPercentileScoring()
    {
        var results = _publicSearchResults.ToList();
        if (!results.Any()) return;
        
        var sorted = results.OrderByDescending(r => r.RawResult.CurrentRank).ToList();
        
        for (int i = 0; i < sorted.Count; i++)
        {
            var percentile = (double)i / sorted.Count;
            sorted[i].RawResult.Percentile = percentile;
        }
    }

    private async Task ExecuteBrowseCsvAsync()
    {
        try
        {
            var path = await _fileInteractionService.OpenFileDialogAsync("Select CSV File", new[] 
            { 
                new SLSKDONET.Services.FileDialogFilter("CSV Files", new List<string> { "csv" }),
                new SLSKDONET.Services.FileDialogFilter("All Files", new List<string> { "*" })
            });

            if (!string.IsNullOrEmpty(path))
            {
                SearchQuery = path; 
                await ExecuteUnifiedSearchAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error browsing for CSV");
            StatusText = "Error selecting file";
        }
    }

    private async Task ExecutePasteTracklistAsync()
    {
        try 
        {
            var text = await _clipboardService.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) 
            {
                StatusText = "Clipboard is empty";
                return;
            }

            var provider = _importProviders.FirstOrDefault(p => p.CanHandle(text));
            if (provider != null)
            {
                 StatusText = $"Importing from Clipboard ({provider.Name})...";
                 await _importOrchestrator.StartImportWithPreviewAsync(provider, text);
            }
            else
            {
                StatusText = "Clipboard content recognition failed.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pasting from clipboard");
            StatusText = "Clipboard error";
        }
    }

    private async Task ExecuteBrowseUserSharesAsync(AnalyzedSearchResultViewModel? vm)
    {
        if (vm == null) return;

        StatusText = $"Opening {vm.User}'s collection...";
        await UserCollectionBrowser.LoadUserAsync(vm.User);
        IsUserCollectionBrowserOpen = true;
    }

    private void ExecuteCancelSearch()
    {
        IsSearching = false;
        StatusText = "Cancelled";
    }

    private async Task ExecuteAddToDownloadsAsync()
    {
         await ExecuteDownloadSelectedAsync(null);
    }

    private async Task ExecuteDownloadSelectedAsync(object? parameter)
    {
        var toDownload = new List<AnalyzedSearchResultViewModel>();
        
        if (parameter is AnalyzedSearchResultViewModel single)
        {
            toDownload.Add(single);
        }
        else
        {
            toDownload.AddRange(SelectedResults);
        }

        if (!toDownload.Any()) return;

        if (_bulkCoordinator.IsRunning) return;

        await _bulkCoordinator.RunOperationAsync(
            toDownload,
            async (vm, ct) =>
            {
                var queued = _downloadManager.EnqueueTrack(vm.RawResult.Model);
                vm.RawResult.Status = queued ? TrackStatus.Pending : TrackStatus.Failed;
                return queued;
            },
            "Batch Download"
        );

        StatusText = $"Queued {toDownload.Count} downloads";
    }

    public void ResetState()
    {
        SearchQuery = "";
        IsSearching = false;
        IsUserCollectionBrowserOpen = false;
        _searchResults.Clear();
        StatusText = "Ready";
    }

    private void ExecuteApplyPreset(string presetName)
    {
        switch (presetName)
        {
            case "Deep Dive":
                BitrateWeight = 0.5;
                ReliabilityWeight = 0.5;
                MatchWeight = 2.0;
                IsFlacEnabled = false; 
                break;
            case "Quick Grab":
                BitrateWeight = 1.5;
                ReliabilityWeight = 2.0;
                MatchWeight = 1.0;
                break;
            case "High Fidelity":
                BitrateWeight = 2.0;
                ReliabilityWeight = 1.0;
                MatchWeight = 1.0;
                IsFlacEnabled = true;
                IsMp3Enabled = false;
                break;
             case "Balanced":
             default:
                BitrateWeight = 1.0;
                ReliabilityWeight = 1.0;
                MatchWeight = 1.0;
                IsFlacEnabled = false;
                IsMp3Enabled = false;
                break;
        }
    }

    private void SyncSearchResultsView()
    {
        _searchResultsView.Clear();
        foreach (var result in _publicSearchResults)
        {
            _searchResultsView.Add(result);
        }
    }

    private static async Task RunOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private void OnTrackStateChanged(TrackStateChangedEvent evt)
    {
        if (_searchResults.Count == 0) return;

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var status = evt.State switch
            {
                PlaylistTrackState.Completed => TrackStatus.Downloaded,
                PlaylistTrackState.Failed => TrackStatus.Failed,
                PlaylistTrackState.Downloading => TrackStatus.Pending,
                PlaylistTrackState.Searching => TrackStatus.Pending,
                PlaylistTrackState.Pending => TrackStatus.Pending,
                _ => TrackStatus.Missing
            };

            foreach (var result in _publicSearchResults)
            {
                if (result.RawResult.Model.UniqueHash == evt.TrackGlobalId)
                {
                    result.RawResult.Status = status;
                }
            }
        });
    }

    private void OnTrackAdded(TrackAddedEvent evt)
    {
         Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Placeholder for future use
        });
    }

    private void OnExcludedSearchPhrasesUpdated(ExcludedSearchPhrasesUpdatedEvent evt)
    {
        if (evt.Phrases == null || evt.Phrases.Count == 0) return;

        foreach (var phrase in evt.Phrases)
        {
            if (!string.IsNullOrWhiteSpace(phrase))
            {
                _runtimeExcludedPhrases.Add(phrase.Trim());
            }
        }

        var sanitized = StripExcludedPhrases(SearchQuery);
        if (!string.Equals(sanitized, SearchQuery, StringComparison.Ordinal))
        {
            SearchQuery = sanitized;
            StatusText = "Search query updated: removed server-excluded phrase(s).";
        }
    }

    private string StripExcludedPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || _runtimeExcludedPhrases.Count == 0)
            return query;

        var sanitized = query;
        foreach (var phrase in _runtimeExcludedPhrases)
        {
            var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(phrase)}\b";
            sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, pattern, " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return System.Text.RegularExpressions.Regex.Replace(sanitized, @"\s+", " ").Trim();
    }


    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _disposables.Dispose();
            _searchResults.Dispose();
            // Cancel any active search
            if (IsSearching)
            {
                ExecuteCancelSearch();
            }
        }

        _isDisposed = true;
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        return true;
    }

    private void OnRankingWeightsChanged()
    {
        var weights = _config.CustomWeights;
        ResultSorter.SetWeights(weights);
        RecalculateScores();
    }

    private void RecalculateScores()
    {
        if (_searchResults.Count == 0 || string.IsNullOrWhiteSpace(SearchQuery)) return;

        var artist = "";
        var title = SearchQuery;
        
        if (SearchQuery.Contains(" - "))
        {
            var parts = SearchQuery.Split(new[] { " - " }, 2, StringSplitOptions.RemoveEmptyEntries);
            artist = parts[0].Trim();
            title = parts[1].Trim();
        }
        else if (SearchQuery.Contains(" – ")) 
        {
            var parts = SearchQuery.Split(new[] { " – " }, 2, StringSplitOptions.RemoveEmptyEntries);
            artist = parts[0].Trim();
            title = parts[1].Trim();
        }

        var searchTrack = new Models.Track { Artist = artist, Title = title }; 

        var evaluator = new Models.FileConditionEvaluator();
        
        evaluator.AddPreferred(new Models.BitrateCondition 
        { 
            MinBitrate = MinBitrate, 
            MaxBitrate = MaxBitrate 
        });

        evaluator.AddPreferred(new Models.FormatCondition 
        { 
            AllowedFormats = GetActiveFormats() 
        });

        var items = _searchResults.Items.ToList();
        foreach (var item in items)
        {
             ResultSorter.CalculateRank(item.RawResult.Model, searchTrack, evaluator);
             item.RawResult.RefreshRank(); 
        }
    }
    
    private List<string> GetActiveFormats()
    {
        var list = new List<string>();
        if (IsFlacEnabled) list.Add("flac");
        if (IsMp3Enabled) list.Add("mp3");
        if (IsWavEnabled) list.Add("wav");
        if (list.Count == 0) list.AddRange(new[] { "mp3", "flac", "wav" }); 
        return list;
    }

}

