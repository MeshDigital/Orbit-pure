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

        // AddTrackAsync reports its own failures (log + toast).
        var pickerVm = vm.CreateTrackPicker();
        pickerVm.Selected += async (_, hash) => await vm.AddTrackAsync(hash);
        SetPickerDialog.ShowModal(new TrackPickerWindow(pickerVm));
    }
}
