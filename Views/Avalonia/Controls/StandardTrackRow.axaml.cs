using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels.Downloads;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class StandardTrackRow : UserControl
{
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<StandardTrackRow, bool>(nameof(IsCompact), defaultValue: false);

    public static readonly StyledProperty<bool> MinimalBadgesProperty =
        AvaloniaProperty.Register<StandardTrackRow, bool>(nameof(MinimalBadges), defaultValue: false);

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public bool MinimalBadges
    {
        get => GetValue(MinimalBadgesProperty);
        set => SetValue(MinimalBadgesProperty, value);
    }

    public StandardTrackRow()
    {
        InitializeComponent();
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is StyledElement sourceElement)
        {
            StyledElement? current = sourceElement;
            while (current != null)
            {
                if (current is Button || current is ToggleButton || current is TextBox || current is CheckBox || current is Slider)
                    return;

                current = current.Parent as StyledElement;
            }
        }

        if (DataContext is UnifiedTrackViewModel track)
        {
            track.IsConsoleOpen = !track.IsConsoleOpen;
            e.Handled = true;
        }
    }
}
