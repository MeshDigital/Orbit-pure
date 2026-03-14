using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class ConfirmationDialog : Window
{
    public bool IsConfirmed { get; private set; }

    public ConfirmationDialog()
    {
        InitializeComponent();
        
        var yesBtn = this.FindControl<Button>("YesButton");
        var noBtn = this.FindControl<Button>("NoButton");

        if (yesBtn != null) yesBtn.Click += YesButton_Click;
        if (noBtn != null) noBtn.Click += NoButton_Click;
    }

    public ConfirmationDialog(string title, string message, string confirmLabel = "Yes", string cancelLabel = "No") : this()
    {
        Title = title;
        var titleText = this.FindControl<TextBlock>("TitleText");
        var messageText = this.FindControl<TextBlock>("MessageText");
        var yesBtn = this.FindControl<Button>("YesButton");
        var noBtn = this.FindControl<Button>("NoButton");

        if (titleText != null) titleText.Text = title;
        if (messageText != null) messageText.Text = message;
        if (yesBtn != null) yesBtn.Content = confirmLabel;
        if (noBtn != null) noBtn.Content = cancelLabel;
    }

    private void YesButton_Click(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        Close();
    }

    private void NoButton_Click(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        Close();
    }
}
