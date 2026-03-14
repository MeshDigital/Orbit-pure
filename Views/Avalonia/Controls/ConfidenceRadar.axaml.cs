using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Data;
using System;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class ConfidenceRadar : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ConfidenceRadar, string>(nameof(Label), "N/A");

    public static readonly StyledProperty<float> ValueProperty =
        AvaloniaProperty.Register<ConfidenceRadar, float>(nameof(Value), 0f);

    public static readonly StyledProperty<IBrush> ColorProperty =
        AvaloniaProperty.Register<ConfidenceRadar, IBrush>(nameof(Color), Brushes.Cyan);

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public float Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public double SweepAngle => (double)(Value * 360f);
    public string ValueText => $"{ (int)(Value * 100) }%";

    public ConfidenceRadar()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
