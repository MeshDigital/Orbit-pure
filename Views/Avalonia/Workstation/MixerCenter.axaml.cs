using Avalonia.Controls;
using Avalonia.VisualTree;

namespace SLSKDONET.Views.Avalonia.Workstation;

public partial class MixerCenter : UserControl
{
    private const string DensityCompactClass = "ws-density-compact";
    private const string DensityNormalClass = "ws-density-normal";

    public MixerCenter()
    {
        InitializeComponent();
        ApplyDensityMode();
    }

    private void ApplyDensityMode()
    {
        // Follow the Workstation's available-space decision without a second DPI-based override.
        var compact = this.FindAncestorOfType<WorkstationPage>()?.Classes.Contains(DensityCompactClass) == true;

        Classes.Set(DensityCompactClass, compact);
        Classes.Set(DensityNormalClass, !compact);
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyDensityMode();
    }
}
