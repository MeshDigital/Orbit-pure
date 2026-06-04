using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public partial class SuggestedFlowImpactDialog : Window
{
    private Grid? _rootGrid;

    public SuggestedFlowImpactDialog()
    {
        InitializeComponent();
        Opened += (_, _) => BuildContent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _rootGrid = this.FindControl<Grid>("RootGrid");
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BuildContent()
    {
        if (_rootGrid == null || DataContext is not SuggestedFlowImpactViewModel vm)
            return;

        _rootGrid.Children.Clear();

        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#10181D")),
            Padding = new Thickness(18),
            Child = BuildLayout(vm),
        };

        _rootGrid.Children.Add(border);
    }

    private Control BuildLayout(SuggestedFlowImpactViewModel vm)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        layout.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var title = new TextBlock
        {
            Text = "Suggested Flow Impact",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#E8F4F1")),
        };
        layout.Children.Add(title);

        var summaryPanel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 12, 0, 12),
            Children =
            {
                new TextBlock
                {
                    Text = vm.SummaryText,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#F3D37A")),
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = vm.AverageTransitionScoreDisplay,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#9BD7CA")),
                }
            }
        };
        Grid.SetRow(summaryPanel, 1);
        layout.Children.Add(summaryPanel);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.1, GridUnitType.Star)));
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)));
        Grid.SetRow(body, 2);

        body.Children.Add(BuildCountsPanel(vm));

        var affected = BuildAffectedTransitionsPanel(vm);
        affected.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(affected, 1);
        body.Children.Add(affected);
        layout.Children.Add(body);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children =
            {
                new Button
                {
                    Content = "Close",
                    HorizontalAlignment = HorizontalAlignment.Right,
                }
            }
        };
        ((Button)buttonPanel.Children.Single()).Click += OnCloseClick;
        Grid.SetRow(buttonPanel, 3);
        layout.Children.Add(buttonPanel);

        return layout;
    }

    private static Border BuildCountsPanel(SuggestedFlowImpactViewModel vm)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = "Style Counts",
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#E8F4F1")),
        });

        foreach (var row in vm.Rows)
        {
            var grid = new Grid
            {
                Margin = new Thickness(0, 2, 0, 2)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            grid.Children.Add(new TextBlock { Text = row.StyleLabel, Foreground = new SolidColorBrush(Color.Parse("#D7E6E2")) });
            grid.Children.Add(BuildMetricText(row.CurrentCount.ToString(), 1, "#8AA0B8"));
            grid.Children.Add(BuildMetricText(row.ProposedCount.ToString(), 2, "#9BD7CA"));
            grid.Children.Add(BuildMetricText(row.DeltaDisplay, 3, "#F3D37A"));
            stack.Children.Add(grid);
        }

        return BuildCard(stack);
    }

    private static Border BuildAffectedTransitionsPanel(SuggestedFlowImpactViewModel vm)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = "Affected Transitions",
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#E8F4F1")),
        });

        var items = new StackPanel { Spacing = 0 };
        foreach (var transition in vm.AffectedTransitions)
        {
            items.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10),
                Background = new SolidColorBrush(Color.Parse("#111920")),
                CornerRadius = new CornerRadius(6),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = transition.EdgeLabel,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(Color.Parse("#D7E6E2")),
                        },
                        new TextBlock
                        {
                            Text = transition.StyleChangeLabel,
                            Foreground = new SolidColorBrush(Color.Parse("#F3D37A")),
                        },
                        new TextBlock
                        {
                            Text = transition.Reason,
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.Parse("#8AA0B8")),
                            TextWrapping = TextWrapping.Wrap,
                        }
                    }
                }
            });
        }

        stack.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = items,
        });

        return BuildCard(stack);
    }

    private static Border BuildCard(Control child)
        => new()
        {
            Background = new SolidColorBrush(Color.Parse("#162027")),
            BorderBrush = new SolidColorBrush(Color.Parse("#27515D")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = child,
        };

    private static TextBlock BuildMetricText(string text, int column, string color)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse(color)),
        };
        Grid.SetColumn(block, column);
        return block;
    }
}
