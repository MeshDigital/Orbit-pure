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
        var compactFromAncestor = this.FindAncestorOfType<WorkstationPage>()?.Classes.Contains(DensityCompactClass) == true;
        var compact = compactFromAncestor || (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0) >= 1.35;

        Classes.Set(DensityCompactClass, compact);
        Classes.Set(DensityNormalClass, !compact);
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyDensityMode();
    }
}
