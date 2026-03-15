using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private readonly System.Collections.ObjectModel.ObservableCollection<OrphanedTrackViewModel> _parentCollection;

    public OrphanedTrackViewModel(LibraryEntryEntity entity, ILibraryService libraryService, IDialogService dialogService, System.Collections.ObjectModel.ObservableCollection<OrphanedTrackViewModel> parentCollection)
    {
        _entity = entity;
        _libraryService = libraryService;
        _dialogService = dialogService;
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
        // TODO: Implement file picker dialog to select new path
        await _dialogService.ShowAlertAsync("Not Implemented", "Repoint functionality will be implemented in the next update.");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}