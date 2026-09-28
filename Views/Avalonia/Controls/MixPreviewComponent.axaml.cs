using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public partial class MixPreviewComponent : UserControl
    {
        public MixPreviewComponent()
        {
            InitializeComponent();

            // Arrow keys fine-tune the selected cue in Edit cues mode (← → beat, Shift bar, Ctrl 10 ms),
            // re-auditioning from the cue on every press. Clicking anywhere in the editor gives it
            // keyboard focus so the keys land here; tunnelling so buttons don't swallow the arrows
            // for focus navigation first.
            Focusable = true;
            AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is TextBox || IsInside<ComboBox>(e.Source) || IsInside<TextBox>(e.Source)) return;
            if (DataContext is MixTransitionViewModel { IsCueEditMode: true }) Focus();
        }

        private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            if (DataContext is not MixTransitionViewModel { IsCueEditMode: true } vm || vm.ActiveEditor?.SelectedCue == null) return;
            // Typing in the cue name box or an open dropdown keeps its own arrow behaviour.
            if (IsInside<TextBox>(e.Source) || IsInside<ComboBox>(e.Source)) return;

            string sign = e.Key == Key.Left ? "-" : "+";
            string unit = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? "fine"
                        : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "bar"
                        : "beat";
            vm.NudgeActiveCueCommand.Execute(sign + unit).Subscribe();
            e.Handled = true;
        }

        private static bool IsInside<T>(object? source) where T : Control
        {
            for (var v = source as Visual; v != null; v = v.GetVisualParent())
                if (v is T) return true;
            return false;
        }
    }
}
