using System;
using System.Threading.Tasks;
using System.Windows.Input;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>One row of the Import page's "Recent imports" list.</summary>
public class RecentImportItemViewModel
{
    public RecentImportItemViewModel(
        PlaylistJob job,
        IImportProvider? refreshProvider,
        Func<RecentImportItemViewModel, Task> checkForNewTracks,
        Action<RecentImportItemViewModel> open)
    {
        Job = job;
        RefreshProvider = refreshProvider;
        CheckForNewTracksCommand = new AsyncRelayCommand(() => checkForNewTracks(this), () => CanCheckForNewTracks);
        OpenCommand = new RelayCommand(() => open(this));
    }

    public PlaylistJob Job { get; }

    /// <summary>The provider that can fetch the source again; null for pasted text.</summary>
    public IImportProvider? RefreshProvider { get; }

    public string Title => Job.SourceTitle;
    public string SourceType => Job.SourceType;
    public string TrackCountText => Job.TotalTracks == 1 ? "1 track" : $"{Job.TotalTracks:N0} tracks";
    public string DateText => Job.CreatedAt.ToLocalTime().ToString("d MMM yyyy");
    public bool IsSpotify => Job.SourceType.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
    public bool IsCsv => Job.SourceType.Contains("CSV", StringComparison.OrdinalIgnoreCase);
    public bool IsText => !IsSpotify && !IsCsv;

    public bool CanCheckForNewTracks => RefreshProvider != null && !string.IsNullOrWhiteSpace(Job.SourceUrl);

    /// <summary>Fetches the source again and opens the preview, which offers to update this playlist.</summary>
    public ICommand CheckForNewTracksCommand { get; }

    /// <summary>Opens the playlist in the Library.</summary>
    public ICommand OpenCommand { get; }
}
