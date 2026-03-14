using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public partial class MixPreviewComponent : UserControl
    {
        public MixPreviewComponent()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
