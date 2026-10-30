using Avalonia.Controls;
using SLSKDONET.ViewModels.StoryGraph;

namespace SLSKDONET.Views.Avalonia.StoryGraph;

/// <summary>Modal picker for choosing a SetProject to open on the Story Graph.</summary>
public partial class SetProjectPickerWindow : Window
{
    public SetProjectPickerWindow()
    {
        InitializeComponent();
    }

    public SetProjectPickerWindow(SetProjectPickerViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Selected += (_, _) => Close();
        Opened += async (_, _) => await viewModel.LoadAsync();
    }
}
