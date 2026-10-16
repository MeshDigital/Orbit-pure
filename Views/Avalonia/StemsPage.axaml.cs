using Avalonia.Controls;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

public partial class StemsPage : UserControl
{
    public StemsPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// DI constructor. Without it the page inherited the shell's MainViewModel as DataContext
    /// and every binding on it resolved to nothing.
    /// </summary>
    public StemsPage(StemMixerViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
