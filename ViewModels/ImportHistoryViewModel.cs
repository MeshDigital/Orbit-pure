using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;


using System.ComponentModel;
using System.Runtime.CompilerServices;

using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public partial class ImportHistoryViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    private readonly ILogger<ImportHistoryViewModel> _logger;
    private readonly ILibraryService _libraryService;
    private readonly DownloadManager _downloadManager;
    private readonly INotificationService? _notifications;

    private ObservableCollection<PlaylistJob> _importHistory = new();
    public ObservableCollection<PlaylistJob> ImportHistory
    {
        get => _importHistory;
        set => SetProperty(ref _importHistory, value);
    }

    private PlaylistJob? _selectedJob;
    public PlaylistJob? SelectedJob
    {
        get => _selectedJob;
        set => SetProperty(ref _selectedJob, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public ICommand LoadHistoryCommand { get; }
    public ICommand ReImportCommand { get; }

    public ImportHistoryViewModel(
        ILogger<ImportHistoryViewModel> logger,
        ILibraryService libraryService,
        DownloadManager downloadManager,
        INotificationService? notifications = null)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _notifications = notifications;

        LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync);
        ReImportCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteReImportAsync);

        // REMOVED: Eager loading - now loads only when page is accessed
        // _ = LoadHistoryAsync();
    }

    public async Task LoadHistoryAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            _logger.LogInformation("Loading import job history...");
            var jobs = await _libraryService.GetHistoricalJobsAsync();
            
            ImportHistory.Clear();
            foreach (var job in jobs)
            {
                ImportHistory.Add(job);
            }
            _logger.LogInformation("Loaded {Count} historical jobs.", jobs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load import history");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Re-runs a download pass on a past import: QueueProject re-saves the playlist and queues its
    /// rows; DownloadManager skips finished ones and retries failed/missing ones. This used to be
    /// fire-and-forget with no feedback, so a failure (or success) was invisible.
    /// </summary>
    private async Task ExecuteReImportAsync(PlaylistJob? job)
    {
        if (job == null) return;

        _logger.LogInformation("Re-import requested for job: {Title} ({Id})", job.SourceTitle, job.Id);
        try
        {
            await _downloadManager.QueueProject(job);
            var pending = job.PlaylistTracks.Count(t => t.Status != TrackStatus.Downloaded);
            _notifications?.Show("Re-import queued",
                pending == 0
                    ? $"Every track in '{job.SourceTitle}' is already downloaded."
                    : $"{pending} missing or failed tracks from '{job.SourceTitle}' are queued again.",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-import failed for job {Title} ({Id})", job.SourceTitle, job.Id);
            _notifications?.Show("Re-import failed", ex.Message, NotificationType.Error);
        }
    }

    public async Task SelectJob(Guid jobId)
    {
        if (ImportHistory.Count == 0)
        {
            await LoadHistoryAsync();
        }

        var job = ImportHistory.FirstOrDefault(j => j.Id == jobId);
        if (job != null)
        {
            SelectedJob = job;
        }
    }
}
