using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

        // Manual drag-to-reorder for the "Group by playlist" Active view — the group list is
        // otherwise auto-sorted by most-recent activity (DownloadCenterViewModel's ActiveGroups
        // pipeline), which previously had no way to be overridden. Mirrors FlowBuilderView's
        // proven DoDragDrop pattern: the handle starts the drag carrying the source
        // DownloadGroupViewModel, and the row Border (DragDrop.AllowDrop) is both the visual
        // drag-over target and the drop target.
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
