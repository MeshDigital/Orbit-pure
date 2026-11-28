using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Vault;

namespace SLSKDONET.Views.Avalonia
{
    public partial class VaultPage : UserControl
    {
        public VaultPage()
        {
            InitializeComponent();
        }

        public VaultPage(VaultViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            if (DataContext is VaultViewModel vm)
                _ = vm.LoadAsync();
        }
    }
}
