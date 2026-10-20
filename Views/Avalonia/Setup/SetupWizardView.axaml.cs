using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SLSKDONET.ViewModels.Setup;

namespace SLSKDONET.Views.Avalonia.Setup;

/// <summary>
/// Setup wizard takeover. Keyboard: Enter runs the page's primary action (unless a button,
/// combo box, toggle or multi-line text box has focus and handles Enter itself), Esc toggles the
/// "Skip setup?" prompt.
///
/// Focus: every page focuses its primary button (the first empty field on the Soulseek page)
/// when it appears. Before, the wizard only tried to focus itself once while its host panel was
/// still hidden, so nothing had focus on Welcome and Enter did nothing; and after Next, which
/// disables itself while the step saves (Avalonia then clears focus), nothing on the new page
/// owned the keyboard either.
/// </summary>
public partial class SetupWizardView : UserControl
{
    private const int FocusAttempts = 10;
    private SetupWizardViewModel? _vm;

    public SetupWizardView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        AttachedToVisualTree += (_, _) => Attach(DataContext as SetupWizardViewModel);
        DataContextChanged += (_, _) => Attach(DataContext as SetupWizardViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Attach(SetupWizardViewModel? vm)
    {
        if (_vm != null) _vm.PropertyChanged -= OnWizardPropertyChanged;
        _vm = vm;
        if (_vm == null) return;
        _vm.PropertyChanged += OnWizardPropertyChanged;
        FocusPrimarySoon();
    }

    private void OnWizardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SetupWizardViewModel.CurrentIndex) or nameof(SetupWizardViewModel.IsSkipConfirmVisible))
            FocusPrimarySoon();
    }

    /// <summary>Focuses the page's primary control once the new page is laid out (retries while the host is still hidden).</summary>
    private void FocusPrimarySoon(int attempt = 0)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm == null || _vm.IsClosed) return;
            if (TryFocusPrimary()) return;
            if (attempt + 1 < FocusAttempts)
                DispatcherTimer.RunOnce(() => FocusPrimarySoon(attempt + 1), TimeSpan.FromMilliseconds(50));
        }, DispatcherPriority.Loaded);
    }

    private bool TryFocusPrimary()
    {
        if (_vm == null || !IsEffectivelyVisible) return false;

        if (_vm.IsSkipConfirmVisible)
            return FocusIfReady(this.FindControl<Button>("KeepSetupButton"));

        // Keep the user's place if they are already working inside the page (typing, a toggle).
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        if (focused != null && focused != this && this.IsVisualAncestorOf(focused) && focused is not Button)
            return true;

        Control? target;
        if (_vm.IsWelcome) target = this.FindControl<Button>("StartSetupButton");
        else if (_vm.IsDone) target = this.FindControl<Button>("FinishButton");
        else if (_vm.CurrentStep == _vm.Soulseek)
            target = this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && string.IsNullOrEmpty(t.Text))
                     ?? (Control?)this.FindControl<Button>("NextButton");
        else target = this.FindControl<Button>("NextButton");

        return FocusIfReady(target);
    }

    private static bool FocusIfReady(Control? target) =>
        target is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } && target.Focus();

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
            // A focused button/toggle/combo already acted on Enter; a multi-line box wants a new line.
            if (e.Source is Button or global::Avalonia.Controls.Primitives.ToggleButton or ComboBox) return;
            if (e.Source is TextBox { AcceptsReturn: true }) return;
            if (vm.EnterCommand.CanExecute(null)) vm.EnterCommand.Execute(null);
            e.Handled = true;
        }
    }
}
