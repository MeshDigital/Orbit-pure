using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SLSKDONET.ViewModels.Downloads;

namespace SLSKDONET.Views.Avalonia
{
    public partial class DownloadsPage : UserControl
    {
        public DownloadsPage()
        {
            InitializeComponent();
        }

        public DownloadsPage(DownloadCenterViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        // Clicking a track row opens it in the details panel. Clicks that land on the row's own
        // controls (selection checkbox, primary action, … button) are theirs, not the row's.
        private void OnRowTapped(object? sender, TappedEventArgs e)
        {
            if (e.Source is Visual source &&
                source.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, sender))
                      .Any(v => v is Button || v is CheckBox || v is ToggleButton))
                return;

            if (sender is Control { DataContext: DownloadRowViewModel row } && row.SelectCommand.CanExecute(null))
                row.SelectCommand.Execute(null);
        }

        // The row/group "…" button opens the same ContextMenu as right-click, so the action list
        // is defined once per template.
        private void OnOpenContextMenuClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control button) return;
            var host = button.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c.ContextMenu != null);
            host?.ContextMenu?.Open(host);
            e.Handled = true;
        }

        // Manual drag-to-reorder for the Playlists view — the group list is otherwise auto-sorted
        // by most-recent activity (DownloadCenterViewModel's ActiveGroups pipeline). The handle
        // starts the drag carrying the source DownloadGroupViewModel; the group header Border
        // (DragDrop.AllowDrop) is both the drag-over highlight and the drop target.
        private async void OnGroupDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Visual visual) return;
            var group = visual.DataContext as DownloadGroupViewModel;
            if (group == null) return;

            var data = new DataObject();
            data.Set("DownloadGroup", group);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }

        private void OnGroupRowDragOver(object? sender, DragEventArgs e)
        {
            bool canDrop = e.Data.Contains("DownloadGroup");
            e.DragEffects = canDrop ? DragDropEffects.Move : DragDropEffects.None;

            if (sender is Border border)
                border.Classes.Set("drag-over", canDrop);

            e.Handled = true;
        }

        private void OnGroupRowDragLeave(object? sender, DragEventArgs e)
        {
            if (sender is Border border)
                border.Classes.Remove("drag-over");
        }

        private void OnGroupRowDrop(object? sender, DragEventArgs e)
        {
            if (sender is Border border)
                border.Classes.Remove("drag-over");

            if (!e.Data.Contains("DownloadGroup")) return;
            if (sender is not Visual visual) return;

            var source = e.Data.Get("DownloadGroup") as DownloadGroupViewModel;
            var target = visual.DataContext as DownloadGroupViewModel;
            if (source == null || target == null || source == target) return;

            if (DataContext is DownloadCenterViewModel vm)
                vm.ReorderActiveGroup(source, target);

            e.Handled = true;
        }
    }
}
