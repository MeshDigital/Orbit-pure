using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SLSKDONET.Data;
using SLSKDONET.Models;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class SonicTelemetryCard : UserControl
{
    public SonicTelemetryCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

// Helper models for the stage LEDs
public class AnalysisStageItem
{
    public string Label { get; set; } = string.Empty;
    public AnalysisStage Stage { get; set; }
    public IBrush Color { get; set; } = Brushes.Gray;
    public Color GlowColor { get; set; } = Colors.Transparent;
    public double Opacity { get; set; } = 0.3;
}
