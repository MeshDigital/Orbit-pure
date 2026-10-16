using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>Sortable columns on the Search results list.</summary>
public enum SearchSortColumn
{
    Match,
    Title,
    Quality,
    Length,
    Size,
    Peer
}

/// <summary>
/// Page-state for the ad-hoc Search page: scope (network / one peer), recent searches, column
/// sorting, the idle / searching / no-results / error states, the session download summary and
/// the small row actions. Kept apart from the search pipeline in SearchViewModel.cs.
/// </summary>
public partial class SearchViewModel : IDisposable
{
    private const int MaxRecentSearches = 8;
    private const int PeerSearchTimeoutMs = 15000;

    private readonly BehaviorSubject<IComparer<AnalyzedSearchResultViewModel>> _sortComparerChanges =
        new(BuildComparer(SearchSortColumn.Match, descending: true));
    private readonly Subject<Unit> _resortRequests = new();
    private readonly HashSet<string> _respondedPeers = new(StringComparer.OrdinalIgnoreCase);

    // ───────────── Scope ─────────────

    private bool _isPeerScope;
    /// <summary>True = search one peer's shares (SearchScope.User); false = whole network.</summary>
    public bool IsPeerScope
    {
        get => _isPeerScope;
        set
        {
            if (SetProperty(ref _isPeerScope, value))
                this.RaisePropertyChanged(nameof(IsNetworkScope));
        }
    }

    public bool IsNetworkScope
    {
        get => !_isPeerScope;
        set => IsPeerScope = !value;
    }

    private string _peerUsername = string.Empty;
    public string PeerUsername
    {
        get => _peerUsername;
        set => SetProperty(ref _peerUsername, value ?? string.Empty);
    }

    // ───────────── Recent searches (session) ─────────────

    public ObservableCollection<string> RecentSearches { get; } = new();
    public bool HasRecentSearches => RecentSearches.Count > 0;

    internal void AddRecentSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var trimmed = query.Trim();
        var existing = RecentSearches.FirstOrDefault(q => string.Equals(q, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing != null) RecentSearches.Remove(existing);
        RecentSearches.Insert(0, trimmed);
        while (RecentSearches.Count > MaxRecentSearches)
            RecentSearches.RemoveAt(RecentSearches.Count - 1);
    }

    // ───────────── Result states ─────────────

    private bool _hasSearched;
    public bool HasSearched
    {
        get => _hasSearched;
        private set
        {
            if (SetProperty(ref _hasSearched, value)) RaiseResultStateChanged();
        }
    }

    private string? _searchErrorText;
    public string? SearchErrorText
    {
        get => _searchErrorText;
        private set
        {
            if (SetProperty(ref _searchErrorText, value))
            {
                this.RaisePropertyChanged(nameof(HasSearchError));
                RaiseResultStateChanged();
            }
        }
    }

    public bool HasSearchError => !string.IsNullOrEmpty(SearchErrorText);

    private int _peersResponded;
    public int PeersResponded
    {
        get => _peersResponded;
        private set => SetProperty(ref _peersResponded, value);
    }

    /// <summary>Idle: nothing searched yet (hint + recent searches).</summary>
    public bool ShowNeverSearchedEmptyState => !IsSearching && !HasDisplayedResults && !HasSearched && !HasSearchError;
    /// <summary>Searching, no visible rows yet (live progress + cancel).</summary>
    public bool ShowActivelySearchingEmptyState => IsSearching && !HasDisplayedResults;
    /// <summary>Finished, the network returned nothing.</summary>
    public bool ShowNoResultsState => HasSearched && !IsSearching && !HasSearchError && TotalResultsReceived == 0;
    /// <summary>Finished with results, but every one is hidden by filters.</summary>
    public bool ShowAllFilteredState => !IsSearching && !HasSearchError && TotalResultsReceived > 0 && !HasDisplayedResults;
    /// <summary>The search failed and there's nothing to show.</summary>
    public bool ShowErrorState => HasSearchError && !IsSearching && !HasDisplayedResults;
    /// <summary>The column header only makes sense with rows under it.</summary>
    public bool ShowResultsHeader => HasDisplayedResults;

    private void RaiseResultStateChanged()
    {
        this.RaisePropertyChanged(nameof(ShowNeverSearchedEmptyState));
        this.RaisePropertyChanged(nameof(ShowActivelySearchingEmptyState));
        this.RaisePropertyChanged(nameof(ShowNoResultsState));
        this.RaisePropertyChanged(nameof(ShowAllFilteredState));
        this.RaisePropertyChanged(nameof(ShowErrorState));
        this.RaisePropertyChanged(nameof(ShowResultsHeader));
    }

