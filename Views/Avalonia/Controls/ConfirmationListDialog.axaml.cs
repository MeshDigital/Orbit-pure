using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// A confirmation dialog with a scrollable list of detail lines (e.g. affected file paths).
/// The cancel button is both the safe default (Enter) and the Escape action, so a destructive
/// confirmation is never triggered by accident.
/// </summary>
public partial class ConfirmationListDialog : Window
{
    public bool IsConfirmed { get; private set; }

    public ConfirmationListDialog()
    {
        InitializeComponent();

        var confirmBtn = this.FindControl<Button>("ConfirmButton");
        var cancelBtn = this.FindControl<Button>("CancelButton");

        if (confirmBtn != null) confirmBtn.Click += ConfirmButton_Click;
        if (cancelBtn != null) cancelBtn.Click += CancelButton_Click;
    }

    public ConfirmationListDialog(
        string title,
        string message,
        IReadOnlyList<string> items,
        string confirmLabel = "Yes",
        string cancelLabel = "No") : this()
    {
        Title = title;

        var titleText = this.FindControl<TextBlock>("TitleText");
        var messageText = this.FindControl<TextBlock>("MessageText");
        var itemsList = this.FindControl<ItemsControl>("ItemsList");
        var confirmBtn = this.FindControl<Button>("ConfirmButton");
        var cancelBtn = this.FindControl<Button>("CancelButton");

        if (titleText != null) titleText.Text = title;
        if (messageText != null) messageText.Text = message;
        if (itemsList != null) itemsList.ItemsSource = items;
        if (confirmBtn != null) confirmBtn.Content = confirmLabel;
        if (cancelBtn != null) cancelBtn.Content = cancelLabel;
    }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        Close();
    }
}
