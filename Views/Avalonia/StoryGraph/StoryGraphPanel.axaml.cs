using Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia.StoryGraph;

/// <summary>
/// Slice K5: the Set Projects cockpit surface — hosts the <see cref="StoryGraphView"/> canvas with
/// a role/energy inspector and undo/redo. Its <c>DataContext</c> is a
/// <see cref="SLSKDONET.ViewModels.StoryGraph.StoryGraphViewModel"/>.
/// </summary>
public partial class StoryGraphPanel : UserControl
{
    public StoryGraphPanel()
    {
        InitializeComponent();
    }
}
