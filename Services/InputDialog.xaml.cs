using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace SLSKDONET.Services
{
    public partial class InputDialog : Window
    {
        public string ResponseText { get; private set; } = string.Empty;
        public bool IsConfirmed { get; private set; }

        public InputDialog(string title, string prompt, string defaultResponse = "")
        {
            Title = title;

            var promptText = new TextBlock { Name = "PromptText", Text = prompt, Margin = new Thickness(0, 0, 0, 8) };
            var inputTextBox = new TextBox { Name = "InputTextBox", Text = defaultResponse };
            var okButton = new Button { Content = "OK", Margin = new Thickness(0, 8, 8, 0) };
            var cancelButton = new Button { Content = "Cancel", Margin = new Thickness(0, 8, 0, 0) };

            okButton.Click += OkButton_Click;
            cancelButton.Click += CancelButton_Click;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);

            var layout = new StackPanel { Margin = new Thickness(12) };
            layout.Children.Add(promptText);
            layout.Children.Add(inputTextBox);
            layout.Children.Add(buttons);

            Content = layout!;

            inputTextBox.Focus();
            inputTextBox.SelectAll();
        }

        private void OkButton_Click(object? sender, RoutedEventArgs e)
        {
            if (this.FindControl<TextBox>("InputTextBox") is TextBox inputTextBox)
            {
                ResponseText = inputTextBox.Text ?? string.Empty;
                IsConfirmed = true;
                Close();
            }
        }

        private void CancelButton_Click(object? sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            Close();
        }
    }
}
