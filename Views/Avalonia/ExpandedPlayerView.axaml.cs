using Avalonia.Controls;
using Avalonia.Input;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

public partial class ExpandedPlayerView : UserControl
{
    private Slider? _seekSlider;

    public ExpandedPlayerView()
    {
        InitializeComponent();
        _seekSlider = this.FindControl<Slider>("SeekSlider");
    }

    /// <summary>
    /// The progress slider used to bind Value TwoWay straight to Position, which meant every
    /// 50ms playback tick immediately overwrote whatever the user was dragging to — the slider
    /// was purely decorative. Now OneWay + an explicit seek on release, matching the pattern
    /// already used by PlayerFallbackPanel's seek slider.
    /// </summary>
    private void OnSeekSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm || _seekSlider is null)
            return;

        var seek = vm.SeekCommand;
        var value = (float)_seekSlider.Value;

        if (seek.CanExecute(value))
            seek.Execute(value);
    }
}
