using Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia
{
    public partial class NowPlayingPage : UserControl
    {
        public NowPlayingPage()
        {
            InitializeComponent();
        }

        public NowPlayingPage(SLSKDONET.ViewModels.PlayerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
