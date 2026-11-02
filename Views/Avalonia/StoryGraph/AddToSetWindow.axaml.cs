using Avalonia.Controls;
using SLSKDONET.ViewModels.StoryGraph;

namespace SLSKDONET.Views.Avalonia.StoryGraph;

/// <summary>Modal picker for adding a library track to an existing (or new) set.</summary>
public partial class AddToSetWindow : Window
{
    public AddToSetWindow()
    {
        InitializeComponent();
    }

    public AddToSetWindow(AddToSetViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Completed += (_, _) => Close();
        Opened += async (_, _) => await viewModel.LoadAsync();
        Closed += (_, _) => viewModel.Dispose();
    }
}
