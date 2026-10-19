using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SLSKDONET.ViewModels.Setup;

namespace SLSKDONET.Views.Avalonia.Setup;

/// <summary>
/// Setup wizard takeover. Keyboard: Enter runs the page's primary action (unless a button,
/// combo box or toggle has focus and handles Enter itself), Esc toggles the "Skip setup?" prompt.
/// </summary>
public partial class SetupWizardView : UserControl
{
    public SetupWizardView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
        DataContextChanged += (_, _) =>
        {
            if (DataContext != null) Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not SetupWizardViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            vm.EscapeCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            // A focused button/toggle/combo already acted on Enter; don't also press Next.
            if (e.Source is Button or global::Avalonia.Controls.Primitives.ToggleButton or ComboBox) return;
            if (vm.EnterCommand.CanExecute(null)) vm.EnterCommand.Execute(null);
            e.Handled = true;
        }
    }
}
