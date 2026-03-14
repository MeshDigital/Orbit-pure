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
        DownloadManager downloadManager)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;

        LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync);
        ReImportCommand = new RelayCommand<PlaylistJob>(ExecuteReImport);

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

    private void ExecuteReImport(PlaylistJob? job)
    {
        if (job == null) return;

        _logger.LogInformation("Re-Import requested for job: {Title} ({Id})", job.SourceTitle, job.Id);

        // Logic: 
        // 1. Reset failed/missing tracks to Missing?
        // 2. Queue project in DownloadManager
        
        // For now, we assume the user wants to retry downloading missing/failed tracks
        // We can just QueueProject anew? 
        // Or better: Iterate tracks and reset if needed?
        
        // Simpler approach: Just queue it. DownloadManager should handle it.
        // If track exists and is downloaded, it skips.
        // If track is missing, it queues it.
        
        // Check if job is already active?
        // If it was "Deleted" (soft), we might need to un-delete it?
        // The job object here is from "GetHistoricalJobsAsync".
        
        // if (job.IsDeleted) 
        // {
             // If we expose IsDeleted in Model, check it.
             // Assume active for now or handle in QueueProject.
        // }

        _ = _downloadManager.QueueProject(job);
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
