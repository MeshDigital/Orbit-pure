using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class UsersPage : UserControl
    {
        public UsersPage()
        {
            InitializeComponent();

            // Tunnel (not Bubble) so this runs before TextBox's own AcceptsReturn key handling —
            // registering via the plain XAML `KeyDown="..."` attribute instead wires the Bubble
            // route, which loses the race against the TextBox's internal newline-insertion and
            // silently never sends. One handler on the root catches both the profile chat and
            // room chat message boxes, distinguished by their Tag-bound send command.
            AddHandler(KeyDownEvent, OnMessageInputKeyDown, RoutingStrategies.Tunnel);
        }

        public UsersPage(UsersViewModel viewModel) : this()
        {
            DataContext = viewModel;
        }

        private void OnMessageInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                return;

            if (e.Source is not TextBox { Tag: ICommand command })
                return;

            e.Handled = true;
            if (command.CanExecute(null))
                command.Execute(null);
        }
    }
}
