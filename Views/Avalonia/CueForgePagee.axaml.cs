using Avalonia.Controls;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

public partial class CueForgePagee : UserControl
{
    public CueForgePagee()
    {
        InitializeComponent();
    }

    public CueForgePagee(CueForgeViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
