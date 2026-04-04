// CS0618: CustomWeights is obsolete but still used for migration stability
#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
using SLSKDONET.Events;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using SLSKDONET.Configuration;
using System.Reactive.Disposables;

namespace SLSKDONET.ViewModels;

public partial class SearchViewModel : ReactiveObject, IDisposable
{
    private sealed record SearchPresetDefinition(string Id, string DisplayName, string Subtitle, string Icon);

    private static readonly SearchPresetDefinition[] SearchPresetScale =
    {
        new("Quick Grab", "Fast", "Prioritize reliable, fast-to-download results.", "⚡"),
        new("Balanced", "Balanced", "Best default mix of quality, trust, and match strength.", "⚖️"),
        new("Deep Dive", "Deep Search", "Search wider and lean harder on title/metadata matching.", "🌊"),
        new("High Fidelity", "Lossless", "Strict quality-first mode with FLAC preference.", "💎")
    };

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
    private readonly SerialDisposable _searchSubscription = new();
    private readonly SerialDisposable _searchIdleMonitor = new();
    private readonly object _searchSessionGate = new();
    private bool _isDisposed;
    private bool _isApplyingPreset;
    private CancellationTokenSource? _activeSearchCts;
    private Guid? _currentSearchSessionId;

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
        set
        {
            if (_hiddenResultsCount != value)
            {
                this.RaiseAndSetIfChanged(ref _hiddenResultsCount, value);
                this.RaisePropertyChanged(nameof(HasHiddenResults));
                this.RaisePropertyChanged(nameof(AllResultsFilteredByRules));
                this.RaisePropertyChanged(nameof(ShowHiddenResultsButtonText));
            }
        }
    }

    private bool _showFilteredOutResults;
    public bool ShowFilteredOutResults
    {
        get => _showFilteredOutResults;
        set
        {
            if (SetProperty(ref _showFilteredOutResults, value))
            {
                this.RaisePropertyChanged(nameof(ShowHiddenResultsButtonText));
            }
        }
    }

    public bool HasHiddenResults => HiddenResultsCount > 0;
    public int DisplayedResultsCount => SearchResultsView.Count;
    public bool HasDisplayedResults => DisplayedResultsCount > 0;
    public bool AllResultsFilteredByRules => TotalResultsReceived > 0 && HiddenResultsCount >= TotalResultsReceived;
    public string ShowHiddenResultsButtonText => ShowFilteredOutResults
        ? "Hide filtered-out"
        : $"Show filtered-out ({HiddenResultsCount})";
    
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

    private bool _isListening;
    public bool IsListening
    {
        get => _isListening;
        set => SetProperty(ref _isListening, value);
    }

    public string CurrentSearchSessionId => _currentSearchSessionId?.ToString("N") ?? string.Empty;

    private double _resultsPerSecond;
    public double ResultsPerSecond
    {
        get => _resultsPerSecond;
        set => SetProperty(ref _resultsPerSecond, value);
    }

    private int _totalResultsReceived;
    public int TotalResultsReceived
    {
        get => _totalResultsReceived;
        set
        {
            if (SetProperty(ref _totalResultsReceived, value))
            {
                this.RaisePropertyChanged(nameof(AllResultsFilteredByRules));
            }
        }
    }

    private DateTime? _lastResultAtUtc;
    public DateTime? LastResultAtUtc
    {
        get => _lastResultAtUtc;
        set => SetProperty(ref _lastResultAtUtc, value);
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
    private readonly ReadOnlyObservableCollection<AnalyzedSearchResultViewModel> _searchResultsView;
    public ReadOnlyObservableCollection<AnalyzedSearchResultViewModel> SearchResults => _publicSearchResults;
    public ReadOnlyObservableCollection<AnalyzedSearchResultViewModel> SearchResultsView => _searchResultsView;
    
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

    private int _qualityPresetIndex = 1;
    public double QualityPresetSliderValue
    {
        get => _qualityPresetIndex;
        set
        {
            var roundedIndex = Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, SearchPresetScale.Length - 1);
            if (_qualityPresetIndex == roundedIndex)
                return;

            _qualityPresetIndex = roundedIndex;
            RaiseQualityPresetPropertiesChanged();
            ApplyPresetByIndex(roundedIndex);
        }
    }

    public string SelectedQualityPresetName => SearchPresetScale[_qualityPresetIndex].DisplayName;
    public string SelectedQualityPresetSubtitle => SearchPresetScale[_qualityPresetIndex].Subtitle;
    public string SelectedQualityPresetIcon => SearchPresetScale[_qualityPresetIndex].Icon;

    // Phase 5: Ranking Weights (Control Surface)
    public double BitrateWeight
    {
        get => _config.CustomWeights.QualityWeight;
        set 
        { 
            _config.CustomWeights.QualityWeight = value;
            this.RaisePropertyChanged();
            if (!_isApplyingPreset)
            {
                OnRankingWeightsChanged();
                _configManager.Save(_config);
                SyncQualityPresetFromCurrentSettings();
            }
        }
    }

    public double ReliabilityWeight
    {
        get => _config.CustomWeights.AvailabilityWeight;
        set 
        { 
            _config.CustomWeights.AvailabilityWeight = value;
            this.RaisePropertyChanged(); // Notify UI
            if (!_isApplyingPreset)
            {
                OnRankingWeightsChanged();
                _configManager.Save(_config);
                SyncQualityPresetFromCurrentSettings();
            }
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
            if (!_isApplyingPreset)
            {
                OnRankingWeightsChanged();
                _configManager.Save(_config);
                SyncQualityPresetFromCurrentSettings();
            }
        }
    }

    // Format Toggles (Zone C)
    public bool IsFlacEnabled
    {
        get => FilterViewModel.FilterFlac;
        set
        {
            FilterViewModel.FilterFlac = value;
            this.RaisePropertyChanged();
            if (!_isApplyingPreset)
            {
                OnRankingWeightsChanged();
                SyncQualityPresetFromCurrentSettings();
            }
        }
    }
    
    public bool IsMp3Enabled
    {
        get => FilterViewModel.FilterMp3;
        set
        {
            FilterViewModel.FilterMp3 = value;
            this.RaisePropertyChanged();
            if (!_isApplyingPreset)
            {
                OnRankingWeightsChanged();
                SyncQualityPresetFromCurrentSettings();
            }
        }
    }

    public bool IsWavEnabled
    {
        get => FilterViewModel.FilterWav;
        set
        {
            FilterViewModel.FilterWav = value;
            this.RaisePropertyChanged();
            if (!_isApplyingPreset)
            {
                OnRankingWeightsChanged();
                SyncQualityPresetFromCurrentSettings();
            }
        }
    }


    // Commands
    public ICommand UnifiedSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand BrowseCsvCommand { get; }
    public ICommand PasteTracklistCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand AddToDownloadsCommand { get; }
    public ReactiveCommand<object?, System.Reactive.Unit> DownloadSelectedCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CopyMetadataCommand { get; }
    public ICommand ApplyPresetCommand { get; } // Phase 5: Search Presets
    public ICommand BrowseUserSharesCommand { get; } // Phase 5: Directory Browsing
    public ICommand CloseUserCollectionBrowserCommand { get; }
    public ICommand ToggleHiddenResultsCommand { get; }
    public ICommand RelaxFiltersCommand { get; }

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
        _searchSubscription.DisposeWith(_disposables);
        _searchIdleMonitor.DisposeWith(_disposables);

        _searchResults.Connect()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _publicSearchResults)
            .DisposeMany()
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(SearchResults));
            })
            .DisposeWith(_disposables);

        var visibilityPredicate = this
            .WhenAnyValue(x => x.ShowFilteredOutResults)
            .StartWith(false)
            .CombineLatest(
                FilterViewModel.FilterChanged.StartWith(FilterViewModel.GetFilterPredicate()),
                (showFiltered, filter) => new Func<AnalyzedSearchResultViewModel, bool>(vm =>
                {
                    var isVisible = filter(vm.RawResult);
                    var reason = isVisible ? null : FilterViewModel.GetHiddenReason(vm.RawResult);
                    vm.SetFilterVisibility(!isVisible, reason);
                    return showFiltered || isVisible;
                }));

        _searchResults.Connect()
            .Filter(visibilityPredicate)
            .Sort(SortExpressionComparer<AnalyzedSearchResultViewModel>.Descending(t => t.TrustScore))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _searchResultsView)
            .DisposeMany()
            .Subscribe(_ =>
            {
                HiddenResultsCount = _publicSearchResults.Count(vm => vm.IsFilteredOut);
                this.RaisePropertyChanged(nameof(DisplayedResultsCount));
                this.RaisePropertyChanged(nameof(HasDisplayedResults));
            })
            .DisposeWith(_disposables);




        // Commands
        var canSearch = this.WhenAnyValue(x => x.SearchQuery, query => !string.IsNullOrWhiteSpace(query));
        
        UnifiedSearchCommand = ReactiveCommand.CreateFromTask(ExecuteUnifiedSearchAsync, canSearch);
        ClearSearchCommand = ReactiveCommand.Create(() =>
        {
            ExecuteCancelSearch();
            SearchQuery = "";
            _searchResults.Clear();
            ResetTelemetry();
        });
        BrowseCsvCommand = ReactiveCommand.CreateFromTask(ExecuteBrowseCsvAsync);
        PasteTracklistCommand = ReactiveCommand.CreateFromTask(ExecutePasteTracklistAsync);
        CancelSearchCommand = ReactiveCommand.Create(ExecuteCancelSearch);
        AddToDownloadsCommand = ReactiveCommand.CreateFromTask(ExecuteAddToDownloadsAsync);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask<object?>(ExecuteDownloadSelectedAsync);
        CopyMetadataCommand = ReactiveCommand.CreateFromTask(ExecuteCopyMetadataAsync);
        ApplyPresetCommand = ReactiveCommand.Create<string>(ExecuteApplyPreset);
        BrowseUserSharesCommand = ReactiveCommand.CreateFromTask<AnalyzedSearchResultViewModel>(ExecuteBrowseUserSharesAsync);
        CloseUserCollectionBrowserCommand = ReactiveCommand.Create(() => IsUserCollectionBrowserOpen = false);
        ToggleHiddenResultsCommand = ReactiveCommand.Create(ToggleHiddenResults);
        RelaxFiltersCommand = ReactiveCommand.Create(RelaxFilters);
        
        FilterViewModel.OnTokenSyncRequested = HandleTokenSync;

        // Contextual Sidebar: Update Right Panel when selection changes
        this.WhenAnyValue(x => x.SelectedResults.Count)
            .Where(count => count == 1)
            .Subscribe(_ => 
            {
                 var single = SelectedResults.FirstOrDefault();
                 if (single != null)
                 {
                     MessageBus.Current.SendMessage(new OpenInspectorEvent(single));
                 }
            })
            .DisposeWith(_disposables);

        SyncQualityPresetFromCurrentSettings();
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

        var cts = BeginNewSearchSession();
        var sessionId = _currentSearchSessionId;

        IsSearching = true;
        IsListening = true;
        StatusText = "Searching...";
        _searchResults.Clear();
        HiddenResultsCount = 0;
        ShowFilteredOutResults = false;
        ResetTelemetry();

        using var incomingTrackStream = new Subject<Track>();
        var streamDrainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var windowStartAt = DateTime.UtcNow;

        _searchSubscription.Disposable = incomingTrackStream
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(track => new AnalyzedSearchResultViewModel(new SearchResult(track)))
            .Buffer(TimeSpan.FromMilliseconds(250), 50)
            .Where(batch => batch.Count > 0)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(batch =>
            {
                if (_currentSearchSessionId != sessionId)
                    return;

                foreach (var item in batch)
                {
                    ApplyActiveDownloadStatus(item.RawResult);
                }

                _searchResults.AddRange(batch);

                TotalResultsReceived += batch.Count;
                LastResultAtUtc = DateTime.UtcNow;
                var elapsedMs = Math.Max(1.0, (LastResultAtUtc.Value - windowStartAt).TotalMilliseconds);
                ResultsPerSecond = Math.Round(batch.Count * 1000.0 / elapsedMs, 1);
                windowStartAt = LastResultAtUtc.Value;
                StatusText = $"Found {TotalResultsReceived} files (streaming at {ResultsPerSecond:0.#} results/sec)";
            },
            ex => streamDrainTcs.TrySetException(ex),
            () => streamDrainTcs.TrySetResult());

        _searchIdleMonitor.Disposable = Observable.Interval(TimeSpan.FromSeconds(1), RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!IsListening || !LastResultAtUtc.HasValue)
                    return;

                if (DateTime.UtcNow - LastResultAtUtc.Value >= TimeSpan.FromSeconds(5))
                {
                    ResultsPerSecond = 0;
                    StatusText = $"Found {TotalResultsReceived} files (stream idle)";
                }
            });

        try
        {
            var provider = _importProviders.FirstOrDefault(p => p.CanHandle(SearchQuery));
            if (provider != null)
            {
                StatusText = $"Importing via {provider.Name}...";
                EndActiveSearchSession(cancelNetwork: true);
                IsSearching = false;
                await _importOrchestrator.StartImportWithPreviewAsync(provider, SearchQuery);
                StatusText = "Ready";
                return;
            }

            StatusText = $"Listening for vibes: {effectiveQuery}...";

            try
            {
                await foreach (var track in _searchOrchestration.SearchAsync(
                    query: effectiveQuery,
                    preferredFormats: string.Join(",", PreferredFormats),
                    minBitrate: MinBitrate,
                    maxBitrate: MaxBitrate,
                    isAlbumSearch: IsAlbumSearch,
                    maxResultsPerLane: 200,
                    cancellationToken: cts.Token))
                {
                    if (_currentSearchSessionId != sessionId)
                    {
                        break;
                    }

                    incomingTrackStream.OnNext(track);
                }

                incomingTrackStream.OnCompleted();
            }
            catch (OperationCanceledException)
            {
                incomingTrackStream.OnCompleted();
                if (_currentSearchSessionId == sessionId)
                {
                    StatusText = "Stopped listening";
                }
            }
            finally
            {
                if (_currentSearchSessionId == sessionId)
                {
                    await streamDrainTcs.Task;

                    IsSearching = false;
                    IsListening = false;

                    if (TotalResultsReceived > 0)
                    {
                        ApplyPercentileScoring();
                        ResultsPerSecond = 0;

                        if (DisplayedResultsCount == 0 && HiddenResultsCount > 0)
                        {
                            // If everything was filtered out, auto-reveal so streaming doesn't look broken.
                            ShowFilteredOutResults = true;
                            StatusText = $"Found {TotalResultsReceived} files (all filtered by current rules; showing filtered-out)";
                        }
                        else
                        {
                            StatusText = $"Found {TotalResultsReceived} files (stream idle)";
                        }
                    }
                    else
                    {
                        StatusText = "No results found";
                    }

                    EndActiveSearchSession(cancelNetwork: false, disposeSubscription: false);
                }
            }
        }
        catch (SearchLimitExceededException ex)
        {
            IsSearching = false;
            IsListening = false;
            ResultsPerSecond = 0;
            EndActiveSearchSession(cancelNetwork: true);
            StatusText = $"⚠️ Showing first {ex.HardResultCap:N0} results. Please use a more specific search.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            StatusText = $"Error: {ex.Message}";
            IsSearching = false;
            IsListening = false;
            EndActiveSearchSession(cancelNetwork: true);
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

    private async Task ExecuteCopyMetadataAsync()
    {
        var resultsToCopy = SelectedResults.Any()
            ? SelectedResults.ToList()
            : _publicSearchResults.Take(1).ToList();

        if (!resultsToCopy.Any())
        {
            StatusText = "No results selected to copy.";
            return;
        }

        var lines = resultsToCopy.Select(result =>
            $"{result.DisplayName} | {result.RawResult.FileFormat} | {result.BitRate} kbps | {result.User}");

        await _clipboardService.SetTextAsync(string.Join(Environment.NewLine, lines));
        StatusText = $"Copied metadata for {resultsToCopy.Count} item(s).";
    }

    private void ExecuteCancelSearch()
    {
        EndActiveSearchSession(cancelNetwork: true);
        IsSearching = false;
        IsListening = false;
        ResultsPerSecond = 0;
        StatusText = "Stopped listening";
    }

    private CancellationTokenSource BeginNewSearchSession()
    {
        lock (_searchSessionGate)
        {
            EndActiveSearchSession(cancelNetwork: true);

            _activeSearchCts = new CancellationTokenSource();
            _currentSearchSessionId = Guid.NewGuid();
            this.RaisePropertyChanged(nameof(CurrentSearchSessionId));
            return _activeSearchCts;
        }
    }

    private void EndActiveSearchSession(bool cancelNetwork, bool disposeSubscription = true)
    {
        CancellationTokenSource? cts;
        lock (_searchSessionGate)
        {
            cts = _activeSearchCts;
            _activeSearchCts = null;
            if (disposeSubscription)
            {
                _searchSubscription.Disposable = null;
            }
            _searchIdleMonitor.Disposable = null;
            _currentSearchSessionId = null;
            this.RaisePropertyChanged(nameof(CurrentSearchSessionId));
        }

        if (cts == null)
            return;

        try
        {
            if (cancelNetwork)
            {
                cts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void ResetTelemetry()
    {
        ResultsPerSecond = 0;
        TotalResultsReceived = 0;
        LastResultAtUtc = null;
    }

    private void ApplyActiveDownloadStatus(SearchResult result)
    {
        if (_downloadManager is null)
            return;

        var existing = _downloadManager.ActiveDownloads.FirstOrDefault(d => d.GlobalId == result.Model.UniqueHash);
        if (existing == null)
            return;

        result.Status = existing.State switch
        {
            PlaylistTrackState.Completed => TrackStatus.Downloaded,
            PlaylistTrackState.Failed => TrackStatus.Failed,
            _ => TrackStatus.Missing
        };
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
        ExecuteCancelSearch();
        SearchQuery = "";
        IsSearching = false;
        IsUserCollectionBrowserOpen = false;
        _searchResults.Clear();
        ResetTelemetry();
        StatusText = "Ready";
    }

    private void ExecuteApplyPreset(string presetName)
    {
        var presetIndex = Array.FindIndex(SearchPresetScale, preset => string.Equals(preset.Id, presetName, StringComparison.OrdinalIgnoreCase));
        if (presetIndex < 0)
        {
            presetIndex = 1;
        }

        if (_qualityPresetIndex != presetIndex)
        {
            _qualityPresetIndex = presetIndex;
            RaiseQualityPresetPropertiesChanged();
        }

        ApplyPresetByIndex(presetIndex);
    }

    private void ApplyPresetByIndex(int presetIndex)
    {
        presetIndex = Math.Clamp(presetIndex, 0, SearchPresetScale.Length - 1);
        _isApplyingPreset = true;

        try
        {
            switch (SearchPresetScale[presetIndex].Id)
            {
                case "Deep Dive":
                    _config.CustomWeights.QualityWeight = 0.5;
                    _config.CustomWeights.AvailabilityWeight = 0.5;
                    _config.CustomWeights.MusicalWeight = 2.0;
                    _config.CustomWeights.MetadataWeight = 2.0;
                    _config.CustomWeights.StringWeight = 2.0;
                    FilterViewModel.FilterFlac = false;
                    FilterViewModel.FilterMp3 = true;
                    FilterViewModel.FilterWav = true;
                    break;
                case "Quick Grab":
                    _config.CustomWeights.QualityWeight = 1.5;
                    _config.CustomWeights.AvailabilityWeight = 2.0;
                    _config.CustomWeights.MusicalWeight = 1.0;
                    _config.CustomWeights.MetadataWeight = 1.0;
                    _config.CustomWeights.StringWeight = 1.0;
                    FilterViewModel.FilterFlac = true;
                    FilterViewModel.FilterMp3 = true;
                    FilterViewModel.FilterWav = true;
                    break;
                case "High Fidelity":
                    _config.CustomWeights.QualityWeight = 2.0;
                    _config.CustomWeights.AvailabilityWeight = 1.0;
                    _config.CustomWeights.MusicalWeight = 1.0;
                    _config.CustomWeights.MetadataWeight = 1.0;
                    _config.CustomWeights.StringWeight = 1.0;
                    FilterViewModel.FilterFlac = true;
                    FilterViewModel.FilterMp3 = false;
                    FilterViewModel.FilterWav = true;
                    break;
                case "Balanced":
                default:
                    _config.CustomWeights.QualityWeight = 1.0;
                    _config.CustomWeights.AvailabilityWeight = 1.0;
                    _config.CustomWeights.MusicalWeight = 1.0;
                    _config.CustomWeights.MetadataWeight = 1.0;
                    _config.CustomWeights.StringWeight = 1.0;
                    FilterViewModel.FilterFlac = true;
                    FilterViewModel.FilterMp3 = true;
                    FilterViewModel.FilterWav = true;
                    break;
            }
        }
        finally
        {
            _isApplyingPreset = false;
        }

        this.RaisePropertyChanged(nameof(BitrateWeight));
        this.RaisePropertyChanged(nameof(ReliabilityWeight));
        this.RaisePropertyChanged(nameof(MatchWeight));
        this.RaisePropertyChanged(nameof(IsFlacEnabled));
        this.RaisePropertyChanged(nameof(IsMp3Enabled));
        this.RaisePropertyChanged(nameof(IsWavEnabled));

        OnRankingWeightsChanged();
        _configManager.Save(_config);
    }

    private void SyncQualityPresetFromCurrentSettings()
    {
        var matchedIndex = GetMatchingPresetIndex();
        if (_qualityPresetIndex == matchedIndex)
            return;

        _qualityPresetIndex = matchedIndex;
        RaiseQualityPresetPropertiesChanged();
    }

    private int GetMatchingPresetIndex()
    {
        if (Math.Abs(BitrateWeight - 1.5) < 0.001 &&
            Math.Abs(ReliabilityWeight - 2.0) < 0.001 &&
            Math.Abs(MatchWeight - 1.0) < 0.001 &&
            IsFlacEnabled &&
            IsMp3Enabled &&
            IsWavEnabled)
        {
            return 0;
        }

        if (Math.Abs(BitrateWeight - 1.0) < 0.001 &&
            Math.Abs(ReliabilityWeight - 1.0) < 0.001 &&
            Math.Abs(MatchWeight - 1.0) < 0.001 &&
            IsFlacEnabled &&
            IsMp3Enabled &&
            IsWavEnabled)
        {
            return 1;
        }

        if (Math.Abs(BitrateWeight - 0.5) < 0.001 &&
            Math.Abs(ReliabilityWeight - 0.5) < 0.001 &&
            Math.Abs(MatchWeight - 2.0) < 0.001 &&
            !IsFlacEnabled &&
            IsMp3Enabled &&
            IsWavEnabled)
        {
            return 2;
        }

        if (Math.Abs(BitrateWeight - 2.0) < 0.001 &&
            Math.Abs(ReliabilityWeight - 1.0) < 0.001 &&
            Math.Abs(MatchWeight - 1.0) < 0.001 &&
            IsFlacEnabled &&
            !IsMp3Enabled &&
            IsWavEnabled)
        {
            return 3;
        }

        return 1;
    }

    private void RaiseQualityPresetPropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(QualityPresetSliderValue));
        this.RaisePropertyChanged(nameof(SelectedQualityPresetName));
        this.RaisePropertyChanged(nameof(SelectedQualityPresetSubtitle));
        this.RaisePropertyChanged(nameof(SelectedQualityPresetIcon));
    }

    private void ToggleHiddenResults()
    {
        if (!HasHiddenResults)
            return;

        ShowFilteredOutResults = !ShowFilteredOutResults;
        StatusText = ShowFilteredOutResults
            ? $"Showing cached filtered-out results ({HiddenResultsCount} hidden by current filters)."
            : $"Showing only visible results ({DisplayedResultsCount} currently pass filters).";
    }

    private void RelaxFilters()
    {
        ShowFilteredOutResults = true;
        FilterViewModel.SetFromQueryParsing(() =>
        {
            FilterViewModel.BouncerMode = BouncerMode.Relaxed;
            FilterViewModel.HideSuspects = false;
            FilterViewModel.MinBitrate = 0;
            FilterViewModel.FilterMp3 = true;
            FilterViewModel.FilterFlac = true;
            FilterViewModel.FilterWav = true;
        });

        StatusText = "Relaxed filters for the cached result set. Hidden matches are now visible without a new network search.";
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
            EndActiveSearchSession(cancelNetwork: true);
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

