using Avalonia.Controls;
using SLSKDONET.ViewModels.StoryGraph;

namespace SLSKDONET.Views.Avalonia.StoryGraph;

/// <summary>Modal single-track picker for adding a slot to the Story Graph.</summary>
public partial class TrackPickerWindow : Window
{
    public TrackPickerWindow()
    {
        InitializeComponent();
    }

    public TrackPickerWindow(TrackPickerViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Selected += (_, _) => Close();
        Opened += async (_, _) => await viewModel.LoadAsync();
        Closed += (_, _) => viewModel.Dispose();
    }
}
