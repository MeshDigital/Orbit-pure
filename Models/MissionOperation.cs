using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SLSKDONET.Models;

public enum OperationType
{
    Download,
    Search,
    Analysis,
    Enrichment,
    System
}

public class MissionOperation : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString();
    public string Id 
    { 
        get => _id; 
        set => SetField(ref _id, value); 
    }

    private OperationType _type;
    public OperationType Type 
    { 
        get => _type; 
        set => SetField(ref _type, value); 
    }

    private string _title = string.Empty;
    public string Title 
    { 
        get => _title; 
        set => SetField(ref _title, value); 
    }

    private string _subtitle = string.Empty;
    public string Subtitle 
    { 
        get => _subtitle; 
        set => SetField(ref _subtitle, value); 
    }

    private double _progress;
    public double Progress 
    { 
        get => _progress; 
        set => SetField(ref _progress, value); 
    }

    private string _statusText = string.Empty;
    public string StatusText 
    { 
        get => _statusText; 
        set => SetField(ref _statusText, value); 
    }
    
    // Optional reference to a Track ViewModel or ActiveThreadInfo
    private object? _track;
    public object? Track 
    { 
        get => _track; 
        set => SetField(ref _track, value); 
    }
    
    private string _name = string.Empty;
    public string Name
    {
        get => string.IsNullOrEmpty(_name) ? _title : _name;
        set => SetField(ref _name, value);
    }

    private string _icon = string.Empty;
    public string Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

    private bool _canCancel;
    public bool CanCancel 
    { 
        get => _canCancel; 
        set => SetField(ref _canCancel, value); 
    }

    private bool _canPromote;
    public bool CanPromote 
    { 
        get => _canPromote; 
        set => SetField(ref _canPromote, value); 
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
