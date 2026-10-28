using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public enum ExportPlaylistChoice
{
    Cancelled,
    XmlOnly,
    CopyFilesAndXml
}

public partial class ExportPlaylistChoiceDialog : Window
{
    public ExportPlaylistChoiceDialog()
    {
        InitializeComponent();
    }

    public ExportPlaylistChoiceDialog(string playlistTitle)
    {
        InitializeComponent();
        SubtitleText.Text = $"How do you want to export \"{playlistTitle}\"?";
    }

    private void CopyFilesAndXml_Click(object? sender, RoutedEventArgs e) => Close(ExportPlaylistChoice.CopyFilesAndXml);

    private void XmlOnly_Click(object? sender, RoutedEventArgs e) => Close(ExportPlaylistChoice.XmlOnly);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(ExportPlaylistChoice.Cancelled);
}
