using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class ProjectPickerDialog : Window, INotifyPropertyChanged
{
    private PlaylistJob? _selectedProject;
    public ObservableCollection<PlaylistJob> Projects { get; } = new();

    public PlaylistJob? SelectedProject
    {
        get => _selectedProject;
        set 
        {
            if (SetProperty(ref _selectedProject, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool CanSave => SelectedProject != null;

    public ProjectPickerDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public ProjectPickerDialog(IEnumerable<PlaylistJob> projects)
    {
        InitializeComponent();
        foreach (var p in projects) Projects.Add(p);
        DataContext = this;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Close(SelectedProject);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
