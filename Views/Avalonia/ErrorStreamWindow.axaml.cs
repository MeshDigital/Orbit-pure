using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ReactiveUI;

namespace SLSKDONET.Views.Avalonia;

public class ErrorItem
{
    public DateTime Timestamp { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
}

public partial class ErrorStreamWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ErrorItem> Errors { get; } = new();
    public int ErrorCount => Errors.Count;

    public ReactiveCommand<Unit, Unit> OpenLogsCommand { get; }
    public ReactiveCommand<ErrorItem, Unit> CopyErrorCommand { get; }
    public ReactiveCommand<ErrorItem, Unit> ClearErrorCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAllCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public ErrorStreamWindow()
    {
        InitializeComponent();
        DataContext = this;

        OpenLogsCommand = ReactiveCommand.Create(OpenLogs);
        CopyErrorCommand = ReactiveCommand.Create<ErrorItem>(CopyError);
        ClearErrorCommand = ReactiveCommand.Create<ErrorItem>(ClearError);
        ClearAllCommand = ReactiveCommand.Create(ClearAll);
        CloseCommand = ReactiveCommand.Create(CloseWindow);
    }

    public void AddError(string source, string message, string stackTrace)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            Errors.Insert(0, new ErrorItem
            {
                Timestamp = DateTime.Now,
                Source = source,
                Message = message,
                StackTrace = stackTrace
            });
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorCount)));
        });
    }

    private void OpenLogs()
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORBIT", "logs");
            if (!Directory.Exists(logDir))
            {
                logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // Fallback: try to open the app directory
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Last resort: show message
                var messageBox = new Window
                {
                    Title = "Error",
                    Content = new TextBlock { Text = $"Could not open logs folder: {ex.Message}" },
                    SizeToContent = SizeToContent.WidthAndHeight
                };
                messageBox.Show();
            }
        }
    }

    private async void CopyError(ErrorItem error)
    {
        if (error == null) return;

        var text = $"Source: {error.Source}\nTimestamp: {error.Timestamp}\nMessage: {error.Message}\n\nStack Trace:\n{error.StackTrace}";
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel != null)
        {
            await topLevel.Clipboard?.SetTextAsync(text);
        }
    }

    private void ClearError(ErrorItem error)
    {
        if (error != null)
        {
            Errors.Remove(error);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorCount)));
        }
    }

    private void ClearAll()
    {
        Errors.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorCount)));
    }

    private void CloseWindow()
    {
        Hide();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}