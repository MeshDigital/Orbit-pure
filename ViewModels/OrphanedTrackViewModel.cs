using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel for displaying orphaned tracks (files that no longer exist on disk).
/// </summary>
public class OrphanedTrackViewModel : INotifyPropertyChanged
{
    private readonly LibraryEntryEntity _entity;
    private readonly ILibraryService _libraryService;
    private readonly IDialogService _dialogService;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly System.Collections.ObjectModel.ObservableCollection<OrphanedTrackViewModel> _parentCollection;

    public OrphanedTrackViewModel(
        LibraryEntryEntity entity,
        ILibraryService libraryService,
        IDialogService dialogService,
        IFileInteractionService fileInteractionService,
        System.Collections.ObjectModel.ObservableCollection<OrphanedTrackViewModel> parentCollection)
    {
        _entity = entity;
        _libraryService = libraryService;
        _dialogService = dialogService;
        _fileInteractionService = fileInteractionService;
        _parentCollection = parentCollection;

        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        RepointCommand = new AsyncRelayCommand(RepointAsync);
    }

    public string Artist => _entity.Artist;
    public string Title => _entity.Title;
    public string Album => _entity.Album;
    public string FilePath => _entity.FilePath;
    public DateTime AddedAt => _entity.AddedAt;

    public ICommand RemoveCommand { get; }
    public ICommand RepointCommand { get; }

    private async Task RemoveAsync()
    {
        try
        {
            await _libraryService.DeleteLibraryEntryAsync(_entity.Id);
            _parentCollection.Remove(this);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowAlertAsync("Error", $"Failed to remove track: {ex.Message}");
        }
    }

    private async Task RepointAsync()
    {
        var filters = new[]
        {
            new Services.FileDialogFilter("Audio Files", new System.Collections.Generic.List<string> { "mp3", "flac", "wav", "m4a", "aiff", "ogg", "opus" }),
            new Services.FileDialogFilter("All Files", new System.Collections.Generic.List<string> { "*" })
        };

        var newPath = await _fileInteractionService.OpenFileDialogAsync(
            $"Locate file for: {Artist} – {Title}", filters);

        if (string.IsNullOrEmpty(newPath)) return;

        if (!File.Exists(newPath))
        {
            await _dialogService.ShowAlertAsync("Invalid File", "The selected file does not exist.");
            return;
        }

        try
        {
            var entry = await _libraryService.FindLibraryEntryAsync(_entity.UniqueHash);
            if (entry == null)
            {
                await _dialogService.ShowAlertAsync("Error", "Could not find the library entry to update.");
                return;
            }

            entry.FilePath = newPath;
            await _libraryService.SaveOrUpdateLibraryEntryAsync(entry);

            _entity.FilePath = newPath;
            _parentCollection.Remove(this);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowAlertAsync("Error", $"Failed to repoint track: {ex.Message}");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}