using Avalonia.Controls;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class UsersPage : UserControl
    {
        public UsersPage()
        {
            InitializeComponent();
        }

        public UsersPage(UsersViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
