using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SLSKDONET.Services;

/// <summary>
/// Service for managing drag adorners (ghost items) during drag-and-drop operations.
/// </summary>
public class DragAdornerService
{
    private Control? _ghostControl;
    private AdornerLayer? _adornerLayer;
    private Control? _rootControl;

    /// <summary>
    /// Shows a ghost control that follows the mouse during drag.
    /// </summary>
    public void ShowGhost(Control sourceControl, Control rootControl)
    {
        _rootControl = rootControl;
        _adornerLayer = AdornerLayer.GetAdornerLayer(rootControl);
        
        if (_adornerLayer == null)
            return;

        // Create a visual clone of the source control
        _ghostControl = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 29, 185, 84)), // Spotify green
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8),
            Child = new TextBlock
            {
                Text = (sourceControl.DataContext as ViewModels.PlaylistTrackViewModel)?.Title ?? "Track",
                Foreground = Brushes.White,
                FontWeight = FontWeight.SemiBold
            },
            Opacity = 0.8
        };

        AdornerLayer.SetAdornedElement(_ghostControl, rootControl);
        _adornerLayer.Children.Add(_ghostControl);
    }

    /// <summary>
    /// Moves the ghost control to follow the mouse.
    /// </summary>
    public void MoveGhost(Point position)
    {
        if (_ghostControl != null && _adornerLayer != null)
        {
            Canvas.SetLeft(_ghostControl, position.X + 10);
            Canvas.SetTop(_ghostControl, position.Y + 10);
        }
    }

    /// <summary>
    /// Hides and removes the ghost control.
    /// </summary>
    public void HideGhost()
    {
        if (_ghostControl != null && _adornerLayer != null)
        {
            _adornerLayer.Children.Remove(_ghostControl);
            _ghostControl = null;
            _adornerLayer = null;
            _rootControl = null;
        }
    }
}
