using Avalonia.Controls;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class AnalysisPage : UserControl
    {
        public AnalysisPage()
        {
            InitializeComponent();
        }

        public AnalysisPage(AnalysisPageViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
