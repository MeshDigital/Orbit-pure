using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;
using Serilog;

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
        // Log every error to the persistent log file
        Serilog.Log.Error("🚨 UI Error Stream - {Source}: {Message}\n{StackTrace}", source, message, stackTrace);

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
            string logDirectory = null;

            // Try multiple possible log locations in order of preference
            string[] possiblePaths = {
                // Development: project root logs (when running from VS Code/dotnet run)
                Path.Combine(Directory.GetCurrentDirectory(), "logs"),
                // Development: go up from bin directory
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "logs"),
                // Production: user app data
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORBIT", "logs"),
                // Fallback: app data roaming
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "logs"),
                // Last resort: bin directory logs
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs")
            };

            foreach (var path in possiblePaths)
            {
                if (Directory.Exists(path) && Directory.GetFiles(path, "*.json").Length > 0)
                {
                    logDirectory = path;
                    break;
                }
            }

            // If no existing log directory found, create one in the most appropriate location
            if (logDirectory == null)
            {
                // Check if we're in development (running from source)
                bool isDevelopment = Directory.GetCurrentDirectory().Contains("GitHub") || 
                                   Directory.GetCurrentDirectory().Contains("source") ||
                                   File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "SLSKDONET.csproj"));
                
                logDirectory = isDevelopment 
                    ? Path.Combine(Directory.GetCurrentDirectory(), "logs")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORBIT", "logs");
                
                Directory.CreateDirectory(logDirectory);
            }

            // Use explorer.exe to open the directory
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{logDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // Fallback: show error message
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var messageBox = new Window
                {
                    Title = "Error Opening Logs",
                    Content = new TextBlock { 
                        Text = $"Could not open logs folder: {ex.Message}\n\nLog directory detection failed. Please check manually in the application directory or %LOCALAPPDATA%\\ORBIT\\logs",
                        TextWrapping = TextWrapping.Wrap 
                    },
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                messageBox.Show();
            });
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