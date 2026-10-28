using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Import;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>Which rows of the preview list are shown. Each value is one chip in the summary strip.</summary>
public enum ImportStatusFilter { All, New, Owned, InPlaylist, Queued, NoMatch }

/// <summary>Why the preview thinks an existing playlist might be the right destination.</summary>
public enum ImportSuggestionReason { None, SameSource, SameName, HighOverlap }

/// <summary>
/// The import preview: what came in from the source, what each row will do, where it goes
/// (a new playlist or an existing one) and one commit button that does exactly what the footer
/// sentence says.
///
/// Flow: <see cref="InitializeStreamingPreview"/> → <see cref="AddTracksToPreviewAsync"/> per
/// streamed batch → <see cref="CompleteLoading"/> (or <see cref="ShowError"/>) → the user picks a
/// destination and rows → <see cref="AddToLibraryCommand"/> queues it through
/// <see cref="IImportQueueGateway"/> (DownloadManager.QueueProject) and raises
/// <see cref="AddedToLibrary"/>.
///
/// Row status ("Owned", "In playlist", …) uses the same matching rules as QueueProject
/// (Services/Import/ImportMatching.cs), so what the preview promises is what the commit does.
/// </summary>
public class ImportPreviewViewModel : INotifyPropertyChanged
{
    private const double OverlapSuggestionRatio = 0.6;
    private const int OverlapSuggestionMinTracks = 3;
    private const int OverlapCandidateCount = 8;

    private readonly ILogger<ImportPreviewViewModel> _logger;
    private readonly IImportQueueGateway _queue;
    private readonly ILibraryService? _libraryService;
    private readonly ISpotifyMetadataService? _metadataService;

    // ── Per-preview session state (reset by InitializeStreamingPreview) ──────────
    private int _session;
    private Task<LibraryMatchIndex>? _libraryIndexTask;
    private readonly Dictionary<Guid, PlaylistMembershipIndex> _membershipCache = new();
    private PlaylistMembershipIndex? _targetMembership;
    private Guid _newJobId;
    private PlaylistJob? _sameSourceJob;
    private bool _hasCommitted;
    private bool _playlistNameEdited;
    private bool _bulkSelecting;
    private bool _closed; // cancelled: ignore batches until the next InitializeStreamingPreview

    public ImportPreviewViewModel(
        ILogger<ImportPreviewViewModel> logger,
        DownloadManager downloadManager,
        INavigationService navigationService,
        ILibraryService? libraryService = null,
        ISpotifyMetadataService? metadataService = null)
        : this(logger, new DownloadManagerImportQueueGateway(downloadManager), navigationService, libraryService, metadataService)
    {
    }

