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

    // Library Folders
    public ObservableCollection<LibraryFolderViewModel> LibraryFolders { get; } = new();

    private string _scanStatus = string.Empty;
    public string ScanStatus
    {
        get => _scanStatus;
        set => SetProperty(ref _scanStatus, value);
    }

    public ICommand AddLibraryFolderCommand { get; }
    public ICommand RemoveLibraryFolderCommand { get; }
    public ICommand ScanAllLibraryFoldersCommand { get; }

    public LibrarySourcesViewModel(
        ILogger<LibrarySourcesViewModel> logger,
        LibraryFolderScannerService libraryFolderScannerService,
        IFileInteractionService fileInteractionService,
        IEventBus eventBus)
    {
        _logger = logger;
        _libraryFolderScannerService = libraryFolderScannerService;
        _fileInteractionService = fileInteractionService;
        _eventBus = eventBus;

        AddLibraryFolderCommand = new AsyncRelayCommand(AddLibraryFolderAsync);
        RemoveLibraryFolderCommand = new RelayCommand<LibraryFolderViewModel?>(RemoveLibraryFolder);
        ScanAllLibraryFoldersCommand = new AsyncRelayCommand(ScanAllLibraryFoldersAsync);

        // Load existing on init
        _ = LoadLibraryFoldersAsync();
        
        // Phase 0.10: Sync
        _foldersChangedSubscription = _eventBus.GetEvent<LibraryFoldersChangedEvent>().Subscribe(e => { _ = LoadLibraryFoldersAsync(); });
    }

    private async Task LoadLibraryFoldersAsync()
    {
        try
        {
            using var context = new AppDbContext();
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

    private async Task AddLibraryFolderAsync()
    {
        try
        {
            var folderPath = await _fileInteractionService.OpenFolderDialogAsync("Select Library Folder");
            if (string.IsNullOrEmpty(folderPath)) return;

            using var context = new AppDbContext();

            // Check if folder already exists
            var exists = await context.LibraryFolders.AnyAsync(f => f.FolderPath == folderPath);

            if (exists)
            {
                _logger.LogWarning("Folder already added: {Path}", folderPath);
                return;
            }

            var folderEntity = new Data.Entities.LibraryFolderEntity
            {
                Id = Guid.NewGuid(),
                FolderPath = folderPath,
                IsEnabled = true,
                AddedAt = DateTime.UtcNow
            };

            context.LibraryFolders.Add(folderEntity);
            await context.SaveChangesAsync();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LibraryFolders.Add(new LibraryFolderViewModel(folderEntity));
            });

            _logger.LogInformation("Added library folder: {Path}", folderPath);
            _eventBus.Publish(new LibraryFoldersChangedEvent());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add library folder");
        }
    }

    private async void RemoveLibraryFolder(LibraryFolderViewModel? folderVm)
    {
        if (folderVm == null) return;

        try
        {
            using var context = new AppDbContext();
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

    private async Task ScanAllLibraryFoldersAsync()
    {
        try
        {
            ScanStatus = "Scanning...";
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanStatus = $"Found: {p.FilesDiscovered} | Imported: {p.FilesImported} | Skipped: {p.FilesSkipped}";
            });

            // Use batch writes implicitly handled by refactored service (TODO)
            var results = await _libraryFolderScannerService.ScanAllFoldersAsync(progress);

            var totalImported = results.Values.Sum(r => r.FilesImported);
            var totalSkipped = results.Values.Sum(r => r.FilesSkipped);

            ScanStatus = $"✅ Complete! Imported: {totalImported}, Skipped: {totalSkipped}";

            _logger.LogInformation("Library folder scan complete: {Imported} imported, {Skipped} skipped",
                totalImported, totalSkipped);

            // Clear status after 5 seconds
            await Task.Delay(5000);
            ScanStatus = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan library folders");
            ScanStatus = "❌ Scan failed";
            await Task.Delay(3000);
            ScanStatus = string.Empty;
        }
    }

    public void Dispose()
    {
        _foldersChangedSubscription?.Dispose();
    }
}
