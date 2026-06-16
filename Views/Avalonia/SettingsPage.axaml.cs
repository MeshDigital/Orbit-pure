using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Diagnostics;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
            AddHandler(InputElement.GotFocusEvent, OnAnyControlFocused, RoutingStrategies.Bubble);
        }

        public SettingsPage(SettingsViewModel viewModel) : this()
        {
            DataContext = viewModel;
        }

        private void OnAnyControlFocused(object? sender, GotFocusEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm) return;
            if (e.Source is not Visual source) return;

            foreach (var visual in source.GetSelfAndVisualAncestors())
            {
                if (visual is Control control && control.Tag is string tag && tag.Contains("||"))
                {
                    var sep = tag.IndexOf("||", System.StringComparison.Ordinal);
                    vm.FocusedHelpTitle = tag[..sep].Trim();
                    vm.FocusedHelpText  = tag[(sep + 2)..].Trim();
                    return;
                }
            }
        }

        private void OnInstallFfmpegClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                // Phase 8: Wire up FFmpeg download button
                // Open browser to official FFmpeg download page
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://ffmpeg.org/download.html",
                    UseShellExecute = true
                });
            }
            catch (System.Exception ex)
            {
                // Log error (logger not available in code-behind, but graceful fallback)
                System.Diagnostics.Debug.WriteLine($"Failed to open FFmpeg download page: {ex.Message}");
            }
        }
    }
}