    private void ReportSearchError(string message)
    {
        SearchErrorText = message;
        StatusText = $"Search failed: {message}";
        RaiseResultStateChanged();
    }

    private void ReportCommandFailure(Exception ex)
    {
        _logger.LogError(ex, "Search page command failed");
        StatusText = $"Action failed: {ex.Message}";
        _eventBus.Publish(new NotificationEvent("Search action failed", ex.Message, NotificationType.Error));
    }

    // ───────────── Selection ─────────────

    public string SelectionCountText => SelectedResults.Count == 1 ? "1 selected" : $"{SelectedResults.Count} selected";

    // ───────────── Sorting ─────────────

    private SearchSortColumn _sortColumn = SearchSortColumn.Match;
    public SearchSortColumn SortColumn
    {
        get => _sortColumn;
        private set => SetProperty(ref _sortColumn, value);
    }

    private bool _sortDescending = true;
    public bool SortDescending
    {
        get => _sortDescending;
        private set => SetProperty(ref _sortDescending, value);
    }

    public bool IsSortedByMatch => SortColumn == SearchSortColumn.Match;
    public bool IsSortedByTitle => SortColumn == SearchSortColumn.Title;
    public bool IsSortedByQuality => SortColumn == SearchSortColumn.Quality;
    public bool IsSortedByLength => SortColumn == SearchSortColumn.Length;
    public bool IsSortedBySize => SortColumn == SearchSortColumn.Size;
    public bool IsSortedByPeer => SortColumn == SearchSortColumn.Peer;

    /// <summary>Clicking the active column flips direction; a new column starts in its natural direction.</summary>
    public void SortBy(SearchSortColumn column)
    {
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            // Text sorts A→Z first; numbers/scores sort best/largest first.
            SortDescending = column != SearchSortColumn.Title;
        }

