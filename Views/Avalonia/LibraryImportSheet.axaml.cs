using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia;

/// <summary>The folder import sheet; see <see cref="SLSKDONET.ViewModels.LibraryImportViewModel"/>.</summary>
public partial class LibraryImportSheet : UserControl
{
    public LibraryImportSheet()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