    internal ImportPreviewViewModel(
        ILogger<ImportPreviewViewModel> logger,
        IImportQueueGateway queue,
        INavigationService navigationService,
        ILibraryService? libraryService = null,
        ISpotifyMetadataService? metadataService = null)
    {
        _logger = logger;
        _queue = queue;
        _libraryService = libraryService;
        _metadataService = metadataService;

        AddToLibraryCommand = new AsyncRelayCommand(AddToLibraryAsync, () => CanAddToLibrary);
        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        SelectMissingCommand = new RelayCommand(SelectMissing);
        ResetSelectionCommand = new RelayCommand(ResetSelection);
        CancelCommand = new RelayCommand(Cancel, () => !IsCommitting);
        AcceptSuggestionCommand = new RelayCommand(AcceptSuggestion);
        ImportAsNewCommand = new RelayCommand(ImportAsNew);
        UseCollidingPlaylistCommand = new RelayCommand(UseCollidingPlaylist);
        ShowAllTracksCommand = new RelayCommand(() => StatusFilter = ImportStatusFilter.All);
        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke(this, EventArgs.Empty));
        ReconnectCommand = new RelayCommand(() => ReconnectRequested?.Invoke(this, EventArgs.Empty));
    }

    // ═════════════════════════════════ Source ═════════════════════════════════

    private string _sourceTitle = "Import";
    /// <summary>The source's own name (playlist / album / file / detected tracklist title).</summary>
    public string SourceTitle
    {
        get => _sourceTitle;
        set
        {
            if (!Set(ref _sourceTitle, value ?? string.Empty)) return;
            if (!_playlistNameEdited) SetPlaylistName(_sourceTitle, userEdit: false);
            OnPropertyChanged(nameof(SourceSubtitle));
        }
    }

    private string _sourceType = string.Empty;
    public string SourceType
    {
        get => _sourceType;
        set
        {
            if (!Set(ref _sourceType, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(SourceSubtitle));
            OnPropertyChanged(nameof(IsSpotifySource));
            OnPropertyChanged(nameof(IsCsvSource));
            OnPropertyChanged(nameof(IsTextSource));
        }
    }

    private string? _sourceOwner;
    /// <summary>Playlist owner / album artist when the source reports one.</summary>
    public string? SourceOwner
    {
        get => _sourceOwner;
        set { if (Set(ref _sourceOwner, value)) OnPropertyChanged(nameof(SourceSubtitle)); }
    }

    private string? _sourceUrl;
    /// <summary>
    /// URL (or file path) identifying the source, persisted onto the created playlist so it can be
    /// re-checked later. Providers that detect a more specific URL while parsing (a 1001Tracklists
    /// backlink inside a pasted tracklist) override it via <see cref="UpdateSourceUrl"/>.
    /// </summary>
    public string? SourceUrl
    {
        get => _sourceUrl;
        private set => Set(ref _sourceUrl, value);
    }

    public void UpdateSourceUrl(string sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl)) SourceUrl = sourceUrl;
    }

    public bool IsSpotifySource => SourceType.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
    public bool IsCsvSource => SourceType.Contains("CSV", StringComparison.OrdinalIgnoreCase);
    public bool IsTextSource => !IsSpotifySource && !IsCsvSource;

    /// <summary>"Spotify · by Alex · 37 tracks".</summary>
    public string SourceSubtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(SourceType)) parts.Add(SourceType);
            if (!string.IsNullOrWhiteSpace(SourceOwner)) parts.Add($"by {SourceOwner}");
            parts.Add(Plural(TotalCount, "track"));
            return string.Join(" · ", parts);
        }
    }

    // ═════════════════════════════════ Rows ═════════════════════════════════

    private ObservableCollection<SelectableTrack> _importedTracks = new();
    /// <summary>Every row from the source, in source order.</summary>
    public ObservableCollection<SelectableTrack> ImportedTracks
    {
        get => _importedTracks;
        private set => Set(ref _importedTracks, value);
    }

    private ObservableCollection<SelectableTrack> _visibleTracks = new();
    /// <summary>The rows matching <see cref="StatusFilter"/>; what the list shows.</summary>
    public ObservableCollection<SelectableTrack> VisibleTracks
    {
        get => _visibleTracks;
        private set => Set(ref _visibleTracks, value);
    }

    public int TrackCount => TotalCount;

    // ── Summary counts (all rows) ─────────────────────────────────────────────
    private int _totalCount, _newCount, _ownedCount, _inPlaylistCount, _queuedCount, _noMatchCount;
    public int TotalCount { get => _totalCount; private set => Set(ref _totalCount, value); }
    /// <summary>Will be downloaded: New + Similar + Retry.</summary>
    public int NewCount { get => _newCount; private set => Set(ref _newCount, value); }
    public int OwnedCount { get => _ownedCount; private set => Set(ref _ownedCount, value); }
    public int InPlaylistCount { get => _inPlaylistCount; private set => Set(ref _inPlaylistCount, value); }
    public int QueuedCount { get => _queuedCount; private set => Set(ref _queuedCount, value); }
    /// <summary>Can't be imported: missing title, or a repeat of an earlier row.</summary>
    public int NoMatchCount { get => _noMatchCount; private set => Set(ref _noMatchCount, value); }

    public bool HasInPlaylist => InPlaylistCount > 0;
    public bool HasQueued => QueuedCount > 0;
    public bool HasNoMatch => NoMatchCount > 0;

    // ── Selection plan (selected rows only) ───────────────────────────────────
    private int _selectedCount, _selectedDownloadCount, _selectedOwnedCount, _selectedQueuedCount, _selectedInPlaylistCount;
    public int SelectedCount
    {
        get => _selectedCount;
        private set { if (Set(ref _selectedCount, value)) RaiseCommitStateChanged(); }
    }
    public int SelectedDownloadCount { get => _selectedDownloadCount; private set => Set(ref _selectedDownloadCount, value); }
    public int SelectedOwnedCount { get => _selectedOwnedCount; private set => Set(ref _selectedOwnedCount, value); }
    public int SelectedQueuedCount { get => _selectedQueuedCount; private set => Set(ref _selectedQueuedCount, value); }
    public int SelectedInPlaylistCount { get => _selectedInPlaylistCount; private set => Set(ref _selectedInPlaylistCount, value); }

    /// <summary>Selected rows that will actually change the destination playlist.</summary>
    public int EffectiveSelectedCount => SelectedDownloadCount + SelectedOwnedCount + SelectedQueuedCount;

    // ── Filter chips ──────────────────────────────────────────────────────────
    private ImportStatusFilter _statusFilter = ImportStatusFilter.All;
    public ImportStatusFilter StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (!Set(ref _statusFilter, value)) return;
            RaiseFilterFlags();
            RefreshVisibleTracks();
        }
    }

    private void RaiseFilterFlags()
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterNew));
        OnPropertyChanged(nameof(IsFilterOwned));
        OnPropertyChanged(nameof(IsFilterInPlaylist));
        OnPropertyChanged(nameof(IsFilterQueued));
        OnPropertyChanged(nameof(IsFilterNoMatch));
    }

    public bool IsFilterAll { get => StatusFilter == ImportStatusFilter.All; set { if (value) StatusFilter = ImportStatusFilter.All; } }
    public bool IsFilterNew { get => StatusFilter == ImportStatusFilter.New; set { if (value) StatusFilter = ImportStatusFilter.New; } }
    public bool IsFilterOwned { get => StatusFilter == ImportStatusFilter.Owned; set { if (value) StatusFilter = ImportStatusFilter.Owned; } }
    public bool IsFilterInPlaylist { get => StatusFilter == ImportStatusFilter.InPlaylist; set { if (value) StatusFilter = ImportStatusFilter.InPlaylist; } }
    public bool IsFilterQueued { get => StatusFilter == ImportStatusFilter.Queued; set { if (value) StatusFilter = ImportStatusFilter.Queued; } }
    public bool IsFilterNoMatch { get => StatusFilter == ImportStatusFilter.NoMatch; set { if (value) StatusFilter = ImportStatusFilter.NoMatch; } }

    public bool ShowFilterEmptyState => TotalCount > 0 && VisibleTracks.Count == 0;

    // ═════════════════════════════════ Destination ═════════════════════════════════

    private string _playlistName = string.Empty;
    /// <summary>Name of the new playlist. Follows <see cref="SourceTitle"/> until the user edits it.</summary>
    public string PlaylistName
    {
        get => _playlistName;
        set => SetPlaylistName(value ?? string.Empty, userEdit: true);
    }

    private void SetPlaylistName(string value, bool userEdit)
    {
        if (userEdit) _playlistNameEdited = true;
        if (!Set(ref _playlistName, value, nameof(PlaylistName))) return;
        RaiseNameCollisionChanged();
        RaiseCommitStateChanged();
    }

    private bool _isMergeMode;
    /// <summary>
    /// Destination: true = add to <see cref="SelectedMergeTarget"/>, false = create a new
    /// playlist named <see cref="PlaylistName"/>. Only changes what the commit will do.
    /// </summary>
    public bool IsMergeMode
    {
        get => _isMergeMode;
        set
        {
            if (_isMergeMode == value) return;
            _isMergeMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNewPlaylistDestination));
            OnPropertyChanged(nameof(IsUpdatingSameSource));
            RaiseNameCollisionChanged();
            OnTargetChanged();
        }
    }

    public bool IsNewPlaylistDestination
    {
        get => !_isMergeMode;
        set => IsMergeMode = !value;
    }

    /// <summary>Every playlist that tracks can be added to (smart playlists excluded).</summary>
    public ObservableCollection<MergeTargetOptionViewModel> MergeTargets { get; } = new();

    /// <summary><see cref="MergeTargets"/> filtered by <see cref="TargetSearchText"/>; the selected target always stays in it.</summary>
    public ObservableCollection<MergeTargetOptionViewModel> FilteredMergeTargets { get; } = new();

    public bool HasMergeTargets => MergeTargets.Count > 0;

    private string _targetSearchText = string.Empty;
    public string TargetSearchText
    {
        get => _targetSearchText;
        set { if (Set(ref _targetSearchText, value ?? string.Empty)) RefreshFilteredTargets(); }
    }

    private MergeTargetOptionViewModel? _selectedMergeTarget;
    public MergeTargetOptionViewModel? SelectedMergeTarget
    {
        get => _selectedMergeTarget;
        set
        {
            if (_selectedMergeTarget == value) return;
            _selectedMergeTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUpdatingSameSource));
            if (_isMergeMode) OnTargetChanged();
            else RaiseCommitStateChanged();
        }
    }

    private bool _isMergeTargetLoading;
    /// <summary>True while the chosen playlist's tracks are loaded to work out what's already there.</summary>
    public bool IsMergeTargetLoading
    {
        get => _isMergeTargetLoading;
        private set { if (Set(ref _isMergeTargetLoading, value)) RaiseCommitStateChanged(); }
    }

    /// <summary>True when the destination is the playlist this same source was imported into before.</summary>
    public bool IsUpdatingSameSource =>
        _isMergeMode && _sameSourceJob != null && _selectedMergeTarget?.Job.Id == _sameSourceJob.Id;

    // ── Name collision ────────────────────────────────────────────────────────
    public MergeTargetOptionViewModel? NameCollisionTarget =>
        _isMergeMode || string.IsNullOrWhiteSpace(PlaylistName)
            ? null
            : MergeTargets.FirstOrDefault(t => string.Equals(t.Title.Trim(), PlaylistName.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool ShowNameCollision => NameCollisionTarget is { } c && !(ShowSuggestion && SuggestedTarget?.Job.Id == c.Job.Id);

    public string NameCollisionText => NameCollisionTarget is { } t
        ? $"A playlist named '{t.Title}' already exists. Importing creates a second one."
        : string.Empty;

    private void RaiseNameCollisionChanged()
    {
        OnPropertyChanged(nameof(NameCollisionTarget));
        OnPropertyChanged(nameof(ShowNameCollision));
        OnPropertyChanged(nameof(NameCollisionText));
    }

    // ── Duplicate / update suggestion ─────────────────────────────────────────
    private MergeTargetOptionViewModel? _suggestedTarget;
    public MergeTargetOptionViewModel? SuggestedTarget
    {
        get => _suggestedTarget;
        private set { if (Set(ref _suggestedTarget, value)) RaiseSuggestionChanged(); }
    }

    private ImportSuggestionReason _suggestionReason;
    public ImportSuggestionReason SuggestionReason
    {
        get => _suggestionReason;
        private set { if (Set(ref _suggestionReason, value)) RaiseSuggestionChanged(); }
    }

    private bool _suggestionResolved;
    public bool SuggestionResolved
    {
        get => _suggestionResolved;
        private set { if (Set(ref _suggestionResolved, value)) RaiseSuggestionChanged(); }
    }

    private int? _suggestionNewCount;
    private int _suggestionOverlapCount;

    /// <summary>Tracks from this import that the suggested playlist doesn't have yet (null while unknown).</summary>
    public int? SuggestionNewCount => _suggestionNewCount;

    public bool ShowSuggestion => SuggestedTarget != null && !SuggestionResolved && SuggestionReason != ImportSuggestionReason.None;

    public string SuggestionTitle => SuggestedTarget is not { } t ? string.Empty : SuggestionReason switch
    {
        ImportSuggestionReason.SameSource => $"You imported this before as '{t.Title}'",
        ImportSuggestionReason.SameName => $"A playlist called '{t.Title}' already exists",
        ImportSuggestionReason.HighOverlap => $"'{t.Title}' already has {_suggestionOverlapCount} of these {Plural(ImportableCount, "track")}",
        _ => string.Empty
    };

    public string SuggestionBody
    {
        get
        {
            if (SuggestedTarget is not { } t) return string.Empty;
            var size = Plural(t.Job.TotalTracks, "track");
            if (_suggestionNewCount is not { } fresh)
                return $"{size} there. Checking which of these tracks are new…";
            var freshText = fresh == 0
                ? "none of these tracks are new"
                : $"{fresh} of these {Plural(ImportableCount, "track")} {(fresh == 1 ? "is" : "are")} new";
            return SuggestionReason == ImportSuggestionReason.SameSource
                ? $"{size} there, {freshText}. Updating adds only what's missing."
                : $"{size} there, {freshText}. Update that playlist instead of creating a near-copy?";
        }
    }

    public string AcceptSuggestionLabel => SuggestedTarget is { } t ? $"Update '{Truncate(t.Title, 28)}'" : "Update";

    private void RaiseSuggestionChanged()
    {
        OnPropertyChanged(nameof(ShowSuggestion));
        OnPropertyChanged(nameof(SuggestionTitle));
        OnPropertyChanged(nameof(SuggestionBody));
        OnPropertyChanged(nameof(SuggestionNewCount));
        OnPropertyChanged(nameof(AcceptSuggestionLabel));
        OnPropertyChanged(nameof(ShowNameCollision));
    }

    // ═════════════════════════════════ Load / error state ═════════════════════════════════

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (!Set(ref _isLoading, value)) return;
            RaiseViewStateChanged();
            RaiseCommitStateChanged();
        }
    }

    private int _loadedCount;
    public int LoadedCount { get => _loadedCount; private set { if (Set(ref _loadedCount, value)) RaiseProgressChanged(); } }

    private int _expectedTrackCount;
    /// <summary>Total the source reported up front (0 when unknown).</summary>
    public int ExpectedTrackCount { get => _expectedTrackCount; private set { if (Set(ref _expectedTrackCount, value)) RaiseProgressChanged(); } }

    public bool HasLoadProgress => ExpectedTrackCount > 0;
    public double LoadProgressValue => ExpectedTrackCount <= 0 ? 0 : Math.Min(LoadedCount, ExpectedTrackCount);
    public string LoadProgressText => ExpectedTrackCount > 0
        ? $"Loading tracks… {SLSKDONET.Utils.CountFormat.N(Math.Min(LoadedCount, ExpectedTrackCount))} of {SLSKDONET.Utils.CountFormat.N(ExpectedTrackCount)}"
        : LoadedCount > 0 ? $"Loading tracks… {SLSKDONET.Utils.CountFormat.N(LoadedCount)}" : "Loading tracks…";

    private void RaiseProgressChanged()
    {
        OnPropertyChanged(nameof(HasLoadProgress));
        OnPropertyChanged(nameof(LoadProgressValue));
        OnPropertyChanged(nameof(LoadProgressText));
        if (IsLoading) RaiseCommitStateChanged();
    }

    /// <summary>Called by the orchestrator when a batch reports the source's total size.</summary>
    public void ReportExpectedTotal(int total)
    {
        if (total > 0 && total != ExpectedTrackCount) ExpectedTrackCount = total;
    }

    private bool _hasError;
    public bool HasError { get => _hasError; private set { if (Set(ref _hasError, value)) RaiseViewStateChanged(); } }

    private string _errorMessage = string.Empty;
    public string ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }

    private bool _errorRequiresSignIn;
    public bool ErrorRequiresSignIn { get => _errorRequiresSignIn; private set => Set(ref _errorRequiresSignIn, value); }

    public bool ShowLoadingState => IsLoading && TotalCount == 0 && !HasError;
    public bool ShowErrorState => HasError && TotalCount == 0;
    public bool ShowErrorBanner => HasError && TotalCount > 0;
    public bool ShowEmptyState => !IsLoading && !HasError && TotalCount == 0;
    public bool ShowTrackList => TotalCount > 0;

    /// <summary>Everything importable is already owned: the import only creates the playlist.</summary>
    public bool ShowAllOwnedNotice => !IsLoading && !IsMergeMode && TotalCount > 0 && NewCount == 0 && QueuedCount == 0 && OwnedCount > 0;

    public string AllOwnedText => $"You already own all {Plural(OwnedCount, "track")}. Importing creates the playlist and links your files; nothing needs downloading.";

    public string EmptyStateTitle => IsTextSource ? "No tracks found" : "This playlist is empty";
    public string EmptyStateBody => IsCsvSource
        ? "The file has no rows with an artist and title."
        : IsTextSource ? "Nothing in the pasted text looked like a track." : "There are no tracks to import from this source.";

    private void RaiseViewStateChanged()
    {
        OnPropertyChanged(nameof(ShowLoadingState));
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(ShowErrorBanner));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowTrackList));
        OnPropertyChanged(nameof(ShowAllOwnedNotice));
        OnPropertyChanged(nameof(AllOwnedText));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateBody));
        OnPropertyChanged(nameof(ShowFilterEmptyState));
    }

    /// <summary>Puts the preview into its error state (or a warning banner when some rows already loaded).</summary>
    public void ShowError(string message, bool requiresSignIn = false)
    {
        ErrorMessage = message;
        ErrorRequiresSignIn = requiresSignIn;
        HasError = true;
        IsLoading = false;
        RaiseViewStateChanged();
        RaiseCommitStateChanged();
    }

    // ═════════════════════════════════ Commit ═════════════════════════════════

    private bool _isCommitting;
    public bool IsCommitting
    {
        get => _isCommitting;
        private set
        {
            if (!Set(ref _isCommitting, value)) return;
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseCommitStateChanged();
        }
    }

    private string? _commitError;
    /// <summary>Why the last commit attempt failed (shown in the footer; the user can try again).</summary>
    public string? CommitError
    {
        get => _commitError;
        private set { if (Set(ref _commitError, value)) OnPropertyChanged(nameof(HasCommitError)); }
    }
    public bool HasCommitError => !string.IsNullOrEmpty(CommitError);

    /// <summary>What the last successful commit actually did (from QueueProject).</summary>
    public ProjectQueueResult? LastCommitResult { get; private set; }

    /// <summary>Toast text for the last successful commit, built from <see cref="LastCommitResult"/>.</summary>
    public string LastCommitSummary { get; private set; } = string.Empty;

    /// <summary>True when the last commit changed nothing (everything was already there).</summary>
    public bool LastCommitWasNoOp { get; private set; }

    /// <summary>Null when the commit is valid; otherwise the short reason shown under the disabled button.</summary>
    public string? CommitBlockedReason
    {
        get
        {
            if (_hasCommitted) return "Already imported";
            if (IsCommitting) return "Importing…";
            if (IsLoading) return LoadProgressText;
            if (TotalCount == 0) return HasError ? "Nothing was loaded" : "Nothing to import";
            if (_isMergeMode)
            {
                if (SelectedMergeTarget == null) return "Choose a playlist to add to";
                if (IsMergeTargetLoading) return $"Checking what's already in '{Truncate(SelectedMergeTarget.Title, 32)}'…";
            }
            else if (string.IsNullOrWhiteSpace(PlaylistName))
            {
                return "Name the new playlist";
            }

            if (EffectiveSelectedCount == 0)
            {
                if (_isMergeMode && SelectedMergeTarget != null && InPlaylistCount > 0 && NewCount + OwnedCount + QueuedCount == 0)
                    return $"Everything is already in '{Truncate(SelectedMergeTarget.Title, 32)}'";
                return "Select at least one track";
            }
            return null;
        }
    }

    public bool HasCommitBlockedReason => CommitBlockedReason != null;

    public bool CanAddToLibrary => CommitBlockedReason == null;

    /// <summary>The primary button's label: always names the action it performs.</summary>
    public string CommitButtonLabel
    {
        get
        {
            var n = EffectiveSelectedCount;
            if (!_isMergeMode) return n > 0 ? $"Import {Plural(n, "track")}" : "Import";
            if (SelectedMergeTarget == null) return "Add to playlist";
            var name = Truncate(SelectedMergeTarget.Title, 24);
            if (IsUpdatingSameSource) return n > 0 ? $"Update '{name}' (+{n})" : $"Update '{name}'";
            return n > 0 ? $"Add {n} to '{name}'" : $"Add to '{name}'";
        }
    }

    /// <summary>One sentence spelling out exactly what the commit will do.</summary>
    public string CommitSummary
    {
        get
        {
            var n = EffectiveSelectedCount;
            if (n == 0) return string.Empty;

            var head = _isMergeMode
                ? SelectedMergeTarget == null ? $"Add {Plural(n, "track")}" : $"Add {Plural(n, "track")} to '{SelectedMergeTarget.Title}'"
                : $"Add {Plural(n, "track")} to new playlist '{PlaylistName.Trim()}'";

            var details = new List<string>();
            if (SelectedOwnedCount > 0) details.Add($"{SelectedOwnedCount} already owned");
            if (SelectedDownloadCount > 0) details.Add($"{SelectedDownloadCount} will download");
            if (SelectedQueuedCount > 0) details.Add($"{SelectedQueuedCount} already downloading");
            var text = details.Count > 0 ? $"{head} · {string.Join(", ", details)}" : head;
            if (_isMergeMode && InPlaylistCount > 0) text += $" · {InPlaylistCount} already there";
            return text;
        }
    }

    /// <summary>
    /// The single place every input of <see cref="CanAddToLibrary"/> funnels through. The commit
    /// button is driven by <see cref="AddToLibraryCommand"/>'s CanExecute, which Avalonia only
    /// re-queries on CanExecuteChanged. Raising just the property (as the old setters did) left
    /// the button disabled after loading finished, after picking a merge target, or after
    /// switching destination, until the user happened to toggle a checkbox.
    /// </summary>
    private void RaiseCommitStateChanged()
    {
        OnPropertyChanged(nameof(CanAddToLibrary));
        OnPropertyChanged(nameof(CommitBlockedReason));
        OnPropertyChanged(nameof(HasCommitBlockedReason));
        OnPropertyChanged(nameof(CommitButtonLabel));
        OnPropertyChanged(nameof(CommitSummary));
        OnPropertyChanged(nameof(EffectiveSelectedCount));
        (AddToLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    // ═════════════════════════════════ Commands / events ═════════════════════════════════

    public ICommand AddToLibraryCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    /// <summary>Selects only rows that still need downloading (New / Similar / Retry).</summary>
    public ICommand SelectMissingCommand { get; }
    /// <summary>Back to the default selection for the current destination.</summary>
    public ICommand ResetSelectionCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AcceptSuggestionCommand { get; }
    public ICommand ImportAsNewCommand { get; }
    public ICommand UseCollidingPlaylistCommand { get; }
    public ICommand ShowAllTracksCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ReconnectCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<PlaylistJob>? AddedToLibrary;
    public event EventHandler? Cancelled;
    /// <summary>The user asked to run the same import again (error state).</summary>
    public event EventHandler? RetryRequested;
    /// <summary>The user asked to (re)connect the account the source needs.</summary>
    public event EventHandler? ReconnectRequested;

    /// <summary>
    /// Marshals view-bound mutations onto the UI thread. Runs inline when already there.
    /// Tests replace it with a synchronous invoker (there is no running dispatcher under xUnit).
    /// </summary>
    internal Func<Action, Task> RunOnUiThread { get; set; } = DefaultRunOnUiThread;

    private static Task DefaultRunOnUiThread(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    // ═════════════════════════════════ Session lifecycle ═════════════════════════════════

    /// <summary>
    /// Resets the preview for a new import and shows it immediately in its loading state.
    /// <paramref name="existingJob"/> is the playlist this same source was imported into before
    /// (matched by id / URL / Spotify playlist id); when set, the destination defaults to updating
    /// it and the suggestion card offers "Import as new" instead.
    /// </summary>
    public void InitializeStreamingPreview(string sourceTitle, string sourceType, Guid newJobId, string? inputUrl, PlaylistJob? existingJob = null)
    {
        var session = ++_session;
        _closed = false;
        _libraryIndexTask = null;
        _membershipCache.Clear();
        _targetMembership = null;
        _hasCommitted = false;
        _playlistNameEdited = false;
        _newJobId = newJobId;
        _sameSourceJob = existingJob;
        _suggestionNewCount = null;
        _suggestionOverlapCount = 0;
        LastCommitResult = null;
        LastCommitSummary = string.Empty;
        LastCommitWasNoOp = false;

        ImportedTracks = new ObservableCollection<SelectableTrack>();
        VisibleTracks = new ObservableCollection<SelectableTrack>();
        MergeTargets.Clear();
        FilteredMergeTargets.Clear();
        _targetSearchText = string.Empty;
        OnPropertyChanged(nameof(TargetSearchText));
        _statusFilter = ImportStatusFilter.All;
        OnPropertyChanged(nameof(StatusFilter));
        RaiseFilterFlags();

        SourceOwner = null;
        SourceType = sourceType;
        SourceTitle = sourceTitle;
        SetPlaylistName(sourceTitle, userEdit: false);
        SourceUrl = inputUrl;

        HasError = false;
        ErrorMessage = string.Empty;
        ErrorRequiresSignIn = false;
        CommitError = null;
        IsCommitting = false;
        LoadedCount = 0;
        ExpectedTrackCount = 0;
        IsLoading = true;

        if (existingJob != null)
        {
            var option = new MergeTargetOptionViewModel(existingJob);
            _selectedMergeTarget = option;
            _isMergeMode = true;
            _suggestionReason = ImportSuggestionReason.SameSource;
            _suggestedTarget = option;
        }
        else
        {
            _selectedMergeTarget = null;
            _isMergeMode = false;
            _suggestionReason = ImportSuggestionReason.None;
            _suggestedTarget = null;
        }
        _suggestionResolved = false;

        OnPropertyChanged(nameof(SelectedMergeTarget));
        OnPropertyChanged(nameof(IsMergeMode));
        OnPropertyChanged(nameof(IsNewPlaylistDestination));
        OnPropertyChanged(nameof(IsUpdatingSameSource));
        OnPropertyChanged(nameof(HasMergeTargets));
        OnPropertyChanged(nameof(SuggestedTarget));
        OnPropertyChanged(nameof(SuggestionReason));
        OnPropertyChanged(nameof(SuggestionResolved));
        RaiseSuggestionChanged();
        RaiseNameCollisionChanged();

        RecomputeStatuses(ImportedTracks);
        RaiseViewStateChanged();

        _ = LoadMergeTargetsAsync(session);
        if (existingJob != null) _ = LoadTargetMembershipAsync(existingJob.Id, session);
    }

    /// <summary>Appends a batch of streamed tracks: matches them against the library, then shows them.</summary>
    public async Task AddTracksToPreviewAsync(IEnumerable<SearchQuery> queries)
    {
        var batch = queries?.ToList();
        if (_closed || batch == null || batch.Count == 0) return;
        var session = _session;

        var index = await GetLibraryIndexAsync();
        var rows = new List<SelectableTrack>(batch.Count);
        foreach (var query in batch)
        {
            var row = new SelectableTrack(ImportTrackMapper.ToTrack(query));
            MatchAgainstLibrary(row, index);
            row.IsInFlight = SafeIsInFlight(row.Model);
            rows.Add(row);
        }

        await RunOnUiThread(() =>
        {
            if (session != _session) return;
            foreach (var row in rows)
            {
                row.OnSelectionChanged = UpdateSelectedCount;
                row.OnIdentityChanged = () => OnRowIdentityChanged(row);
                ImportedTracks.Add(row);
            }
            LoadedCount = ImportedTracks.Count;
            RecomputeStatuses(rows);
            RaiseViewStateChanged();
        });
    }

    /// <summary>Called once the source is exhausted. Looks for a likely duplicate playlist.</summary>
    public void CompleteLoading()
    {
        IsLoading = false;
        if (!HasError) ExpectedTrackCount = LoadedCount;
        UpdateSuggestionCounts();
        RaiseViewStateChanged();
        RaiseCommitStateChanged();
        if (_sameSourceJob == null) _ = DetectLikelyDuplicateAsync(_session);
    }

    // ═════════════════════════════════ Matching ═════════════════════════════════

    private Task<LibraryMatchIndex> GetLibraryIndexAsync()
    {
        if (_libraryService == null) return Task.FromResult(LibraryMatchIndex.Empty);
        return _libraryIndexTask ??= BuildLibraryIndexAsync(_libraryService);
    }

    private async Task<LibraryMatchIndex> BuildLibraryIndexAsync(ILibraryService library)
    {
        try
        {
            var entries = await library.LoadAllLibraryEntriesAsync();
            return await Task.Run(() => new LibraryMatchIndex(entries));
        }
        catch (Exception ex)
        {
            // Rows stay "New" (will download) rather than claiming ownership we can't verify.
            _logger.LogWarning(ex, "Import preview: library lookup failed; rows can't be marked as owned");
            return LibraryMatchIndex.Empty;
        }
    }

    private static void MatchAgainstLibrary(SelectableTrack row, LibraryMatchIndex index)
    {
        var t = row.Model;
        var (owned, similar, score) = index.Match(t.Artist, t.Title, t.SpotifyTrackId, t.UniqueHash);
        t.IsInLibrary = owned != null;
        row.OwnedLibraryHash = owned?.UniqueHash;
        t.IsPossibleDuplicate = similar != null;
        t.FuzzyMatchScore = similar != null ? score : 0;
        t.FuzzyMatchArtist = similar?.Artist;
        t.FuzzyMatchTitle = similar?.Title;
    }

    private bool SafeIsInFlight(Track t)
    {
        try { return _queue.IsInFlight(t.SpotifyTrackId, t.Artist, t.Title); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Import preview: in-flight lookup failed for {Artist} - {Title}", t.Artist, t.Title);
            return false;
        }
    }

    private async void OnRowIdentityChanged(SelectableTrack row)
    {
        try
        {
            var index = await GetLibraryIndexAsync();
            MatchAgainstLibrary(row, index);
            row.IsInFlight = SafeIsInFlight(row.Model);
            await RunOnUiThread(() => RecomputeStatuses(null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import preview: re-matching an edited row failed");
        }
    }

    /// <summary>
    /// Re-labels every row for the current destination. Rows in <paramref name="applyDefaultsTo"/>
    /// get the default selection for their new status (all rows when the destination changed,
    /// just the new batch while streaming).
    /// </summary>
    private void RecomputeStatuses(IEnumerable<SelectableTrack>? applyDefaultsTo)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var membership = _isMergeMode ? _targetMembership : null;
        var targetName = SelectedMergeTarget?.Title ?? "the playlist";

        foreach (var row in ImportedTracks)
        {
            var (status, tip) = Classify(row, seen, membership, targetName);
            row.SetStatus(status, tip);
        }

        if (applyDefaultsTo != null)
        {
            _bulkSelecting = true;
            try
            {
                foreach (var row in applyDefaultsTo) row.IsSelected = IsSelectedByDefault(row.Status);
            }
            finally { _bulkSelecting = false; }
        }

        UpdateCounts();
        if (StatusFilter != ImportStatusFilter.All && CountFor(StatusFilter) == 0)
            StatusFilter = ImportStatusFilter.All; // filter emptied by a destination change
        RefreshVisibleTracks();
        UpdateSelectedCount();
        UpdateSuggestionCounts();
    }

    private static (ImportRowStatus, string) Classify(SelectableTrack row, HashSet<string> seen, PlaylistMembershipIndex? membership, string targetName)
    {
        var t = row.Model;
        if (string.IsNullOrWhiteSpace(t.Title))
            return (ImportRowStatus.NoMatch, "No title, so this row can't be searched. Edit it to fix.");

        var hash = t.UniqueHash;
        if (!seen.Add(hash))
            return (ImportRowStatus.Repeat, "Appears earlier in this list. It's imported once.");

        if (membership != null)
        {
            var existing = membership.Find(hash, row.OwnedLibraryHash, t.SpotifyTrackId, t.Artist, t.Title);
            if (existing != null)
            {
                return PlaylistMembershipIndex.NeedsRetry(existing)
                    ? (ImportRowStatus.Retry, $"Already in '{targetName}' but it failed to download before. It will be retried.")
                    : (ImportRowStatus.InPlaylist, $"Already in '{targetName}'. Skipped.");
            }
        }

        if (t.IsInLibrary)
            return (ImportRowStatus.Owned, "Already in your library. The playlist links your file; nothing to download.");
        if (row.IsInFlight)
            return (ImportRowStatus.Queued, "Already downloading. The playlist links it when it finishes.");
        if (t.IsPossibleDuplicate)
        {
            var pct = (int)Math.Round(t.FuzzyMatchScore * 100);
            return (ImportRowStatus.Similar,
                $"Not owned, but '{t.FuzzyMatchArtist} - {t.FuzzyMatchTitle}' in your library is a {pct}% match. It will download unless you uncheck it.");
        }
        return (ImportRowStatus.New, "Not in your library. It will be searched and downloaded.");
    }

    private static bool IsSelectedByDefault(ImportRowStatus status) =>
        status is not (ImportRowStatus.InPlaylist or ImportRowStatus.Repeat or ImportRowStatus.NoMatch);

    private static ImportStatusFilter FilterFor(ImportRowStatus status) => status switch
    {
        ImportRowStatus.New or ImportRowStatus.Similar or ImportRowStatus.Retry => ImportStatusFilter.New,
        ImportRowStatus.Owned => ImportStatusFilter.Owned,
        ImportRowStatus.InPlaylist => ImportStatusFilter.InPlaylist,
        ImportRowStatus.Queued => ImportStatusFilter.Queued,
        _ => ImportStatusFilter.NoMatch
    };

    private int CountFor(ImportStatusFilter filter) => filter switch
    {
        ImportStatusFilter.New => NewCount,
        ImportStatusFilter.Owned => OwnedCount,
        ImportStatusFilter.InPlaylist => InPlaylistCount,
        ImportStatusFilter.Queued => QueuedCount,
        ImportStatusFilter.NoMatch => NoMatchCount,
        _ => TotalCount
    };

    /// <summary>Rows that can be imported at all (not repeats, not unsearchable).</summary>
    private int ImportableCount => TotalCount - NoMatchCount;

    private void UpdateCounts()
    {
        int total = 0, fresh = 0, owned = 0, inPlaylist = 0, queued = 0, noMatch = 0;
        foreach (var row in ImportedTracks)
        {
            total++;
            switch (FilterFor(row.Status))
            {
                case ImportStatusFilter.New: fresh++; break;
                case ImportStatusFilter.Owned: owned++; break;
                case ImportStatusFilter.InPlaylist: inPlaylist++; break;
                case ImportStatusFilter.Queued: queued++; break;
                default: noMatch++; break;
            }
        }
        TotalCount = total;
        NewCount = fresh;
        OwnedCount = owned;
        InPlaylistCount = inPlaylist;
        QueuedCount = queued;
        NoMatchCount = noMatch;
        OnPropertyChanged(nameof(TrackCount));
        OnPropertyChanged(nameof(HasInPlaylist));
        OnPropertyChanged(nameof(HasQueued));
        OnPropertyChanged(nameof(HasNoMatch));
        OnPropertyChanged(nameof(SourceSubtitle));
        RaiseViewStateChanged();
    }

    public void UpdateSelectedCount()
    {
        if (_bulkSelecting) return;
        int selected = 0, download = 0, owned = 0, queued = 0, inPlaylist = 0;
        foreach (var row in ImportedTracks)
        {
            if (!row.IsSelected) continue;
            selected++;
            switch (row.Status)
            {
                case ImportRowStatus.New:
                case ImportRowStatus.Similar:
                case ImportRowStatus.Retry: download++; break;
                case ImportRowStatus.Owned: owned++; break;
                case ImportRowStatus.Queued: queued++; break;
                case ImportRowStatus.InPlaylist: inPlaylist++; break;
            }
        }
        SelectedDownloadCount = download;
        SelectedOwnedCount = owned;
        SelectedQueuedCount = queued;
        SelectedInPlaylistCount = inPlaylist;
        SelectedCount = selected;
        RaiseCommitStateChanged();
    }

    private void RefreshVisibleTracks()
    {
        var desired = StatusFilter == ImportStatusFilter.All
            ? ImportedTracks.ToList()
            : ImportedTracks.Where(r => FilterFor(r.Status) == StatusFilter).ToList();

        var current = VisibleTracks;
        var isPrefix = desired.Count >= current.Count;
        for (int i = 0; isPrefix && i < current.Count; i++)
            if (!ReferenceEquals(current[i], desired[i])) isPrefix = false;

        if (isPrefix)
        {
            for (int i = current.Count; i < desired.Count; i++) current.Add(desired[i]);
        }
        else
        {
            VisibleTracks = new ObservableCollection<SelectableTrack>(desired);
        }
        OnPropertyChanged(nameof(ShowFilterEmptyState));
    }

    // ═════════════════════════════════ Destination plumbing ═════════════════════════════════

    private void OnTargetChanged()
    {
        _targetMembership = null;
        var target = _isMergeMode ? SelectedMergeTarget : null;
        if (target != null && _membershipCache.TryGetValue(target.Job.Id, out var cached))
        {
            _targetMembership = cached;
            target = null; // nothing to load
        }

        RecomputeStatuses(ImportedTracks);
        RaiseCommitStateChanged();

        if (target != null) _ = LoadTargetMembershipAsync(target.Job.Id, _session);
        else IsMergeTargetLoading = false;
    }

    private async Task<PlaylistMembershipIndex> GetMembershipAsync(Guid playlistId)
    {
        if (_membershipCache.TryGetValue(playlistId, out var cached)) return cached;
        if (_libraryService == null) return PlaylistMembershipIndex.Empty;
        var rows = await _libraryService.LoadPlaylistTracksAsync(playlistId);
        var index = new PlaylistMembershipIndex(rows);
        _membershipCache[playlistId] = index;
        return index;
    }

    private async Task LoadTargetMembershipAsync(Guid playlistId, int session)
    {
        IsMergeTargetLoading = true;
        try
        {
            var index = await GetMembershipAsync(playlistId);
            await RunOnUiThread(() =>
            {
                if (session != _session || SelectedMergeTarget?.Job.Id != playlistId || !_isMergeMode) return;
                _targetMembership = index;
                RecomputeStatuses(ImportedTracks);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import preview: couldn't load the tracks of playlist {PlaylistId}", playlistId);
            await RunOnUiThread(() =>
            {
                if (session != _session) return;
                CommitError = "Couldn't read that playlist's tracks, so rows already in it aren't marked. The import still skips them.";
            });
        }
        finally
        {
            await RunOnUiThread(() => { if (session == _session) IsMergeTargetLoading = false; });
        }
    }

    private async Task LoadMergeTargetsAsync(int session)
    {
        if (_libraryService == null) return;
        try
        {
            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();
            var options = jobs
                .Where(j => !j.IsDeleted && !j.IsSmartPlaylist)
                .OrderByDescending(j => j.CreatedAt)
                .Select(j => new MergeTargetOptionViewModel(j))
                .ToList();

            await RunOnUiThread(() =>
            {
                if (session != _session) return;
                MergeTargets.Clear();
                foreach (var o in options) MergeTargets.Add(o);

                // Swap the placeholder option created from the orchestrator's job for the list's
                // own instance (same playlist), so the picker shows it as selected.
                if (_selectedMergeTarget != null)
                {
                    var match = MergeTargets.FirstOrDefault(m => m.Job.Id == _selectedMergeTarget.Job.Id);
                    if (match == null) MergeTargets.Insert(0, _selectedMergeTarget);
                    else
                    {
                        if (_suggestedTarget?.Job.Id == match.Job.Id) _suggestedTarget = match;
                        _selectedMergeTarget = match;
                        OnPropertyChanged(nameof(SelectedMergeTarget));
                        OnPropertyChanged(nameof(SuggestedTarget));
                    }
                }

                RefreshFilteredTargets();
                OnPropertyChanged(nameof(HasMergeTargets));
                RaiseNameCollisionChanged();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import preview: failed to load playlists for the destination picker");
        }
    }

    private void RefreshFilteredTargets()
    {
        var q = TargetSearchText.Trim();
        var desired = MergeTargets
            .Where(t => q.Length == 0 || ReferenceEquals(t, SelectedMergeTarget)
                        || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || t.Job.SourceType.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Update in place: clearing the collection would make the ListBox push SelectedItem=null
        // back through the TwoWay binding and silently drop the chosen destination.
        for (int i = FilteredMergeTargets.Count - 1; i >= 0; i--)
            if (!desired.Contains(FilteredMergeTargets[i])) FilteredMergeTargets.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            var existingAt = FilteredMergeTargets.IndexOf(desired[i]);
            if (existingAt == i) continue;
            if (existingAt >= 0) FilteredMergeTargets.Move(existingAt, i);
            else FilteredMergeTargets.Insert(i, desired[i]);
        }
    }

    private void UpdateSuggestionCounts()
    {
        if (SuggestedTarget == null || !_membershipCache.TryGetValue(SuggestedTarget.Job.Id, out var membership) || IsLoading)
        {
            if (_suggestionNewCount != null) { _suggestionNewCount = null; RaiseSuggestionChanged(); }
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fresh = 0, overlap = 0;
        foreach (var row in ImportedTracks)
        {
            var t = row.Model;
            if (string.IsNullOrWhiteSpace(t.Title) || !seen.Add(t.UniqueHash)) continue;
            var hit = membership.Find(t.UniqueHash, row.OwnedLibraryHash, t.SpotifyTrackId, t.Artist, t.Title);
            if (hit == null) fresh++; else overlap++;
        }
        _suggestionNewCount = fresh;
        _suggestionOverlapCount = overlap;
        RaiseSuggestionChanged();
    }

    /// <summary>
    /// No same-source match: look for a playlist with the same name, or one of the most recent
    /// playlists that already holds most of these tracks.
    /// </summary>
    private async Task DetectLikelyDuplicateAsync(int session)
    {
        if (_libraryService == null || TotalCount == 0) return;
        try
        {
            // Reuse the picker's list when it's already loaded (loading every playlist with its
            // tracks is the expensive part); snapshot it before the first await.
            var known = MergeTargets.Select(m => m.Job).ToList();
            var jobs = (known.Count > 0 ? known : await _libraryService.LoadAllPlaylistJobsAsync())
                .Where(j => !j.IsDeleted && !j.IsSmartPlaylist)
                .ToList();

            var name = SourceTitle.Trim();
            var byName = jobs.FirstOrDefault(j => string.Equals(j.SourceTitle.Trim(), name, StringComparison.OrdinalIgnoreCase));
            var candidates = jobs.Where(j => j.TotalTracks > 0).OrderByDescending(j => j.CreatedAt).Take(OverlapCandidateCount).ToList();
            if (byName != null && !candidates.Contains(byName)) candidates.Insert(0, byName);

            var rows = ImportedTracks.ToList();
            PlaylistJob? best = null;
            int bestOverlap = 0;
            foreach (var job in candidates)
            {
                var membership = await GetMembershipAsync(job.Id);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int overlap = 0;
                foreach (var row in rows)
                {
                    var t = row.Model;
                    if (string.IsNullOrWhiteSpace(t.Title) || !seen.Add(t.UniqueHash)) continue;
                    if (membership.Find(t.UniqueHash, row.OwnedLibraryHash, t.SpotifyTrackId, t.Artist, t.Title) != null) overlap++;
                }
                if (overlap > bestOverlap) { best = job; bestOverlap = overlap; }
            }

            var importable = Math.Max(1, ImportableCount);
            ImportSuggestionReason reason;
            PlaylistJob suggested;
            if (byName != null) { reason = ImportSuggestionReason.SameName; suggested = byName; }
            else if (best != null && bestOverlap >= OverlapSuggestionMinTracks && (double)bestOverlap / importable >= OverlapSuggestionRatio)
            { reason = ImportSuggestionReason.HighOverlap; suggested = best; }
            else return;

            await RunOnUiThread(() =>
            {
                if (session != _session || _sameSourceJob != null) return;
                var option = MergeTargets.FirstOrDefault(m => m.Job.Id == suggested.Id) ?? new MergeTargetOptionViewModel(suggested);
                _suggestionReason = reason;
                _suggestionResolved = false;
                SuggestedTarget = option;
                OnPropertyChanged(nameof(SuggestionReason));
                OnPropertyChanged(nameof(SuggestionResolved));
                UpdateSuggestionCounts();
                RaiseNameCollisionChanged();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import preview: duplicate-playlist detection failed");
        }
    }

    private void AcceptSuggestion()
    {
        if (SuggestedTarget == null) return;
        if (!MergeTargets.Contains(SuggestedTarget)) MergeTargets.Insert(0, SuggestedTarget);
        SuggestionResolved = true;
        _selectedMergeTarget = SuggestedTarget;
        OnPropertyChanged(nameof(SelectedMergeTarget));
        if (_isMergeMode) OnTargetChanged();
        else IsMergeMode = true;
        OnPropertyChanged(nameof(IsUpdatingSameSource));
        RefreshFilteredTargets();
    }

    private void ImportAsNew()
    {
        SuggestionResolved = true;
        IsMergeMode = false;
    }

    private void UseCollidingPlaylist()
    {
        var target = NameCollisionTarget;
        if (target == null) return;
        _selectedMergeTarget = target;
        OnPropertyChanged(nameof(SelectedMergeTarget));
        IsMergeMode = true;
        RefreshFilteredTargets();
    }

    // ═════════════════════════════════ Selection commands ═════════════════════════════════

    private void BulkSelect(Func<SelectableTrack, bool> selector, IEnumerable<SelectableTrack> rows)
    {
        _bulkSelecting = true;
        try { foreach (var row in rows.ToList()) row.IsSelected = selector(row); }
        finally { _bulkSelecting = false; }
        UpdateSelectedCount();
    }

    /// <summary>Checks every row in the current filter.</summary>
    private void SelectAll() => BulkSelect(_ => true, VisibleTracks);

    /// <summary>Unchecks every row in the current filter.</summary>
    private void DeselectAll() => BulkSelect(_ => false, VisibleTracks);

    private void SelectMissing() => BulkSelect(
        r => r.Status is ImportRowStatus.New or ImportRowStatus.Similar or ImportRowStatus.Retry, ImportedTracks);

    private void ResetSelection() => BulkSelect(r => IsSelectedByDefault(r.Status), ImportedTracks);

    // ═════════════════════════════════ Commit ═════════════════════════════════

    private async Task AddToLibraryAsync()
    {
        if (!CanAddToLibrary) return;

        // Only the rows the footer counted: repeats/unsearchable rows are dropped by
        // QueueProject anyway, and selected "already there" rows are no-ops.
        var selected = ImportedTracks
            .Where(r => r.IsSelected && r.Status is not (ImportRowStatus.Repeat or ImportRowStatus.NoMatch or ImportRowStatus.InPlaylist))
            .Select(r => r.Model)
            .ToList();
        if (selected.Count == 0) return;

        var job = BuildCommitJob();
        foreach (var track in selected) job.OriginalTracks.Add(track);

        IsCommitting = true;
        CommitError = null;
        try
        {
            _logger.LogInformation("Import commit: {Count} tracks to {Mode} playlist '{Title}' ({JobId})",
                selected.Count, _isMergeMode ? "existing" : "new", job.SourceTitle, job.Id);

            var result = await _queue.QueueProjectAsync(job);
            _hasCommitted = true;
            LastCommitResult = result;
            LastCommitWasNoOp = result.Added + result.Retried + result.ReconciledExisting == 0;
            LastCommitSummary = BuildCommitSummary(job, result);

            if (_metadataService != null && _libraryService != null)
            {
                var jobId = job.Id;
                _ = Task.Run(() => EnrichPersistedTracksAsync(jobId));
            }

            AddedToLibrary?.Invoke(this, job);
        }
        catch (Exception ex)
        {
            // Nothing was queued: stay on the page, say so, let the user try again.
            _logger.LogError(ex, "Import commit failed for '{Title}' ({JobId})", job.SourceTitle, job.Id);
            CommitError = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private PlaylistJob BuildCommitJob()
    {
        if (_isMergeMode && SelectedMergeTarget is { } target)
        {
            var existing = target.Job;
            // Keep the destination's own identity. The old commit stamped the incoming source's
            // title/type/URL onto the target, so adding a pasted tracklist to "Techno" renamed
            // it "Pasted Tracklist (…)" and pointed its sync link at the paste.
            return new PlaylistJob
            {
                Id = existing.Id,
                SourceTitle = existing.SourceTitle,
                SourceType = existing.SourceType,
                SourceUrl = IsUpdatingSameSource ? SourceUrl ?? existing.SourceUrl : existing.SourceUrl,
                CreatedAt = existing.CreatedAt,
                DestinationFolder = existing.DestinationFolder,
                IsSmartPlaylist = existing.IsSmartPlaylist,
                SmartCriteriaJson = existing.SmartCriteriaJson
            };
        }

        // The deterministic id (hash of the source) lets the next import of the same source find
        // this playlist. When that id already belongs to a playlist (the user chose "Import as
        // new" over updating it), mint a fresh one so the two stay separate.
        var idTaken = _newJobId == Guid.Empty || (_sameSourceJob != null && _sameSourceJob.Id == _newJobId);
        return new PlaylistJob
        {
            Id = idTaken ? Guid.NewGuid() : _newJobId,
            SourceTitle = PlaylistName.Trim(),
            SourceType = SourceType,
            SourceUrl = SourceUrl,
            CreatedAt = DateTime.UtcNow
        };
    }

    internal static string BuildCommitSummary(PlaylistJob job, ProjectQueueResult r)
    {
        if (r.Added + r.Retried + r.ReconciledExisting == 0)
            return $"Nothing new to add to '{job.SourceTitle}'. Every track was already there.";

        var parts = new List<string>();
        if (r.LinkedFromLibrary > 0) parts.Add($"{r.LinkedFromLibrary} linked from your library");
        if (r.QueuedForDownload > 0) parts.Add($"{r.QueuedForDownload} queued for download");
        if (r.ReconciledExisting > 0) parts.Add($"{r.ReconciledExisting} matched to files you already have");
        if (r.AlreadyInPlaylist > 0) parts.Add($"{r.AlreadyInPlaylist} already there");
        if (r.SkippedInvalidOrRepeated > 0) parts.Add($"{r.SkippedInvalidOrRepeated} skipped as repeats");
        var details = parts.Count > 0 ? ": " + string.Join(", ", parts) : string.Empty;
        return $"{Plural(r.Added, "track")} added to '{job.SourceTitle}'{details}.";
    }

    /// <summary>
    /// Fills Spotify metadata on rows that came in without it (pasted tracklists, CSV), after they
    /// are persisted. Runs in the background after the commit.
    /// </summary>
    private async Task EnrichPersistedTracksAsync(Guid jobId)
    {
        try
        {
            var tracks = await _libraryService!.LoadPlaylistTracksAsync(jobId);
            var needsEnrichment = tracks.Where(t => string.IsNullOrEmpty(t.SpotifyTrackId)).ToList();
            if (needsEnrichment.Count == 0) return;

            _logger.LogInformation("Post-import enrichment: {Count} tracks need Spotify metadata", needsEnrichment.Count);
            int enriched = 0;
            foreach (var pt in needsEnrichment)
            {
                try
                {
                    if (await _metadataService!.EnrichTrackAsync(pt))
                    {
                        await _libraryService.UpdatePlaylistTrackAsync(pt);
                        enriched++;
                    }
                    await Task.Delay(300); // Respect Spotify rate limit
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-import enrichment failed for {Artist} - {Title}", pt.Artist, pt.Title);
                }
            }
            _logger.LogInformation("Post-import enrichment complete: {Enriched}/{Total} tracks for job {JobId}", enriched, needsEnrichment.Count, jobId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-import enrichment task failed for job {JobId}", jobId);
        }
    }

    private void Cancel()
    {
        if (IsCommitting) return;
        _session++; // drop any batches still in flight for this preview
        _closed = true;
        IsLoading = false;
        _logger.LogInformation("Import preview cancelled");
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    // ═════════════════════════════════ Helpers ═════════════════════════════════

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{SLSKDONET.Utils.CountFormat.N(n)} {noun}s";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>One playlist in the "Add to existing" picker.</summary>
public class MergeTargetOptionViewModel
{
    public MergeTargetOptionViewModel(PlaylistJob job)
    {
        Job = job;
    }

    public PlaylistJob Job { get; }
    public string Title => Job.SourceTitle;
    public string Subtitle => string.IsNullOrWhiteSpace(Job.SourceType) ? string.Empty : Job.SourceType;
    public string TrackCountText => Job.TotalTracks == 1 ? "1 track" : $"{SLSKDONET.Utils.CountFormat.N(Job.TotalTracks)} tracks";
    public string Display => $"{Job.SourceTitle} ({TrackCountText})";
}
