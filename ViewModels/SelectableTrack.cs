using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Wrapper for Track model to handle UI selection state and notifications.
/// Supports in-place editing of Artist/Title and "Restore Original" for over-cleaned imports.
/// </summary>
public class SelectableTrack : INotifyPropertyChanged
{
    public Track Model { get; }
    public Track Track => Model; // Alias for compatibility
    private readonly RelayCommand _restoreOriginalCommand;

    // ─── Selection ────────────────────────────────────────────────────────────

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                Model.IsSelected = value; // Sync with model
                OnPropertyChanged();
                OnSelectionChanged?.Invoke();
            }
        }
    }

    public Action? OnSelectionChanged { get; set; }

    /// <summary>Raised after the user edits Artist or Title, so the import preview can re-match the row.</summary>
    public Action? OnIdentityChanged { get; set; }

    // ─── Import preview status ────────────────────────────────────────────────

    private ImportRowStatus _status = ImportRowStatus.New;
    private string _statusTooltip = string.Empty;

    /// <summary>What importing this row will do. Set by <see cref="ImportPreviewViewModel"/>.</summary>
    public ImportRowStatus Status => _status;

    /// <summary>Hover text explaining <see cref="Status"/> in a full sentence.</summary>
    public string StatusTooltip => _statusTooltip;

    public void SetStatus(ImportRowStatus status, string tooltip)
    {
        var changed = _status != status;
        _status = status;
        if (_statusTooltip != tooltip)
        {
            _statusTooltip = tooltip;
            OnPropertyChanged(nameof(StatusTooltip));
        }
        if (!changed) return;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(IsToneAccent));
        OnPropertyChanged(nameof(IsToneSuccess));
        OnPropertyChanged(nameof(IsToneInfo));
        OnPropertyChanged(nameof(IsToneWarning));
        OnPropertyChanged(nameof(IsToneError));
    }

    /// <summary>Short status word shown next to the dot.</summary>
    public string StatusLabel => _status switch
    {
        ImportRowStatus.New => "New",
        ImportRowStatus.Owned => "Owned",
        ImportRowStatus.InPlaylist => "In playlist",
        ImportRowStatus.Queued => "Queued",
        ImportRowStatus.Retry => "Retry",
        ImportRowStatus.Similar => "Similar",
        ImportRowStatus.Repeat => "Repeat",
        ImportRowStatus.NoMatch => "No match",
        _ => string.Empty
    };

    public bool IsToneAccent => _status == ImportRowStatus.New;
    public bool IsToneSuccess => _status == ImportRowStatus.Owned;
    public bool IsToneInfo => _status == ImportRowStatus.Queued;
    public bool IsToneWarning => _status is ImportRowStatus.Retry or ImportRowStatus.Similar;
    public bool IsToneError => _status == ImportRowStatus.NoMatch;

    /// <summary>Hash of the owned library file this row resolved to (null when not owned).</summary>
    internal string? OwnedLibraryHash { get; set; }

    /// <summary>True when the same track is already waiting/searching/downloading.</summary>
    internal bool IsInFlight { get; set; }

    /// <summary>"m:ss" from <see cref="Track.Length"/> (seconds) or <see cref="Track.CanonicalDuration"/> (ms); an en dash when unknown.</summary>
    public string DurationDisplay
    {
        get
        {
            int seconds = 0;
            if (Model.Length is > 0) seconds = Model.Length.Value;
            else if (Model.CanonicalDuration is > 0)
                seconds = Model.CanonicalDuration.Value > 10_000 ? Model.CanonicalDuration.Value / 1000 : Model.CanonicalDuration.Value;
            if (seconds <= 0) return "–";
            return seconds >= 3600
                ? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}"
                : $"{seconds / 60}:{seconds % 60:00}";
        }
    }

    // ─── Editable Artist / Title ──────────────────────────────────────────────

    /// <summary>
    /// Editable artist field. Writes back to <see cref="Track.Artist"/> so the final
    /// import uses the user-corrected value.
    /// </summary>
    public string? Artist
    {
        get => Model.Artist;
        set
        {
            if (Model.Artist != value)
            {
                Model.Artist = value;
                OnPropertyChanged();
                OnIdentityChanged?.Invoke();
                OnPropertyChanged(nameof(RawInputDisplay));
                OnPropertyChanged(nameof(HasAnyChange));
                OnPropertyChanged(nameof(IsCleaned));
                OnPropertyChanged(nameof(CleanBadgeVisible));
                OnPropertyChanged(nameof(CleanTooltip));
                _restoreOriginalCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Editable title field. Writes back to <see cref="Track.Title"/>.
    /// </summary>
    public string? Title
    {
        get => Model.Title;
        set
        {
            if (Model.Title != value)
            {
                Model.Title = value;
                OnPropertyChanged();
                OnIdentityChanged?.Invoke();
                OnPropertyChanged(nameof(RawInputDisplay));
                OnPropertyChanged(nameof(HasAnyChange));
                OnPropertyChanged(nameof(IsCleaned));
                OnPropertyChanged(nameof(CleanBadgeVisible));
                OnPropertyChanged(nameof(CleanTooltip));
                _restoreOriginalCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ─── Original / Cleaning Indicators ──────────────────────────────────────

    /// <summary>Raw artist string before sanitization (null if none applied).</summary>
    public string? OriginalArtist => Model.OriginalArtist;

    /// <summary>Raw title string before sanitization (null if none applied).</summary>
    public string? OriginalTitle => Model.OriginalTitle;

    /// <summary>
    /// Side-by-side preview column value: original "Artist - Title" (raw input).
    /// Falls back to current values if originals are unavailable.
    /// </summary>
    public string RawInputDisplay
    {
        get
        {
            var rawArtist = string.IsNullOrWhiteSpace(OriginalArtist) ? Artist : OriginalArtist;
            var rawTitle = string.IsNullOrWhiteSpace(OriginalTitle) ? Title : OriginalTitle;
            return $"{rawArtist ?? ""} - {rawTitle ?? ""}".Trim(' ', '-');
        }
    }

    /// <summary>
    /// True when a significant cleaning step removed more than 30% of the original
    /// artist or title string. Drives the ⚠️ badge visibility.
    /// </summary>
    public bool IsCleaned
    {
        get
        {
            if (ExceedsCleanThreshold(Model.OriginalArtist, Model.Artist)) return true;
            if (ExceedsCleanThreshold(Model.OriginalTitle, Model.Title)) return true;
            return false;
        }
    }

    /// <summary>True when any sanitization occurred, even below the 30% threshold.</summary>
    public bool HasAnyChange =>
        (Model.OriginalArtist != null && Model.OriginalArtist != Model.Artist) ||
        (Model.OriginalTitle  != null && Model.OriginalTitle  != Model.Title);

    /// <summary>Drives badge visibility — shows when <see cref="IsCleaned"/> is true.</summary>
    public bool CleanBadgeVisible => IsCleaned;

    /// <summary>
    /// Tooltip text showing original → cleaned diff for artist and title.
    /// </summary>
    public string CleanTooltip
    {
        get
        {
            if (!HasAnyChange) return string.Empty;
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("Cleaned up during import:");
            if (Model.OriginalArtist != null && Model.OriginalArtist != Model.Artist)
                lines.AppendLine($"  Artist:  \"{Model.OriginalArtist}\"  →  \"{Model.Artist}\"");
            if (Model.OriginalTitle != null && Model.OriginalTitle != Model.Title)
                lines.AppendLine($"  Title:   \"{Model.OriginalTitle}\"  →  \"{Model.Title}\"");
            lines.Append("Use the restore button to put the original text back.");
            return lines.ToString();
        }
    }

    // ─── Restore Command ─────────────────────────────────────────────────────

    /// <summary>
    /// Reverts Artist and Title to the raw values captured before sanitization.
    /// </summary>
    public ICommand RestoreOriginalCommand { get; }

    private void RestoreOriginal()
    {
        if (Model.OriginalArtist != null) Artist = Model.OriginalArtist;
        if (Model.OriginalTitle  != null) Title  = Model.OriginalTitle;
        OnPropertyChanged(nameof(RawInputDisplay));
        _restoreOriginalCommand.RaiseCanExecuteChanged();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static bool ExceedsCleanThreshold(string? original, string? cleaned)
    {
        if (string.IsNullOrEmpty(original) || original == cleaned) return false;
        var removed = original.Length - (cleaned?.Length ?? 0);
        return removed >= 20 || (removed > 0 && (double)removed / original.Length > 0.30);
    }

    // ─── Proxied / computed pass-through ─────────────────────────────────────

    public string? Album => Model.Album;
    public bool IsInLibrary => Model.IsInLibrary;
    public bool IsMissingFromLibrary => !Model.IsInLibrary && !Model.IsPossibleDuplicate;

    /// <summary>
    /// True when a fuzzy near-match was found in the library but confidence was below auto-dedup.
    /// Drives the ⚠️ duplicate-warning badge in the import preview.
    /// </summary>
    public bool IsPossibleDuplicate => Model.IsPossibleDuplicate;

    /// <summary>
    /// Tooltip describing the possible-duplicate match for user review.
    /// </summary>
    public string PossibleDuplicateTooltip
    {
        get
        {
            if (!Model.IsPossibleDuplicate) return string.Empty;
            var pct = (int)(Model.FuzzyMatchScore * 100);
            var matched = $"{Model.FuzzyMatchArtist ?? "?"} – {Model.FuzzyMatchTitle ?? "?"}";
            return $"Possible duplicate ({pct}% match)\nLibrary entry: {matched}\nVerify and deselect if already owned.";
        }
    }

    private int _trackNumber;
    public int TrackNumber
    {
        get => _trackNumber;
        set
        {
            if (_trackNumber != value)
            {
                _trackNumber = value;
                OnPropertyChanged();
            }
        }
    }

    // ─── Constructors ─────────────────────────────────────────────────────────

    public SelectableTrack(Track track, bool isSelected = false)
    {
        Model = track;
        _isSelected = isSelected;
        Model.IsSelected = isSelected;
        _restoreOriginalCommand = new RelayCommand(RestoreOriginal, () => HasAnyChange);
        RestoreOriginalCommand = _restoreOriginalCommand;
    }

    // Constructor for SpotifyImportViewModel compatibility
    public SelectableTrack(Track track, int trackNumber) : this(track, false)
    {
        TrackNumber = trackNumber;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


/// <summary>What the import preview will do with one row.</summary>
public enum ImportRowStatus
{
    /// <summary>Not in the library: will be searched and downloaded.</summary>
    New,
    /// <summary>Already in the library: the playlist links the existing file.</summary>
    Owned,
    /// <summary>Already in the destination playlist: skipped.</summary>
    InPlaylist,
    /// <summary>Already downloading: linked when it finishes.</summary>
    Queued,
    /// <summary>In the destination playlist but failed before: retried.</summary>
    Retry,
    /// <summary>Not owned, but a close match exists in the library: review.</summary>
    Similar,
    /// <summary>Same track appears earlier in this import: imported once.</summary>
    Repeat,
    /// <summary>Missing artist or title: can't be searched.</summary>
    NoMatch
}
