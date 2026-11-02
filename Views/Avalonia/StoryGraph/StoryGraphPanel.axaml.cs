using System;
using Avalonia.Controls;
using SLSKDONET.ViewModels.StoryGraph;

namespace SLSKDONET.Views.Avalonia.StoryGraph;

/// <summary>
/// Slice K5: the Set Projects cockpit surface — hosts the <see cref="StoryGraphView"/> canvas with
/// a role/energy inspector and undo/redo. Its <c>DataContext</c> is a
/// <see cref="SLSKDONET.ViewModels.StoryGraph.StoryGraphViewModel"/>.
/// </summary>
public partial class StoryGraphPanel : UserControl
{
    public StoryGraphPanel()
    {
        InitializeComponent();
    }

    private void OnAddTrackClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not StoryGraphViewModel vm)
            return;

        var pickerVm = vm.CreateTrackPicker();
        pickerVm.Selected += async (_, hash) =>
        {
            try
            {
                await vm.AddTrackAsync(hash);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Add track failed: {ex.Message}");
            }
        };
        var window = new TrackPickerWindow(pickerVm);
        window.Show();
    }
}
