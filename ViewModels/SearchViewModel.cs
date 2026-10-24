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
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using SLSKDONET.Configuration;
using System.Reactive.Disposables;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels;

public partial class SearchViewModel : ReactiveObject, IDisposable
{
    private sealed record SearchPresetDefinition(string Id, string DisplayName, string Subtitle);

    private static readonly SearchPresetDefinition[] SearchPresetScale =
    {
        new("Quick Grab", "Fast", "Prioritize reliable, fast-to-download results."),
        new("Balanced", "Balanced", "Best default mix of quality, trust, and match strength."),
        new("Deep Dive", "Deep search", "Lean harder on title/metadata matching."),
        new("High Fidelity", "Lossless", "Quality-first: FLAC/WAV only, quality weighted highest.")
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
    private readonly IEventBus _eventBus;

    private readonly FileNameFormatter _fileNameFormatter;
    private readonly HashSet<string> _runtimeExcludedPhrases = new(StringComparer.OrdinalIgnoreCase);
    
    // Cleanup
    private readonly CompositeDisposable _disposables = new();
    private readonly Subject<Unit> _rankingRefreshRequests = new();
    private readonly SerialDisposable _searchSubscription = new();
    private readonly SerialDisposable _searchIdleMonitor = new();
    private readonly object _searchSessionGate = new();
    private bool _isDisposed;
    private bool _isApplyingPreset;
    private CancellationTokenSource? _activeSearchCts;
    private Guid? _currentSearchSessionId;

    // Was a hardcoded ["mp3","flac","m4a","wav"] regardless of the IsFlacEnabled/IsMp3Enabled/
    // IsWavEnabled toggles — those only affected local re-ranking (GetActiveFormats, used for
    // display/sort) while the actual network query always requested every format anyway,
    // defeating SearchOrchestrationService.BuildNetworkQuery's lossless-only negative-token
    // injection (which inspects this exact list). Now derives from the same toggle state.
    public IEnumerable<string> PreferredFormats => GetActiveFormats();

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
    public bool HasSelectedResults => SelectedResults.Count > 0;
    public string SelectedResultsSummary => SelectedResults.Count switch
    {
        0 => "Select results to download or stage into a mix.",
        1 => $"1 result armed • {SelectedResults[0].DisplayName}",
        _ => $"{SelectedResults.Count} results armed for batch download or Add to Mix"
    };
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
        set
        {
            if (SetProperty(ref _isSearching, value))
            {
                RaiseResultStateChanged();
            }
        }
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
                RaiseResultStateChanged();
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

    // Session Downloads: tracks queued ad-hoc from the search page
    private readonly ObservableCollection<AnalyzedSearchResultViewModel> _sessionDownloads = new();
    public ObservableCollection<AnalyzedSearchResultViewModel> SessionDownloads => _sessionDownloads;
    private bool _hasSessionDownloads;
    public bool HasSessionDownloads
    {
        get => _hasSessionDownloads;
        private set => SetProperty(ref _hasSessionDownloads, value);
    }
    
    // Phase 19: Search 2.0 Dense Grid Source


    // Search Parameters (Pre-Search)
    /// <summary>Minimum bitrate (kbps, 0 = any). One value drives both the network request and
    /// the client-side filter — they used to be two separate 320 defaults with two separate
    /// controls, so lowering one silently did nothing.</summary>
    public int MinBitrate
    {
        get => FilterViewModel.MinBitrate;
        set => FilterViewModel.MinBitrate = value;
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

    public bool IsAiffEnabled
    {
        get => FilterViewModel.FilterAiff;
        set
        {
            FilterViewModel.FilterAiff = value;
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


    /// <summary>Raised when Ctrl+F/"Focus Search" navigates here so SearchPage's code-behind can
    /// put keyboard focus in the search box — the ViewModel has no direct handle on the Avalonia
    /// control, so the View subscribes to this rather than the reverse.</summary>
    public event Action? FocusRequested;

    // Commands
    public ICommand UnifiedSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand BrowseCsvCommand { get; }
    public ICommand PasteTracklistCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand AddToDownloadsCommand { get; }
    public ICommand AddToPlaylistCommand { get; }
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
        _eventBus = eventBus;
        _bulkCoordinator = bulkCoordinator;

        // Reactive Status Updates
        eventBus.GetEvent<TrackStateChangedEvent>()
            .Subscribe(OnTrackStateChanged)
            .DisposeWith(_disposables);
            
        eventBus.GetEvent<FocusSearchBoxRequestedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => FocusRequested?.Invoke())
            .DisposeWith(_disposables);

        eventBus.GetEvent<ExcludedSearchPhrasesUpdatedEvent>()
            .Subscribe(OnExcludedSearchPhrasesUpdated)
            .DisposeWith(_disposables);
            
        // Phase 6: Removed FindSimilarRequestEvent

        // --- Reactive Pipeline Setup ---
        _searchSubscription.DisposeWith(_disposables);
        _searchIdleMonitor.DisposeWith(_disposables);

        // Format toggles (IsFlacEnabled/IsMp3Enabled/IsWavEnabled) and the quality preset slider
        // each used to trigger RecalculateScores() (a full synchronous re-rank of every buffered
        // search result) plus a synchronous config save directly, with no debounce — dragging the
        // slider or flipping toggles back-to-back re-ran the whole re-rank/save once per change.
        // Coalesce into one recompute per change-burst instead. Throttle on the default (thread-
        // pool) scheduler and marshal to the UI thread via Dispatcher.UIThread.Post rather than
        // .ObserveOn(RxApp.MainThreadScheduler) — matches LibraryViewModel's established debounce
        // pattern, which avoids relying on RxApp.MainThreadScheduler's own delayed-scheduling
        // (that scheduler can be torn down independently of this ViewModel, e.g. across test runs,
        // which crashed the process when a pending Throttle timer fired after teardown).
        _rankingRefreshRequests
            .Throttle(TimeSpan.FromMilliseconds(200))
            .Subscribe(_ =>
            {
                try
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            RecalculateScores();
                            _configManager.Save(_config);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Ranking refresh failed");
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to schedule ranking refresh");
                }
            })
            .DisposeWith(_disposables);

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
                    try
                    {
                        var isVisible = filter(vm.RawResult);
                        var reason = isVisible ? null : FilterViewModel.GetHiddenReason(vm.RawResult);
                        vm.SetFilterVisibility(!isVisible, reason);
                        return showFiltered || isVisible;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Search visibility evaluation failed for result {File}. Falling back to visible.", vm.Filename);
                        vm.SetFilterVisibility(false, null);
                        return true;
                    }
                }));

        _searchResults.Connect()
            .Filter(visibilityPredicate)
            // Column-header sorting: the comparer is swapped by SortByCommand, and a resort is
            // requested after re-ranking (CurrentRank changes in place, which Sort can't see).
            .Sort(_sortComparerChanges, resort: _resortRequests)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _searchResultsView)
            .DisposeMany()
            .Subscribe(_ =>
            {
                HiddenResultsCount = _publicSearchResults.Count(vm => vm.IsFilteredOut);
                this.RaisePropertyChanged(nameof(DisplayedResultsCount));
                this.RaisePropertyChanged(nameof(HasDisplayedResults));
                RaiseResultStateChanged();
            })
            .DisposeWith(_disposables);




        // Commands
        var canSearch = this.WhenAnyValue(
            x => x.SearchQuery, x => x.IsPeerScope, x => x.PeerUsername,
            (query, peer, user) => !string.IsNullOrWhiteSpace(query) && (!peer || !string.IsNullOrWhiteSpace(user)));
        
        var searchCmd = ReactiveCommand.CreateFromTask(ExecuteUnifiedSearchAsync, canSearch);
        searchCmd.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsSearching = false;
                IsListening = false;
                ReportSearchError(ex.InnerException?.Message ?? ex.Message);
            })
            .DisposeWith(_disposables);
        UnifiedSearchCommand = searchCmd;

        ClearSearchCommand = ReactiveCommand.Create(() =>
        {
            ExecuteCancelSearch();
            SearchQuery = "";
            _searchResults.Clear();
            // SelectedResults is a separate collection holding direct VM references, not a live
            // view derived from _searchResults — clearing the results grid left stale selections
            // pointing at VMs that no longer existed anywhere, so "X selected" / Download Selected
            // kept showing/acting on results that had just been cleared. SessionDownloads is
            // deliberately NOT cleared here — it's the "SESSION QUEUE" strip tracking downloads
            // across the whole session, not scoped to the current search's results.
            SelectedResults.Clear();
            ResetTelemetry();
            HasSearched = false;
            SearchErrorText = null;
            StatusText = "";
            RaiseResultStateChanged();
        });

        var csvCmd = ReactiveCommand.CreateFromTask(ExecuteBrowseCsvAsync);
        csvCmd.ThrownExceptions.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ReportCommandFailure).DisposeWith(_disposables);
        BrowseCsvCommand = csvCmd;

        var pasteCmd = ReactiveCommand.CreateFromTask(ExecutePasteTracklistAsync);
        pasteCmd.ThrownExceptions.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ReportCommandFailure).DisposeWith(_disposables);
        PasteTracklistCommand = pasteCmd;

        CancelSearchCommand = ReactiveCommand.Create(ExecuteCancelSearch);

        var addToDownloadsCmd = ReactiveCommand.CreateFromTask(ExecuteAddToDownloadsAsync);
        addToDownloadsCmd.ThrownExceptions.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ReportCommandFailure).DisposeWith(_disposables);
        AddToDownloadsCommand = addToDownloadsCmd;

        var addToPlaylistCmd = ReactiveCommand.CreateFromTask(ExecuteAddSelectedToPlaylistAsync);
        addToPlaylistCmd.ThrownExceptions.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ReportCommandFailure).DisposeWith(_disposables);
        AddToPlaylistCommand = addToPlaylistCmd;

        DownloadSelectedCommand = ReactiveCommand.CreateFromTask<object?>(ExecuteDownloadSelectedAsync);
        DownloadSelectedCommand.ThrownExceptions.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ReportCommandFailure).DisposeWith(_disposables);

        CopyMetadataCommand = ReactiveCommand.CreateFromTask(ExecuteCopyMetadataAsync);
        CopyMetadataCommand.ThrownExceptions.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ReportCommandFailure).DisposeWith(_disposables);

        ApplyPresetCommand = ReactiveCommand.Create<string>(ExecuteApplyPreset);

        var browseSharesCmd = ReactiveCommand.CreateFromTask<object?>(ExecuteBrowseUserSharesAsync);
        browseSharesCmd.ThrownExceptions.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ReportCommandFailure).DisposeWith(_disposables);
        BrowseUserSharesCommand = browseSharesCmd;

        CloseUserCollectionBrowserCommand = ReactiveCommand.Create(() => IsUserCollectionBrowserOpen = false);
        ToggleHiddenResultsCommand = ReactiveCommand.Create(ToggleHiddenResults);
        RelaxFiltersCommand = ReactiveCommand.Create(RelaxFilters);
        
        // Contextual Sidebar: Update Right Panel when selection changes
        SelectedResults.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasSelectedResults));
            this.RaisePropertyChanged(nameof(SelectedResultsSummary));
        };

        this.WhenAnyValue(x => x.SelectedResults.Count)
            .Where(count => count == 1)
            .Subscribe(_ => 
            {
                 var single = SelectedResults.FirstOrDefault();
                 if (single != null)
                 {
                     MessageBus.Current.SendMessage(OpenInspectorEvent.Create(single, "Search.Selection.Single"));
                 }
            })
            .DisposeWith(_disposables);

        _sessionDownloads.CollectionChanged += (_, _) =>
        {
            HasSessionDownloads = _sessionDownloads.Count > 0;
            RefreshSessionSummary();
        };

        InitializePresentation();

        SyncQualityPresetFromCurrentSettings();
    }

    private async Task ExecuteUnifiedSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        // Import sources (CSV paths, playlist URLs) are routed before any filter/token handling.
        var provider = _importProviders.FirstOrDefault(p => p.CanHandle(SearchQuery));
        if (provider != null)
        {
            ExecuteCancelSearch();
            StatusText = $"Importing via {provider.Name}...";
            await _importOrchestrator.StartImportWithPreviewAsync(provider, SearchQuery);
            StatusText = "Import opened in preview";
            return;
        }

        // Query tokens ("flac", "mp3", "wav", ">256", "320+") become filters and are stripped
        // from the network query. Filters the user set on the strip are only overridden by
        // tokens actually present — searching no longer resets the strip to defaults.
        var parsed = SearchQueryTokenParser.Parse(SearchQuery);
        if (parsed.Formats != null)
        {
            FilterViewModel.FilterFlac = parsed.Formats.Contains("FLAC");
            FilterViewModel.FilterMp3 = parsed.Formats.Contains("MP3");
            FilterViewModel.FilterWav = parsed.Formats.Contains("WAV");
            FilterViewModel.FilterAiff = parsed.Formats.Contains("AIFF");
        }
        if (parsed.MinBitrate.HasValue)
        {
            FilterViewModel.MinBitrate = parsed.MinBitrate.Value;
        }

        string effectiveQuery = string.IsNullOrWhiteSpace(parsed.Query) ? SearchQuery : parsed.Query;

        effectiveQuery = StripExcludedPhrases(effectiveQuery);
        if (string.IsNullOrWhiteSpace(effectiveQuery))
        {
            ReportSearchError("The query only contains phrases the server excludes.");
            return;
        }

        if (!string.Equals(effectiveQuery, SearchQuery, StringComparison.Ordinal))
        {
            SearchQuery = effectiveQuery;
        }

        var peer = IsPeerScope ? PeerUsername?.Trim() : null;
        if (IsPeerScope && string.IsNullOrWhiteSpace(peer))
        {
            ReportSearchError("Enter a username to search one peer's shares.");
            return;
        }

        AddRecentSearch(effectiveQuery);

        var cts = BeginNewSearchSession();
        var sessionId = _currentSearchSessionId;

        IsSearching = true;
        IsListening = true;
        HasSearched = true;
        SearchErrorText = null;
        StatusText = peer != null ? $"Searching {peer}'s shares…" : "Searching the network…";
        _searchResults.Clear();
        SelectedResults.Clear();
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
                    _respondedPeers.Add(item.User);
                }

                _searchResults.AddRange(batch);

                HiddenResultsCount = _publicSearchResults.Count(vm => vm.IsFilteredOut);
                if (!ShowFilteredOutResults && HiddenResultsCount > 0 && SearchResultsView.Count == 0)
                {
                    ShowFilteredOutResults = true;
                }

                TotalResultsReceived += batch.Count;
                PeersResponded = _respondedPeers.Count;
                LastResultAtUtc = DateTime.UtcNow;
                var elapsedMs = Math.Max(1.0, (LastResultAtUtc.Value - windowStartAt).TotalMilliseconds);
                ResultsPerSecond = Math.Round(batch.Count * 1000.0 / elapsedMs, 1);
                windowStartAt = LastResultAtUtc.Value;
                StatusText = NumFormat.Ui($"Found {TotalResultsReceived} files from {PeersResponded} peers (streaming at {ResultsPerSecond:0.#} results/sec)");
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
                    StatusText = $"Found {TotalResultsReceived} files from {PeersResponded} peers (stream idle)";
                }
            });

        try
        {
            try
            {
                if (peer != null)
                {
                    // SearchScope.User: one targeted search of a single peer's shares.
                    var tracks = await _searchOrchestration.SearchUserForTrackAsync(
                        peer,
                        effectiveQuery,
                        PreferredFormats,
                        (MinBitrate > 0 ? MinBitrate : null, MaxBitrate > 0 ? MaxBitrate : null),
                        PeerSearchTimeoutMs,
                        cts.Token);
                    foreach (var track in tracks)
                    {
                        if (_currentSearchSessionId != sessionId) break;
                        incomingTrackStream.OnNext(track);
                    }
                }
                else
                {
                    await foreach (var track in _searchOrchestration.SearchAsync(
                        query: effectiveQuery,
                        preferredFormats: string.Join(",", PreferredFormats),
                        minBitrate: MinBitrate,
                        maxBitrate: MaxBitrate,
                        isAlbumSearch: IsAlbumSearch,
                        maxResultsPerLane: 200,
                        progressiveYield: true,
                        cancellationToken: cts.Token))
                    {
                        if (_currentSearchSessionId != sessionId)
                        {
                            break;
                        }

                        incomingTrackStream.OnNext(track);
                    }
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
            catch (SearchLimitExceededException)
            {
                incomingTrackStream.OnCompleted();
                throw;
            }
            catch (Exception ex)
            {
                // Any other exception ending the stream must still complete it — otherwise
                // streamDrainTcs never resolves and the await below hangs forever (IsSearching
                // stuck true, Cancel can't recover).
                incomingTrackStream.OnCompleted();
                _logger.LogError(ex, "Search stream failed");
                if (_currentSearchSessionId == sessionId)
                {
                    SearchErrorText = ex.Message;
                    StatusText = $"Search failed: {ex.Message}";
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
                        if (peer != null)
                        {
                            // Peer results don't pass through the network ranking pipeline.
                            RecalculateScores();
                        }

                        ApplyPercentileScoring();
                        ResultsPerSecond = 0;

                        if (DisplayedResultsCount == 0 && HiddenResultsCount > 0)
                        {
                            ShowFilteredOutResults = true;
                            StatusText = $"Found {TotalResultsReceived} files (all hidden by filters; showing them dimmed)";
                        }
                        else
                        {
                            StatusText = $"Found {TotalResultsReceived} files from {PeersResponded} peers (stream idle)";
                        }
                    }
                    else if (SearchErrorText == null && StatusText != "Stopped listening")
                    {
                        StatusText = "No results found";
                    }

                    EndActiveSearchSession(cancelNetwork: false, disposeSubscription: false);
                    RaiseResultStateChanged();
                }
            }
        }
        catch (SearchLimitExceededException ex)
        {
            IsSearching = false;
            IsListening = false;
            ResultsPerSecond = 0;
            EndActiveSearchSession(cancelNetwork: true);
            StatusText = $"Showing the first {SLSKDONET.Utils.CountFormat.N(ex.HardResultCap)} results. Use a more specific search.";
            RaiseResultStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            IsSearching = false;
            IsListening = false;
            EndActiveSearchSession(cancelNetwork: true);
            ReportSearchError(ex.Message);
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

    private async Task ExecuteBrowseUserSharesAsync(object? parameter)
    {
        // The context menu can't bind VirtualGrid.SelectedItem (a plain CLR property, read once
        // at bind time = null), so fall back to the current selection.
        var vm = parameter as AnalyzedSearchResultViewModel ?? SelectedResults.FirstOrDefault();
        if (vm == null)
        {
            StatusText = "Select a result first.";
            return;
        }

        StatusText = $"Opening {vm.User}'s collection...";
        await UserCollectionBrowser.LoadUserAsync(vm.User);
        IsUserCollectionBrowserOpen = true;
    }

    private async Task ExecuteAddSelectedToPlaylistAsync()
    {
        var selected = SelectedResults.ToList();
        if (selected.Count == 0)
        {
            StatusText = "Select one or more search results to stage into a mix.";
            return;
        }

        var tracks = selected
            .Select(result => ConvertToPlaylistTrack(result.RawResult.Model))
            .Where(track => track != null)
            .Cast<PlaylistTrack>()
            .ToList();

        if (tracks.Count == 0)
        {
            StatusText = "No valid tracks available to add to a mix.";
            return;
        }

        foreach (var result in selected)
        {
            result.RawResult.IsAddedToProject = true;
        }

        _eventBus.Publish(new AddToProjectRequestEvent(tracks));
        StatusText = tracks.Count == 1
            ? "1 result sent to Add to Mix."
            : $"{tracks.Count} results sent to Add to Mix.";

        await Task.CompletedTask;
    }

    public static PlaylistTrack? ConvertToPlaylistTrack(Track? track)
    {
        if (track == null)
        {
            return null;
        }

        var filePath = track.FilePath ?? track.LocalPath ?? track.Filename ?? string.Empty;

        return new PlaylistTrack
        {
            TrackUniqueHash = track.UniqueHash,
            Artist = track.Artist ?? "Unknown Artist",
            Title = track.Title ?? Path.GetFileNameWithoutExtension(track.Filename) ?? "Unknown Track",
            Album = track.Album ?? string.Empty,
            SourcePlaylistName = track.SourceTitle ?? "Search Results",
            SourceProvenance = track.Username,
            ResolvedFilePath = filePath,
            Format = track.Format,
            CanonicalDuration = track.CanonicalDuration ?? (track.Length.HasValue ? track.Length.Value * 1000 : null),
            Bitrate = track.Bitrate > 0 ? track.Bitrate : null,
            BPM = track.BPM,
            MusicalKey = track.MusicalKey,
            Energy = track.Energy,
            AlbumArtUrl = track.AlbumArtUrl,
            SpotifyTrackId = track.SpotifyTrackId,
            SpotifyAlbumId = track.SpotifyAlbumId,
            SpotifyArtistId = track.SpotifyArtistId,
            Genres = track.Genres,
            Status = TrackStatus.Missing,
            IsFlagged = track.IsFlagged,
            FlagReason = track.FlagReason
        };
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
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Search session token was already disposed");
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

        // Was Completed/Failed only — a queued or in-flight download showed as "not downloaded".
        result.Status = MapDownloadState(existing.State);
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

        if (!toDownload.Any())
        {
            StatusText = "Select one or more results to download.";
            return;
        }

        if (_bulkCoordinator.IsRunning)
        {
            StatusText = "Another bulk operation is running. Try again when it finishes.";
            return;
        }

        var result = await _bulkCoordinator.RunOperationAsync(
            toDownload,
            (vm, ct) =>
            {
                var queued = _downloadManager.EnqueueTrack(vm.RawResult.Model, priority: 0);
                vm.RawResult.Status = queued ? TrackStatus.Pending : TrackStatus.Failed;
                if (queued && !_sessionDownloads.Contains(vm))
                    _sessionDownloads.Add(vm);
                return Task.FromResult(queued);
            },
            "Batch Download"
        );

        RefreshSessionSummary();
        var queuedCount = result?.SuccessCount ?? toDownload.Count(vm => vm.IsQueued);
        var failedCount = toDownload.Count - queuedCount;
        StatusText = failedCount == 0
            ? $"Queued {queuedCount} download{(queuedCount == 1 ? "" : "s")}"
            : $"Queued {queuedCount} of {toDownload.Count} downloads; {failedCount} rejected";

        if (failedCount > 0)
        {
            // Never report success when part of the batch was rejected (format/bitrate rules).
            _eventBus.Publish(new NotificationEvent(
                "Some downloads were rejected",
                $"{failedCount} of {toDownload.Count} selected results did not pass the download rules (format or bitrate). See the status banner for the reason.",
                NotificationType.Warning));
        }
    }

    public void ResetState()
    {
        ExecuteCancelSearch();
        SearchQuery = "";
        IsSearching = false;
        IsUserCollectionBrowserOpen = false;
        _searchResults.Clear();
        SelectedResults.Clear();
        ResetTelemetry();
        HasSearched = false;
        SearchErrorText = null;
        StatusText = "Ready";
        RaiseResultStateChanged();
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
                    FilterViewModel.FilterAiff = true;
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
                    FilterViewModel.FilterAiff = true;
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
                    FilterViewModel.FilterAiff = true;
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
                    FilterViewModel.FilterAiff = true;
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
        this.RaisePropertyChanged(nameof(IsAiffEnabled));

        OnRankingWeightsChanged();
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
            IsWavEnabled &&
            IsAiffEnabled)
        {
            return 0;
        }

        if (Math.Abs(BitrateWeight - 1.0) < 0.001 &&
            Math.Abs(ReliabilityWeight - 1.0) < 0.001 &&
            Math.Abs(MatchWeight - 1.0) < 0.001 &&
            IsFlacEnabled &&
            IsMp3Enabled &&
            IsWavEnabled &&
            IsAiffEnabled)
        {
            return 1;
        }

        if (Math.Abs(BitrateWeight - 0.5) < 0.001 &&
            Math.Abs(ReliabilityWeight - 0.5) < 0.001 &&
            Math.Abs(MatchWeight - 2.0) < 0.001 &&
            !IsFlacEnabled &&
            IsMp3Enabled &&
            IsWavEnabled &&
            IsAiffEnabled)
        {
            return 2;
        }

        if (Math.Abs(BitrateWeight - 2.0) < 0.001 &&
            Math.Abs(ReliabilityWeight - 1.0) < 0.001 &&
            Math.Abs(MatchWeight - 1.0) < 0.001 &&
            IsFlacEnabled &&
            !IsMp3Enabled &&
            IsWavEnabled &&
            IsAiffEnabled)
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
            FilterViewModel.FilterAiff = true;
        });

        StatusText = "Relaxed filters for the cached result set. Hidden matches are now visible without a new network search.";
    }

    private void OnTrackStateChanged(TrackStateChangedEvent evt)
    {
        if (_searchResults.Count == 0 && _sessionDownloads.Count == 0) return;
        Dispatcher.UIThread.Post(() => ApplyDownloadState(evt.TrackGlobalId, evt.State));
    }

    /// <summary>
    /// Reflects a DownloadManager state change onto matching search results and the session
    /// queue. DownloadContext.GlobalId is the queued PlaylistTrack's TrackUniqueHash, which
    /// EnqueueTrack copies from the search result's Track.UniqueHash.
    /// </summary>
    public void ApplyDownloadState(string trackGlobalId, PlaylistTrackState state)
    {
        if (string.IsNullOrEmpty(trackGlobalId)) return;
        var status = MapDownloadState(state);

        foreach (var result in _searchResults.Items.Concat(_sessionDownloads).Distinct())
        {
            if (result.RawResult.Model.UniqueHash == trackGlobalId)
            {
                result.RawResult.Status = status;
            }
        }

        RefreshSessionSummary();
    }

    public static TrackStatus MapDownloadState(PlaylistTrackState state) => state switch
    {
        PlaylistTrackState.Completed => TrackStatus.Downloaded,
        PlaylistTrackState.Failed => TrackStatus.Failed,
        PlaylistTrackState.Cancelled => TrackStatus.Missing,
        _ => TrackStatus.Pending // Pending, Searching, Queued, Downloading, Converting, Paused, Stalled...
    };

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


    /// <summary>Routes a command's failures to the status bar + a toast, tied to this VM's lifetime.</summary>
    private void ObserveCommandFailures(IObservable<Exception> thrownExceptions)
    {
        thrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ReportCommandFailure)
            .DisposeWith(_disposables);
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
        // RecalculateScores() + the config save both run debounced — see the
        // _rankingRefreshRequests subscription wired up in the constructor.
        _rankingRefreshRequests.OnNext(Unit.Default);
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

        _resortRequests.OnNext(Unit.Default);
    }
    
    private List<string> GetActiveFormats()
    {
        var list = new List<string>();
        if (IsFlacEnabled) list.Add("flac");
        if (IsMp3Enabled) list.Add("mp3");
        if (IsWavEnabled) list.Add("wav");
        if (IsAiffEnabled) { list.Add("aiff"); list.Add("aif"); }
        if (list.Count == 0) list.AddRange(new[] { "mp3", "flac", "wav", "aiff", "aif" });
        return list;
    }

}

