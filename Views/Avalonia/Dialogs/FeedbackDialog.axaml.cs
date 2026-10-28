using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using SLSKDONET.ViewModels.Diagnostics;

namespace SLSKDONET.Views.Avalonia.Dialogs;

/// <summary>
/// Layout and bindings live in XAML; this file only supplies what needs the window itself:
/// closing, and putting the report zip on the clipboard as a file.
/// </summary>
public partial class FeedbackDialog : Window
{
    public FeedbackDialog()
    {
        InitializeComponent();
    }

    public FeedbackDialog(FeedbackDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CopyFileToClipboard = CopyFileToClipboardAsync;
        vm.CloseRequested += OnCloseRequested;
        Closed += (_, _) => vm.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private async Task<bool> CopyFileToClipboardAsync(string path)
    {
        if (Clipboard == null) return false;
        var file = await StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        if (file == null) return false;

        var data = new DataObject();
        data.Set(DataFormats.Files, new[] { file });
        await Clipboard.SetDataObjectAsync(data);

        // Only claim success when the file actually landed there; otherwise the caller falls back
        // to opening the folder.
        var formats = await Clipboard.GetFormatsAsync();
        return Array.IndexOf(formats, DataFormats.Files) >= 0;
    }
}
