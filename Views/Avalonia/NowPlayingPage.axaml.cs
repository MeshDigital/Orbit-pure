using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class NowPlayingPage : UserControl
    {
        private INavigationService? _navigationService;

        // Keep a public parameterless constructor so Avalonia runtime loader can always instantiate this view.
        public NowPlayingPage()
        {
            InitializeComponent();
            EnsureResolvedPlayerContext();
        }

        public NowPlayingPage(PlayerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            EnsureResolvedPlayerContext();
        }

        public NowPlayingPage(PlayerViewModel viewModel, INavigationService navigationService)
        {
            _navigationService = navigationService;
            InitializeComponent();
            DataContext = viewModel;
            EnsureResolvedPlayerContext();
        }

        private void EnsureResolvedPlayerContext()
        {
            if (Application.Current is not App app || app.Services is null)
            {
                return;
            }

            _navigationService = app.Services.GetService(typeof(INavigationService)) as INavigationService;

            var singletonPlayerVm = app.Services.GetService(typeof(PlayerViewModel)) as PlayerViewModel;
            if (singletonPlayerVm != null && !ReferenceEquals(DataContext, singletonPlayerVm))
            {
                DataContext = singletonPlayerVm;
            }
        }

        protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            EnsureResolvedPlayerContext();
        }

        private void OnBackClick(object? sender, RoutedEventArgs e)
        {
            _navigationService?.GoBack();
        }
    }
}
