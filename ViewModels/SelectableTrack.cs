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
            lines.AppendLine("⚠️ Field(s) were sanitized during import:");
            if (Model.OriginalArtist != null && Model.OriginalArtist != Model.Artist)
                lines.AppendLine($"  Artist:  \"{Model.OriginalArtist}\"  →  \"{Model.Artist}\"");
            if (Model.OriginalTitle != null && Model.OriginalTitle != Model.Title)
                lines.AppendLine($"  Title:   \"{Model.OriginalTitle}\"  →  \"{Model.Title}\"");
            lines.Append("Click ↩ to restore the original text.");
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

