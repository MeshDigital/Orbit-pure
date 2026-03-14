using Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia;

public partial class SplashScreen : Window
{
    public SplashScreen()
    {
        InitializeComponent();
    }

    public void UpdateStatus(string message)
    {
        var textBlock = this.FindControl<TextBlock>("StatusTextBlock");
        if (textBlock != null)
        {
            textBlock.Text = message;
        }
    }
}
