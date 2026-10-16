using Avalonia.Controls;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
        }

        public SettingsPage(SettingsViewModel viewModel) : this()
        {
            DataContext = viewModel;
            viewModel.EnsureInitialized();
        }
    }
}
