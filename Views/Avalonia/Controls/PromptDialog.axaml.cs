using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class PromptDialog : Window, INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _message = string.Empty;
    private string _inputText = string.Empty;

    public string PromptTitle { get => _title; set => SetProperty(ref _title, value); }
    public string Message { get => _message; set => SetProperty(ref _message, value); }
    public string InputText { get => _inputText; set => SetProperty(ref _inputText, value); }

    public bool IsConfirmed { get; private set; }
    
    // Required for XAML loader / Previewer
    public PromptDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public PromptDialog(string title, string message, string initialValue = "")
    {
        InitializeComponent();
        PromptTitle = title;
        Message = message;
        InputText = initialValue;
        DataContext = this;
        
        var inputBox = this.FindControl<TextBox>("InputBox");
        inputBox?.Focus();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        Close(InputText);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
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
