using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class QueuePanel : UserControl
{
    private readonly DragAdornerService _dragAdorner = new();
    private Point? _dragStartPoint;
    private Control? _insertionLine;

    public QueuePanel()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        
        // Find the ListBox and attach pointer events to items
        var listBox = this.FindDescendantOfType<ListBox>();
        if (listBox != null)
        {
            listBox.AddHandler(PointerPressedEvent, OnItemPointerPressed, RoutingStrategies.Tunnel);
            listBox.AddHandler(PointerMovedEvent, OnItemPointerMoved, RoutingStrategies.Tunnel);
            listBox.AddHandler(PointerReleasedEvent, OnItemPointerReleased, RoutingStrategies.Tunnel);
            
            DragDrop.SetAllowDrop(listBox, true);
        }
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var item = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>();
            if (item?.DataContext is PlaylistTrackViewModel)
            {
                _dragStartPoint = e.GetPosition(this);
            }
        }
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartPoint.HasValue && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var currentPoint = e.GetPosition(this);
            var diff = currentPoint - _dragStartPoint.Value;
            
            // Check if moved past threshold (5 pixels)
            if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
            {
                var item = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>();
                if (item?.DataContext is PlaylistTrackViewModel track)
                {
                    // Show ghost
                    _dragAdorner.ShowGhost(item, this);
                    
                    // Create drag data
                    var data = new DataObject();
                    data.Set(DragContext.QueueTrackFormat, track.GlobalId);
                    
                    // Start drag operation
                    await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
                    
                    // Clean up
                    _dragAdorner.HideGhost();
                    HideInsertionLine();
                    _dragStartPoint = null;
                }
            }
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStartPoint = null;
        _dragAdorner.HideGhost();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Accept tracks from queue (for reordering) or library (for adding)
        if (e.Data.Contains(DragContext.QueueTrackFormat) || e.Data.Contains(DragContext.LibraryTrackFormat))
        {
            e.DragEffects = e.Data.Contains(DragContext.QueueTrackFormat) 
                ? DragDropEffects.Move 
                : DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        
        // Calculate insertion point and show visual feedback (only for queue reordering)
        if (e.Data.Contains(DragContext.QueueTrackFormat))
        {
            var listBox = this.FindDescendantOfType<ListBox>();
            if (listBox != null)
            {
                var position = e.GetPosition(listBox);
                ShowInsertionLine(listBox, position);
            }
        }
        
        // Update ghost position
        _dragAdorner.MoveGhost(e.GetPosition(this));
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        string? trackHash = null;
        bool isFromLibrary = false;
        
        // Check if it's from library or queue
        if (e.Data.Contains(DragContext.LibraryTrackFormat))
        {
            trackHash = e.Data.Get(DragContext.LibraryTrackFormat) as string;
            isFromLibrary = true;
        }
        else if (e.Data.Contains(DragContext.QueueTrackFormat))
        {
            trackHash = e.Data.Get(DragContext.QueueTrackFormat) as string;
            isFromLibrary = false;
        }

        if (string.IsNullOrEmpty(trackHash))
            return;

        var listBox = this.FindDescendantOfType<ListBox>();
        if (listBox == null || DataContext is not PlayerViewModel playerViewModel)
            return;

        if (isFromLibrary)
        {
            // Adding from library - find the track and add to queue
            var mainWindow = this.VisualRoot as MainWindow;
            var mainViewModel = mainWindow?.DataContext as MainViewModel;
            var track = mainViewModel?.AllGlobalTracks
                .FirstOrDefault(t => t.GlobalId == trackHash);
            
            if (track != null)
            {
                playerViewModel.AddToQueue(track);
            }
        }
        else
        {
            // Reordering within queue
            var targetIndex = CalculateDropIndex(listBox, e.GetPosition(listBox));
            playerViewModel.MoveTrack(trackHash, targetIndex);
        }

        // Clean up
        HideInsertionLine();
        _dragAdorner.HideGhost();
    }

    private int CalculateDropIndex(ListBox listBox, Point position)
    {
        for (int i = 0; i < listBox.ItemCount; i++)
        {
            var container = listBox.ContainerFromIndex(i) as ListBoxItem;
            if (container != null)
            {
                var bounds = container.Bounds;
                var relativeY = position.Y - bounds.Y;
                
                if (relativeY < bounds.Height / 2)
                    return i;
            }
        }
        
        return Math.Max(0, listBox.ItemCount - 1);
    }

    private void ShowInsertionLine(ListBox listBox, Point position)
    {
        HideInsertionLine();
        
        var targetIndex = CalculateDropIndex(listBox, position);
        var container = listBox.ContainerFromIndex(targetIndex) as ListBoxItem;
        
        if (container != null)
        {
            _insertionLine = new Border
            {
                Height = 2,
                Background = global::Avalonia.Media.Brushes.Green,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch
            };
            
            // Position the line (simplified - would need proper adorner positioning)
            // This is a placeholder - proper implementation would use adorner layer
        }
    }

    private void HideInsertionLine()
    {
        _insertionLine = null;
    }
}
