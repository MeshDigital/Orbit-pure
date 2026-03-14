using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SLSKDONET;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
        }

        public SettingsPage(SettingsViewModel viewModel) : this()
        {
            DataContext = viewModel;
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