        this.RaisePropertyChanged(nameof(IsSortedByMatch));
        this.RaisePropertyChanged(nameof(IsSortedByTitle));
        this.RaisePropertyChanged(nameof(IsSortedByQuality));
        this.RaisePropertyChanged(nameof(IsSortedByLength));
        this.RaisePropertyChanged(nameof(IsSortedBySize));
        this.RaisePropertyChanged(nameof(IsSortedByPeer));
        _sortComparerChanges.OnNext(BuildComparer(SortColumn, SortDescending));
    }

    internal static IComparer<AnalyzedSearchResultViewModel> BuildComparer(SearchSortColumn column, bool descending)
    {
        Comparison<AnalyzedSearchResultViewModel> primary = column switch
        {
            SearchSortColumn.Title => (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase),
            SearchSortColumn.Quality => (a, b) =>
            {
                var tier = a.IsLossless.CompareTo(b.IsLossless);
                return tier != 0 ? tier : a.BitRate.CompareTo(b.BitRate);
            },
            SearchSortColumn.Length => (a, b) => (a.Length ?? 0).CompareTo(b.Length ?? 0),
            SearchSortColumn.Size => (a, b) => a.Size.CompareTo(b.Size),
            // "Best peer" = free slot, then shortest queue, then fastest.
            SearchSortColumn.Peer => (a, b) =>
            {
                var slot = a.SlotFree.CompareTo(b.SlotFree);
                if (slot != 0) return slot;
                var queue = b.QueueLength.CompareTo(a.QueueLength);
                return queue != 0 ? queue : a.UploadSpeed.CompareTo(b.UploadSpeed);
            },
            _ => (a, b) => a.MatchConfidence.CompareTo(b.MatchConfidence)
        };

        return Comparer<AnalyzedSearchResultViewModel>.Create((a, b) =>
        {
            var result = primary(a, b);
            if (descending) result = -result;
            // Stable tie-break so equal rows don't shuffle on every resort.
            return result != 0 ? result : string.Compare(a.FullPath, b.FullPath, StringComparison.Ordinal);
        });
    }

    // ───────────── Session downloads ─────────────

    private string _sessionSummaryText = string.Empty;
    public string SessionSummaryText
    {
        get => _sessionSummaryText;
        private set => SetProperty(ref _sessionSummaryText, value);
    }

    internal void RefreshSessionSummary()
    {
        var queued = _sessionDownloads.Count(d => d.IsQueued);
        var done = _sessionDownloads.Count(d => d.IsDownloaded);
        var failed = _sessionDownloads.Count(d => d.IsDownloadFailed);
        var parts = new List<string>();
        if (queued > 0) parts.Add($"{queued} queued");
        if (done > 0) parts.Add($"{done} downloaded");
        if (failed > 0) parts.Add($"{failed} failed");
        SessionSummaryText = parts.Count == 0 ? string.Empty : "This session: " + string.Join(" · ", parts);
    }

    // ───────────── Commands ─────────────

    public ICommand SortByCommand { get; private set; } = null!;
    public ICommand ClearFiltersCommand { get; private set; } = null!;
    public ICommand RunRecentSearchCommand { get; private set; } = null!;
    public ICommand ClearRecentSearchesCommand { get; private set; } = null!;
    public ICommand SearchPeerCommand { get; private set; } = null!;
    public ICommand CopyPathCommand { get; private set; } = null!;
    public ICommand OpenDownloadsCommand { get; private set; } = null!;
    public ICommand SetMinBitrateCommand { get; private set; } = null!;

    /// <summary>"Any" / "320+ kbps" label for the min-bitrate dropdown.</summary>
    public string MinBitrateLabel => MinBitrate > 0 ? $"{MinBitrate}+ kbps" : "Any bitrate";

    private void InitializePresentation()
    {
        _sortComparerChanges.DisposeWith(_disposables);
        _resortRequests.DisposeWith(_disposables);

        SortByCommand = ReactiveCommand.Create<object?>(p =>
        {
            if (p is SearchSortColumn c) SortBy(c);
            else if (p is string s && Enum.TryParse<SearchSortColumn>(s, true, out var parsed)) SortBy(parsed);
        });

        ClearFiltersCommand = ReactiveCommand.Create(ClearFilters);

        RunRecentSearchCommand = ReactiveCommand.CreateFromTask<object?>(async p =>
        {
            if (p is not string query || string.IsNullOrWhiteSpace(query)) return;
            SearchQuery = query;
            await ExecuteUnifiedSearchAsync();
        });

        ClearRecentSearchesCommand = ReactiveCommand.Create(() => RecentSearches.Clear());

        var searchPeerCmd = ReactiveCommand.CreateFromTask<object?>(async p =>
        {
            var vm = p as AnalyzedSearchResultViewModel ?? SelectedResults.FirstOrDefault();
            if (vm == null || string.IsNullOrWhiteSpace(vm.User))
            {
                StatusText = "Select a result first.";
                return;
            }

            PeerUsername = vm.User;
            IsPeerScope = true;
            await ExecuteUnifiedSearchAsync();
        });
        searchPeerCmd.ThrownExceptions.Subscribe(ReportCommandFailure).DisposeWith(_disposables);
        SearchPeerCommand = searchPeerCmd;

        var copyPathCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var selected = SelectedResults.ToList();
            if (selected.Count == 0)
            {
                StatusText = "Select one or more results to copy.";
                return;
            }

            var text = string.Join(Environment.NewLine, selected.Select(r => $"{r.User}: {r.FullPath}"));
            await _clipboardService.SetTextAsync(text);
            StatusText = selected.Count == 1 ? "Copied file path." : $"Copied {selected.Count} file paths.";
        });
        copyPathCmd.ThrownExceptions.Subscribe(ReportCommandFailure).DisposeWith(_disposables);
        CopyPathCommand = copyPathCmd;

        OpenDownloadsCommand = ReactiveCommand.Create(() => _navigationService?.NavigateTo(PageType.Projects));

        SetMinBitrateCommand = ReactiveCommand.Create<object?>(p =>
        {
            if (p is int i) MinBitrate = i;
            else if (p is string s && int.TryParse(s, out var parsed)) MinBitrate = parsed;
        });

        RecentSearches.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasRecentSearches));
        SelectedResults.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(SelectionCountText));

        // Query tokens and Clear change FilterViewModel directly; keep the chips (bound to the
        // pass-through properties here) in sync, and re-rank when the format set changes.
        FilterViewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SearchFilterViewModel.FilterFlac):
                    this.RaisePropertyChanged(nameof(IsFlacEnabled));
                    break;
                case nameof(SearchFilterViewModel.FilterMp3):
                    this.RaisePropertyChanged(nameof(IsMp3Enabled));
                    break;
                case nameof(SearchFilterViewModel.FilterWav):
                    this.RaisePropertyChanged(nameof(IsWavEnabled));
                    break;
                case nameof(SearchFilterViewModel.MinBitrate):
                    this.RaisePropertyChanged(nameof(MinBitrate));
                    this.RaisePropertyChanged(nameof(MinBitrateLabel));
                    break;
            }
        };
    }

    /// <summary>The filter strip's conditional "Clear": every filter back to default, cached
    /// results re-filtered instantly (no new network search).</summary>
    private void ClearFilters()
    {
        FilterViewModel.ResetToDefaults();
        ShowFilteredOutResults = false;
        OnRankingWeightsChanged();
        SyncQualityPresetFromCurrentSettings();
        StatusText = "Filters cleared.";
    }
}
