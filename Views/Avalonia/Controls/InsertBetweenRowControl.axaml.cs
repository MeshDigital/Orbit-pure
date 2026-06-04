using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class InsertBetweenRowControl : UserControl
{
    public static readonly StyledProperty<bool> IsMagneticHoverProperty =
        AvaloniaProperty.Register<InsertBetweenRowControl, bool>(nameof(IsMagneticHover));

    public static readonly StyledProperty<ICommand?> InsertBetweenCommandProperty =
        AvaloniaProperty.Register<InsertBetweenRowControl, ICommand?>(nameof(InsertBetweenCommand));

    public static readonly StyledProperty<object?> InsertBetweenCommandParameterProperty =
        AvaloniaProperty.Register<InsertBetweenRowControl, object?>(nameof(InsertBetweenCommandParameter));

    public ICommand? InsertBetweenCommand
    {
        get => GetValue(InsertBetweenCommandProperty);
        set => SetValue(InsertBetweenCommandProperty, value);
    }

    public bool IsMagneticHover
    {
        get => GetValue(IsMagneticHoverProperty);
        set => SetValue(IsMagneticHoverProperty, value);
    }

    public object? InsertBetweenCommandParameter
    {
        get => GetValue(InsertBetweenCommandParameterProperty);
        set => SetValue(InsertBetweenCommandParameterProperty, value);
    }

    public InsertBetweenRowControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}