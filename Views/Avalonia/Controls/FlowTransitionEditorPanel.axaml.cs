using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public partial class FlowTransitionEditorPanel : UserControl
    {
        public FlowTransitionEditorPanel()
        {
            InitializeComponent();

            // Same keys as the compact Mix editor (MixPreviewComponent): in Edit cues mode the arrow
            // keys nudge the selected cue (Shift = bar, Ctrl = 10 ms) and D / 1 / 2 place a drop.
            // Space toggles the transition preview. Clicking anywhere in the editor focuses it.
            Focusable = true;
            AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private MixTransitionViewModel? Mix => (DataContext as FlowBuilderViewModel)?.MixTransitionVm;

        private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
        {
            if (IsInside<TextBox>(e.Source) || IsInside<ComboBox>(e.Source) || IsInside<NumericUpDown>(e.Source)) return;
            Focus();
        }

        private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
        {
            if (Mix is not { } mix) return;
            if (IsInside<TextBox>(e.Source) || IsInside<ComboBox>(e.Source) || IsInside<NumericUpDown>(e.Source)) return;

            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
            {
                mix.TogglePreviewCommand.Execute().Subscribe(_ => { }, _ => { });
                e.Handled = true;
                return;
            }

            if (!mix.IsCueEditMode) return;

            if (e.Key is (Key.D or Key.D1 or Key.D2 or Key.NumPad1 or Key.NumPad2) && e.KeyModifiers == KeyModifiers.None)
            {
                string key = e.Key switch { Key.D1 or Key.NumPad1 => "1", Key.D2 or Key.NumPad2 => "2", _ => "d" };
                mix.DropKeyCommand.Execute(key).Subscribe();
                e.Handled = true;
                return;
            }

            if (e.Key is not (Key.Left or Key.Right) || mix.ActiveEditor?.SelectedCue == null) return;
            string sign = e.Key == Key.Left ? "-" : "+";
            string unit = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? "fine"
                        : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "bar"
                        : "beat";
            mix.NudgeActiveCueCommand.Execute(sign + unit).Subscribe();
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
