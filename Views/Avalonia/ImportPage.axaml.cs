using Avalonia.Controls;
using Avalonia.Input;
using System;
using System.Linq;

namespace SLSKDONET.Views.Avalonia;

/// <summary>
/// Phase 6D: Import page for Spotify, CSV, and USB imports.
/// </summary>
public partial class ImportPage : UserControl
{
    public ImportPage()
    {
        InitializeComponent();
        
        var dropZone = this.FindControl<Border>("CsvDropZone");
        if (dropZone != null)
        {
            dropZone.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
            dropZone.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            dropZone.AddHandler(DragDrop.DropEvent, OnDrop);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files) && DataContext is ViewModels.SpotifyImportViewModel vm)
        {
            vm.IsCsvDragging = true;
            if (sender is Border b) b.Tag = "Dragging";
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DataContext is ViewModels.SpotifyImportViewModel vm)
        {
            vm.IsCsvDragging = false;
            if (sender is Border b) b.Tag = null;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is ViewModels.SpotifyImportViewModel vm)
        {
            vm.IsCsvDragging = false;
            if (sender is Border b) b.Tag = null;
            
            var files = e.Data.GetFiles();
            if (files != null && files.Any())
            {
                var file = files.First();
                var path = file.Path.LocalPath;
                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    await vm.ProcessCsvFileAsync(path);
                }
            }
        }
    }

    public ImportPage(ViewModels.SpotifyImportViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
