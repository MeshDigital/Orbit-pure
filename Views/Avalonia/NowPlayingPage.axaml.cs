using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SLSKDONET.Views.Avalonia
{
    public partial class NowPlayingPage : UserControl
    {
        public NowPlayingPage()
        {
            InitializeComponent();
            
            // Wire up seek functionality
            var slider = this.FindControl<Slider>("ProgressSlider");
            if (slider != null)
            {
                slider.PointerReleased += OnProgressSliderReleased;
            }
        }
        
        private void OnProgressSliderReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (DataContext is ViewModels.PlayerViewModel playerViewModel && sender is Slider slider)
            {
                playerViewModel.Seek((float)slider.Value);
            }
        }
    }
}
