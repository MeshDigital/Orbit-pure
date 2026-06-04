using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class PlayerFallbackPanel : UserControl
{
    private Slider? _seekSlider;

    public PlayerFallbackPanel()
    {
        InitializeComponent();
        _seekSlider = this.FindControl<Slider>("SeekSlider");
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnSeekSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm || _seekSlider is null)
            return;

        var seek = vm.PlayerViewModel.SeekCommand;
        var value = (float)_seekSlider.Value;

        if (seek.CanExecute(value))
            seek.Execute(value);
    }
}