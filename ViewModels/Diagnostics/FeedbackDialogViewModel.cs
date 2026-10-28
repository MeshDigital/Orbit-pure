using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Diagnostics;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Diagnostics;

/// <summary>
/// "Send feedback": the tester describes the problem, ORBIT zips screenshot + recent logs, then
/// helps them hand the zip to WhatsApp or their mail app. Neither accepts an attachment through a
/// link, so the zip goes on the clipboard (paste with Ctrl+V) with Show in folder as the fallback.
/// </summary>
public sealed class FeedbackDialogViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly FeedbackReportService _reports;
    private readonly ILogger _logger;
    private readonly byte[]? _screenshotPng;
    private readonly AsyncRelayCommand _createReportCommand;

    public FeedbackDialogViewModel(FeedbackReportService reports, ILogger logger, byte[]? screenshotPng)
    {
        _reports = reports;
        _logger = logger;
        _screenshotPng = screenshotPng is { Length: > 0 } ? screenshotPng : null;
        _includeScreenshot = _screenshotPng != null;

        if (_screenshotPng != null)
        {
            try
            {
                using var stream = new MemoryStream(_screenshotPng);
                ScreenshotPreview = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feedback: couldn't decode the screenshot preview");
            }
        }

        _createReportCommand = new AsyncRelayCommand(CreateReportAsync, () => !IsBusy && !IsReportReady);
        CreateReportCommand = _createReportCommand;
        ShareWhatsAppCommand = new AsyncRelayCommand(ShareViaWhatsAppAsync, () => IsReportReady);
        ShareEmailCommand = new AsyncRelayCommand(ShareViaEmailAsync, () => IsReportReady);
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => IsReportReady);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CloseRequested;

    /// <summary>Puts a file on the clipboard; set by the dialog (needs its TopLevel). Returns false when it couldn't.</summary>
    internal Func<string, Task<bool>> CopyFileToClipboard { get; set; } = _ => Task.FromResult(false);

    /// <summary>Hands a URI to the OS. Returns false when nothing handles it.</summary>
    internal Func<string, bool> OpenUri { get; set; } = DefaultOpenUri;

    internal Action<string> RevealInFolder { get; set; } = DefaultRevealInFolder;

    public ICommand CreateReportCommand { get; }
    public ICommand ShareWhatsAppCommand { get; }
    public ICommand ShareEmailCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand CloseCommand { get; }

    public Bitmap? ScreenshotPreview { get; }
    public bool HasScreenshot => ScreenshotPreview != null;

    private string _note = string.Empty;
    public string Note { get => _note; set => Set(ref _note, value ?? string.Empty); }

    private bool _includeScreenshot;
    public bool IncludeScreenshot { get => _includeScreenshot; set => Set(ref _includeScreenshot, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) _createReportCommand.RaiseCanExecuteChanged();
        }
    }

    private FeedbackReport? _report;
    public FeedbackReport? Report
    {
        get => _report;
        private set
        {
            if (!Set(ref _report, value)) return;
            OnPropertyChanged(nameof(IsReportReady));
            OnPropertyChanged(nameof(IsComposing));
            OnPropertyChanged(nameof(ReportFileName));
            OnPropertyChanged(nameof(ReportSize));
            _createReportCommand.RaiseCanExecuteChanged();
            (ShareWhatsAppCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ShareEmailCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ShowInFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsReportReady => Report != null;
    public bool IsComposing => Report == null;
    public string ReportFileName => Report == null ? string.Empty : Path.GetFileName(Report.ZipPath);
    public string ReportSize => Report == null ? string.Empty : NumFormat.Size(Report.SizeBytes);

    private string? _statusText;
    /// <summary>What the last share action did, or why the report failed.</summary>
    public string? StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }
    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    internal async Task CreateReportAsync()
    {
        IsBusy = true;
        SetStatus(null);
        try
        {
            var request = new FeedbackReportRequest(Note, IncludeScreenshot ? _screenshotPng : null);
            Report = await _reports.CreateReportAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Feedback: creating the report failed");
            SetStatus($"Couldn't create the report: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ShareViaWhatsAppAsync()
    {
        var copied = await CopyReportAsync();
        var text = Uri.EscapeDataString($"ORBIT feedback ({AppVersion}). {ShortNote(300)}".Trim());

        // The desktop app registers whatsapp:; without it, WhatsApp Web.
        if (!OpenUri($"whatsapp://send?text={text}") && !OpenUri($"https://web.whatsapp.com/send?text={text}"))
        {
            SetStatus("Couldn't open WhatsApp. Send the zip from the folder instead.", isError: true);
            ShowInFolder();
            return;
        }
        ReportShareOutcome(copied, "chat");
    }

    internal async Task ShareViaEmailAsync()
    {
        var copied = await CopyReportAsync();
        var subject = Uri.EscapeDataString($"ORBIT feedback ({AppVersion})");
        var body = Uri.EscapeDataString(
            "What happened:\n" + (ShortNote(1000) is { Length: > 0 } note ? note : "(describe it here)") +
            "\n\nThe report zip (" + ReportFileName + ") is attached.\n");

        if (!OpenUri($"mailto:?subject={subject}&body={body}"))
        {
            SetStatus("No mail app is set up on this PC. Send the zip from the folder instead.", isError: true);
            ShowInFolder();
            return;
        }
        ReportShareOutcome(copied, "mail");
    }

    private void ReportShareOutcome(bool copied, string target)
    {
        if (copied)
        {
            SetStatus($"The zip is on your clipboard. Paste it into the {target} with Ctrl+V.");
        }
        else
        {
            // Without the clipboard the user has to drag the file in themselves.
            ShowInFolder();
            SetStatus($"Drag the zip from the folder that just opened into the {target}.");
        }
    }

    private async Task<bool> CopyReportAsync()
    {
        if (Report == null) return false;
        try
        {
            return await CopyFileToClipboard(Report.ZipPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Feedback: couldn't put the report on the clipboard");
            return false;
        }
    }

    private void ShowInFolder()
    {
        if (Report != null) RevealInFolder(Report.ZipPath);
    }

    private string ShortNote(int max)
    {
        var note = Note.Trim();
        return note.Length <= max ? note : note[..max] + "…";
    }

    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "unknown version";

    private void SetStatus(string? text, bool isError = false)
    {
        StatusIsError = isError;
        StatusText = text;
    }

    private static bool DefaultOpenUri(string uri)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Information(ex, "Feedback: nothing handles {Scheme}", uri.Split(':')[0]);
            return false;
        }
    }

    private static void DefaultRevealInFolder(string path)
    {
        try
        {
            using var _ = Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Feedback: couldn't open the report folder for {Path}", path);
        }
    }

    public void Dispose() => ScreenshotPreview?.Dispose();

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
