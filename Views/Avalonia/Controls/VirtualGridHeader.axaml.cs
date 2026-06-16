using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public partial class VirtualGridHeader : UserControl
    {
        public static readonly StyledProperty<Control?> HeaderContentProperty =
            AvaloniaProperty.Register<VirtualGridHeader, Control?>(nameof(HeaderContent));

        public Control? HeaderContent
        {
            get => GetValue(HeaderContentProperty);
            set => SetValue(HeaderContentProperty, value);
        }

        public VirtualGridHeader()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
