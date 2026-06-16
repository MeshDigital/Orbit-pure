using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SLSKDONET.ViewModels.Downloads;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public partial class SpectralForensicsDialog : Window
{
    private Grid? _rootGrid;

    public SpectralForensicsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => BuildContent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _rootGrid = this.FindControl<Grid>("RootGrid");
    }

    private void BuildContent()
    {
        if (_rootGrid == null || DataContext is not UnifiedTrackViewModel vm)
            return;

        _rootGrid.Children.Clear();
        _rootGrid.Background = new SolidColorBrush(Color.Parse("#0E141A"));

        var outer = new Border
        {
            Padding = new Thickness(20),
            Child = BuildLayout(vm),
        };
        _rootGrid.Children.Add(outer);
    }

    private Control BuildLayout(UnifiedTrackViewModel vm)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));  // header
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));  // verdict bar
        layout.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star))); // metrics
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));  // buttons

        // ── Header ─────────────────────────────────────────────────────────────
        var header = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock
        {
            Text = "SPECTRAL FORENSICS REPORT",
            FontSize = 11,
            FontWeight = FontWeight.Black,
            Foreground = new SolidColorBrush(Color.Parse("#3A6080")),
            LetterSpacing = 1.5,
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{vm.ArtistName} – {vm.TrackTitle}",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#E8EEF5")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        layout.Children.Add(header);

        // ── Verdict bar ─────────────────────────────────────────────────────────
        var verdictColor = Color.Parse(vm.SpectralVerdictColor);
        var verdictBar = new Border
        {
            Background = new SolidColorBrush(verdictColor, 0.1),
            BorderBrush = new SolidColorBrush(verdictColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 0, 0, 14),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    MakeText(vm.SpectralVerdictBadgeText, 18, FontWeight.Bold, vm.SpectralVerdictColor),
                    MakeMetricRight(vm.SpectralConfidenceDisplay, "#8AACCC", 1),
                }
            }
        };
        Grid.SetRow(verdictBar, 1);
        layout.Children.Add(verdictBar);

        // ── Metrics grid ────────────────────────────────────────────────────────
        var metricsGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        metricsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        metricsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        metricsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        metricsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var leftCard = BuildMetricCard("SPECTRAL", new[]
        {
            ("Cutoff",    vm.SpectralCutoffKhzDisplay,    "#9BD7CA"),
            ("Rolloff",   vm.SpectralRolloffDisplay + " — " + vm.SpectralRolloffLabel, "#8AACCC"),
            ("Mid Band",  vm.SpectralMidBandDisplay,      "#D7C0F5"),
            ("High Band", vm.SpectralHighBandDisplay,     "#F5D490"),
        });

        var rightCard = BuildMetricCard("DYNAMICS", new[]
        {
            ("Sample Rate", vm.SpectralSampleRateDisplay, "#9BD7CA"),
            ("Bit Depth",   vm.SpectralBitDepthDisplay,   "#8AACCC"),
            ("RMS Level",   vm.SpectralRmsDisplay,        "#D7C0F5"),
            ("Crest Factor", vm.SpectralCrestFactorDisplay, "#F5D490"),
        });

        Grid.SetColumn(rightCard, 1);
        rightCard.Margin = new Thickness(8, 0, 0, 0);
        metricsGrid.Children.Add(leftCard);
        metricsGrid.Children.Add(rightCard);

        Grid.SetRow(metricsGrid, 2);
        layout.Children.Add(metricsGrid);

        // ── Buttons ──────────────────────────────────────────────────────────────
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var reQueueBtn = new Button
        {
            Content = "↺ Re-queue",
            Background = new SolidColorBrush(Color.Parse("#2A1A1A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E74C3C")),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.Parse("#FF8A7A")),
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(6),
            IsVisible = vm.IsFailed || vm.IsWaiting,
        };
        reQueueBtn.Click += (_, _) =>
        {
            vm.RetryCommand.Execute(null);
            Close();
        };

        var closeBtn = new Button
        {
            Content = "Close",
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(6),
        };
        closeBtn.Click += (_, _) => Close();

        btnPanel.Children.Add(reQueueBtn);
        btnPanel.Children.Add(closeBtn);

        Grid.SetRow(btnPanel, 3);
        layout.Children.Add(btnPanel);

        return layout;
    }

    private static Border BuildMetricCard(string title, (string Label, string Value, string Color)[] rows)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 9,
            FontWeight = FontWeight.Black,
            Foreground = new SolidColorBrush(Color.Parse("#3A6080")),
            LetterSpacing = 1.2,
            Margin = new Thickness(0, 0, 0, 4),
        });

        foreach (var (label, value, color) in rows)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90, GridUnitType.Pixel)));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#5A7890")),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var valueBlock = new TextBlock
            {
                Text = value,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse(color)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(valueBlock, 1);
            row.Children.Add(valueBlock);

            stack.Children.Add(row);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#141D26")),
            BorderBrush = new SolidColorBrush(Color.Parse("#203040")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Child = stack,
        };
    }

    private static TextBlock MakeText(string text, int size, FontWeight weight, string color)
        => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static TextBlock MakeMetricRight(string text, string color, int column)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(block, column);
        return block;
    }
}
