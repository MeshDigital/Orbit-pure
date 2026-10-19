using System;
using System.Collections.Generic; // Added for EqualityComparer
using System.Collections.ObjectModel;
using System.ComponentModel; // Added for INotifyPropertyChanged
using System.Linq;
using System.Runtime.CompilerServices; // Added for CallerMemberName
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Views; // For AsyncRelayCommand (if strict match needed)
using SLSKDONET.Models; // For Events

namespace SLSKDONET.ViewModels;

public class LibrarySourcesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IDisposable? _foldersChangedSubscription;
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private readonly ILogger<LibrarySourcesViewModel> _logger;
    private readonly LibraryFolderScannerService _libraryFolderScannerService;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly IEventBus _eventBus;
    private readonly IDbContextFactory<Data.AppDbContext> _dbFactory;
    private readonly AppConfig _config;
    private readonly ILibraryImportLauncher _importLauncher;

    // Library Folders
    public ObservableCollection<LibraryFolderViewModel> LibraryFolders { get; } = new();

    public ICommand AddLibraryFolderCommand { get; }
    public ICommand RemoveLibraryFolderCommand { get; }
    public ICommand ScanAllLibraryFoldersCommand { get; }
    public ICommand ToggleWatchCommand { get; }

    public LibrarySourcesViewModel(
        ILogger<LibrarySourcesViewModel> logger,
        LibraryFolderScannerService libraryFolderScannerService,
        IFileInteractionService fileInteractionService,
        IEventBus eventBus,
        IDbContextFactory<Data.AppDbContext> dbFactory,
        AppConfig config,
        ILibraryImportLauncher importLauncher)
    {
        _logger = logger;
        _libraryFolderScannerService = libraryFolderScannerService;
        _fileInteractionService = fileInteractionService;
        _eventBus = eventBus;
        _dbFactory = dbFactory;
        _config = config;
        _importLauncher = importLauncher;

        AddLibraryFolderCommand = new AsyncRelayCommand(AddLibraryFolderAsync);
        RemoveLibraryFolderCommand = new RelayCommand<LibraryFolderViewModel?>(RemoveLibraryFolder);
        ScanAllLibraryFoldersCommand = new AsyncRelayCommand(ScanAllLibraryFoldersAsync);
        ToggleWatchCommand = new AsyncRelayCommand<LibraryFolderViewModel?>(ToggleWatchAsync);

        // Load existing on init
        _ = LoadLibraryFoldersAsync();
        
        // Phase 0.10: Sync
        _foldersChangedSubscription = _eventBus.GetEvent<LibraryFoldersChangedEvent>().Subscribe(e => { _ = LoadLibraryFoldersAsync(); });
    }

    private async Task LoadLibraryFoldersAsync()
    {
        try
        {
            await using var context = _dbFactory.CreateDbContext();
            var folders = await context.LibraryFolders.ToListAsync();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LibraryFolders.Clear();
                foreach (var folder in folders)
                {
                    LibraryFolders.Add(new LibraryFolderViewModel(folder));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load library folders");
        }
    }

    /// <summary>Folder picker, then the import sheet (registers the folder and imports it).</summary>
    private Task AddLibraryFolderAsync() => _importLauncher.PickFolderAndStartImportAsync();

    private async Task ToggleWatchAsync(LibraryFolderViewModel? folderVm)
    {
        if (folderVm == null) return;

        try
        {
            var newValue = !folderVm.IsWatched;
            await using var context = _dbFactory.CreateDbContext();
            var folder = await context.LibraryFolders.FindAsync(folderVm.Id);
            if (folder == null) return;

            folder.IsWatched = newValue;
            await context.SaveChangesAsync();

            Avalonia.Threading.Dispatcher.UIThread.Post(() => folderVm.IsWatched = newValue);

            _logger.LogInformation("Library folder watch {State}: {Path}", newValue ? "enabled" : "disabled", folderVm.FolderPath);
            _eventBus.Publish(new LibraryFoldersChangedEvent());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle folder watch state");
        }
    }

    private async void RemoveLibraryFolder(LibraryFolderViewModel? folderVm)
    {
        if (folderVm == null) return;

        try
        {
            await using var context = _dbFactory.CreateDbContext();
            var folder = await context.LibraryFolders.FindAsync(folderVm.Id);
            if (folder != null)
            {
                context.LibraryFolders.Remove(folder);
                await context.SaveChangesAsync();

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    LibraryFolders.Remove(folderVm);
                });

                _logger.LogInformation("Removed library folder: {Path}", folderVm.FolderPath);
                _eventBus.Publish(new LibraryFoldersChangedEvent());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove library folder");
        }
    }

    /// <summary>"Scan all" opens the import sheet over every enabled Library Source.</summary>
    private async Task ScanAllLibraryFoldersAsync()
    {
        try
        {
            // Auto-register configured paths so all drives are included even if the
            // user hasn't manually added them via the Library Folders panel.
            if (!string.IsNullOrEmpty(_config.DownloadDirectory))
                await _libraryFolderScannerService.EnsureDefaultFolderAsync(_config.DownloadDirectory);

            if (!string.IsNullOrEmpty(_config.SharedFolderPath) &&
                !string.Equals(_config.SharedFolderPath, _config.DownloadDirectory, StringComparison.OrdinalIgnoreCase))
                await _libraryFolderScannerService.EnsureDefaultFolderAsync(_config.SharedFolderPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not register the download/shared folders before scanning");
        }

        await _importLauncher.StartImportAsync(null, "Scanning your library folders");
    }

    public void Dispose()
    {
        _foldersChangedSubscription?.Dispose();
    }
}
