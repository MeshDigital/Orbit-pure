using Avalonia;
using Avalonia.Controls.Primitives;

namespace SLSKDONET.Views.Avalonia.Controls;

public class StrategyCard : TemplatedControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<StrategyCard, string>(nameof(Title), "Strategy Name");

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<StrategyCard, string>(nameof(Description), "Description of what this strategy does.");

    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<StrategyCard, string>(nameof(Icon), "🎵");

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<StrategyCard, bool>(nameof(IsSelected));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }
}
