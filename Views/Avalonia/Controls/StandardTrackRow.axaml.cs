using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using SLSKDONET.ViewModels.Downloads;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class StandardTrackRow : UserControl
{
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<StandardTrackRow, bool>(nameof(IsCompact), defaultValue: false);

    public static readonly StyledProperty<bool> MinimalBadgesProperty =
        AvaloniaProperty.Register<StandardTrackRow, bool>(nameof(MinimalBadges), defaultValue: false);

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public bool MinimalBadges
    {
        get => GetValue(MinimalBadgesProperty);
        set => SetValue(MinimalBadgesProperty, value);
    }

    public StandardTrackRow()
    {
        InitializeComponent();
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is StyledElement sourceElement)
        {
            StyledElement? current = sourceElement;
            while (current != null)
            {
                if (current is Button || current is ToggleButton || current is TextBox || current is CheckBox || current is Slider)
                    return;

                current = current.Parent as StyledElement;
            }
        }

        if (DataContext is UnifiedTrackViewModel track)
        {
            // Set global selection for Inspector
            var page = this.FindAncestorOfType<DownloadsPage>();
            if (page?.DataContext is DownloadCenterViewModel dc)
            {
                dc.SelectedTrack = track;
            }

            // Still allow toggle console if needed? 
            // Better: clicking row opens inspector, small icon toggles log.
            // Keeping toggle console for now as fallback.
            // track.IsConsoleOpen = !track.IsConsoleOpen;
            
            e.Handled = true;
        }
    }

    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is UnifiedTrackViewModel track && track.IsCompleted)
        {
            // Initiate Drag & Drop if left button is pressed
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsLeftButtonPressed)
            {
                var filePath = track.Model.ResolvedFilePath;
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    var dragData = new DataObject();
                    dragData.Set(DataFormats.Files, new[] { filePath });
                    
                    // Add a tiny delay to ensure it's a deliberate drag
                    await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Copy);
                }
            }
        }
    }
}
