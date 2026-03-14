using Avalonia.Controls;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class ImportPreviewPage : UserControl
    {
        // Design-time constructor
        public ImportPreviewPage()
        {
            InitializeComponent();
        }

        // DI Constructor
        public ImportPreviewPage(ImportPreviewViewModel viewModel) : this()
        {
            DataContext = viewModel;
        }
    }
}
