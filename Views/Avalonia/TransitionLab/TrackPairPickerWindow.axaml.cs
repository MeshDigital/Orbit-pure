using Avalonia.Controls;
using SLSKDONET.ViewModels.TransitionLab;

namespace SLSKDONET.Views.Avalonia.TransitionLab;

/// <summary>Modal picker for choosing the outgoing/incoming track pair for the Transition Lab.</summary>
public partial class TrackPairPickerWindow : Window
{
    public TrackPairPickerWindow()
    {
        InitializeComponent();
    }

    public TrackPairPickerWindow(TrackPairPickerViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Confirmed += (_, _) => Close();
        Opened += async (_, _) => await viewModel.LoadAsync();
    }
}
