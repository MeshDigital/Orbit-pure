using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia
{
    public partial class MixTransitionPanel : UserControl
    {
        public MixTransitionPanel()
        {
            InitializeComponent();
        }

        // "My presets" (#204 D5): the view models report their own failures in StatusMessage.
        private async void OnSaveUserPresetClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is SLSKDONET.ViewModels.MixTransitionViewModel vm)
                await vm.SaveUserPresetAsync();
        }

        private async void OnDeleteUserPresetClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is SLSKDONET.ViewModels.MixTransitionViewModel vm)
                await vm.DeleteSelectedUserPresetAsync();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
