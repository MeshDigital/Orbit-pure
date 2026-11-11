using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels.Sets;

namespace SLSKDONET.Views.Avalonia.Sets;

/// <summary>P2.2: the Sets page shell. Its DataContext is the DI-resolved <see cref="SetsViewModel"/>.</summary>
public partial class SetsPage : UserControl
{
    public SetsPage()
    {
        InitializeComponent();
        if (!Design.IsDesignMode && DataContext == null &&
            Application.Current is App app && app.Services != null)
        {
            DataContext = app.Services.GetService(typeof(SetsViewModel)) as SetsViewModel;
        }

        Loaded += async (_, _) =>
        {
            if (DataContext is SetsViewModel vm)
                await vm.LoadAsync();
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
