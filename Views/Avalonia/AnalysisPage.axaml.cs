using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class AnalysisPage : UserControl
    {
        public AnalysisPage()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => PushCurrentWidthToViewModel();
        }

        public AnalysisPage(AnalysisPageViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            DataContextChanged += (_, _) => PushCurrentWidthToViewModel();
            PushCurrentWidthToViewModel();
        }

        private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            PushCurrentWidthToViewModel(e.NewSize.Width);
        }

        private void PushCurrentWidthToViewModel(double? width = null)
        {
            if (DataContext is AnalysisPageViewModel vm)
            {
                vm.UpdateLayoutMode(width ?? Bounds.Width);
            }
        }
    }
}
