using Avalonia.Controls;
using SLSKDONET.ViewModels.Downloads;

namespace SLSKDONET.Views.Avalonia
{
    public partial class DownloadsPage : UserControl
    {
        public DownloadsPage()
        {
            InitializeComponent();
        }

        public DownloadsPage(DownloadCenterViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
