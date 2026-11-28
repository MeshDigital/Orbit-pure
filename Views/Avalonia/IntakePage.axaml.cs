using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Intake;

namespace SLSKDONET.Views.Avalonia
{
    public partial class IntakePage : UserControl
    {
        public IntakePage()
        {
            InitializeComponent();
        }

        public IntakePage(IntakeViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            if (DataContext is IntakeViewModel vm)
                _ = vm.LoadAsync();
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            // Leaving the page stops a preview that would otherwise keep playing unseen.
            if (DataContext is IntakeViewModel vm)
                vm.StopPreview();
            base.OnUnloaded(e);
        }
    }
}
