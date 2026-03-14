using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class SpectrogramControl : UserControl
{
    public static readonly StyledProperty<Bitmap?> SpectrogramBitmapProperty =
        AvaloniaProperty.Register<SpectrogramControl, Bitmap?>(nameof(SpectrogramBitmap));

    public Bitmap? SpectrogramBitmap
    {
        get => GetValue(SpectrogramBitmapProperty);
        set => SetValue(SpectrogramBitmapProperty, value);
    }

    public SpectrogramControl()
    {
        InitializeComponent();
    }
}
