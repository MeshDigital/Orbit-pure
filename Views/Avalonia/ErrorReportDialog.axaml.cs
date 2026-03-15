using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using SLSKDONET.Services;

namespace SLSKDONET.Views.Avalonia;

/// <summary>
/// Phase 12: Error Report Dialog - User-friendly crash reporting for beta testing
/// </summary>
public partial class ErrorReportDialog : Window, INotifyPropertyChanged
{
    private string _errorMessage = string.Empty;
    private string _stackTrace = string.Empty;
    private bool _isTerminating;
    private string _logFilePath = string.Empty;

    public ErrorReportDialog()
    {
        InitializeComponent();
        DataContext = this;

        CopyToClipboardCommand = new RelayCommand(CopyToClipboard);
        CloseCommand = new RelayCommand(CloseDialog);

        // Get log file path
        LogFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SLSKDONET",
            "logs",
            $"log-{DateTime.Now:yyyy-MM-dd}.txt");
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public string StackTrace
    {
        get => _stackTrace;
        set { _stackTrace = value; OnPropertyChanged(); }
    }

    public bool IsTerminating
    {
        get => _isTerminating;
        set { _isTerminating = value; OnPropertyChanged(); OnPropertyChanged(nameof(CloseButtonText)); }
    }

    public string LogFilePath
    {
        get => _logFilePath;
        set { _logFilePath = value; OnPropertyChanged(); }
    }

    public string CloseButtonText => IsTerminating ? "Exit Application" : "Continue";

    public ICommand CopyToClipboardCommand { get; }
    public ICommand CloseCommand { get; }

    private async void CopyToClipboard()
    {
        try
        {
            var report = "ORBIT-Pure Error Report\n" +
                        $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" +
                        $"Error Summary:\n{ErrorMessage}\n\n" +
                        $"Stack Trace:\n{StackTrace}\n\n" +
                        $"Log File: {LogFilePath}\n\n" +
                        $"System Info:\n" +
                        $"OS: {Environment.OSVersion}\n" +
                        $".NET: {Environment.Version}\n" +
                        $"Architecture: {RuntimeInformation.ProcessArchitecture}\n";

            await TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(report);

            // Show brief success feedback
            var originalText = ErrorMessage;
            ErrorMessage = "Report copied to clipboard!";
            await Task.Delay(2000);
            ErrorMessage = originalText;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to copy to clipboard: {ex.Message}";
        }
    }

    private void CloseDialog()
    {
        Close();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}