using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.ViewModels;

public class LibraryFolderViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isWatched;

    public Guid Id { get; }
    public string FolderPath { get; }

    public bool IsWatched
    {
        get => _isWatched;
        set
        {
            if (_isWatched == value) return;
            _isWatched = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWatched)));
        }
    }

    public LibraryFolderViewModel(LibraryFolderEntity entity)
    {
        Id = entity.Id;
        FolderPath = entity.FolderPath;
        _isWatched = entity.IsWatched;
    }
}
