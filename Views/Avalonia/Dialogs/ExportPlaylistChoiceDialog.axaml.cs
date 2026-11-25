using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.Models;
using SLSKDONET.Services.Export;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public enum ExportPlaylistChoice
{
    Cancelled,
    XmlOnly,
    CopyFilesAndXml
}

public sealed record ExportPlaylistOptionsResult(ExportPlaylistChoice Choice, UsbLayoutMode? LayoutMode);

public partial class ExportPlaylistChoiceDialog : Window
{
    private readonly string _playlistTitle = string.Empty;
    private readonly IReadOnlyList<PlaylistTrack>? _tracks;
    private readonly bool _returnOptionsResult;

    private static readonly (string Label, UsbLayoutMode? Mode)[] LayoutOptions =
    {
        ("Default (OrbitAudio/)", null),
        ("By Key (08A/)", UsbLayoutMode.ByKey),
        ("By BPM bracket (120-129/)", UsbLayoutMode.ByBpmBracket),
        ("By Genre", UsbLayoutMode.ByGenre),
        ("By Playlist", UsbLayoutMode.ByPlaylist),
    };

    public ExportPlaylistChoiceDialog()
    {
        InitializeComponent();
    }

    public ExportPlaylistChoiceDialog(string playlistTitle)
    {
        InitializeComponent();
        _playlistTitle = playlistTitle;
        SubtitleText.Text = $"How do you want to export \"{playlistTitle}\"?";
    }

    public ExportPlaylistChoiceDialog(string playlistTitle, IReadOnlyList<PlaylistTrack> tracks)
        : this(playlistTitle)
    {
        _tracks = tracks;
        _returnOptionsResult = true;

        LayoutPreviewPanel.IsVisible = true;
        var labels = new List<string>(LayoutOptions.Length);
        foreach (var opt in LayoutOptions)
            labels.Add(opt.Label);
        LayoutComboBox.ItemsSource = labels;
        LayoutComboBox.SelectedIndex = 0;
        UpdatePreview();
    }

    public UsbLayoutMode? SelectedLayoutMode =>
        LayoutComboBox.SelectedIndex >= 0 && LayoutComboBox.SelectedIndex < LayoutOptions.Length
            ? LayoutOptions[LayoutComboBox.SelectedIndex].Mode
            : null;

    private void LayoutComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_tracks is null) return;
        var summary = UsbExportPlanner.BuildPreviewSummary(_playlistTitle, _tracks, SelectedLayoutMode);
        PreviewSummaryText.Text = summary.TrackCount == 0
            ? "No downloaded files"
            : $"{summary.TrackCount} {(summary.TrackCount == 1 ? "track" : "tracks")} · {summary.FolderCount} {(summary.FolderCount == 1 ? "folder" : "folders")} · {summary.TotalSizeDisplay}";
        PreviewTreeText.Text = summary.FolderLines.Count == 0
            ? "(No local audio files found for this playlist)"
            : string.Join("\n", summary.FolderLines);
    }

    private void CopyFilesAndXml_Click(object? sender, RoutedEventArgs e)
    {
        if (_returnOptionsResult)
            Close(new ExportPlaylistOptionsResult(ExportPlaylistChoice.CopyFilesAndXml, SelectedLayoutMode));
        else
            Close(ExportPlaylistChoice.CopyFilesAndXml);
    }

    private void XmlOnly_Click(object? sender, RoutedEventArgs e)
    {
        if (_returnOptionsResult)
            Close(new ExportPlaylistOptionsResult(ExportPlaylistChoice.XmlOnly, null));
        else
            Close(ExportPlaylistChoice.XmlOnly);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_returnOptionsResult)
            Close(new ExportPlaylistOptionsResult(ExportPlaylistChoice.Cancelled, null));
        else
            Close(ExportPlaylistChoice.Cancelled);
    }
}
