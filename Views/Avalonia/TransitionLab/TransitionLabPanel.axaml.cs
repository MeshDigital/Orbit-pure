using Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia.TransitionLab;

/// <summary>
/// Slice K6: the Transition Lab cockpit surface — hosts the dual-track <see cref="TransitionWaveformCanvas"/>
/// with audition/save commands and a rating/verified strip. DataContext is a
/// <see cref="SLSKDONET.ViewModels.TransitionLab.TransitionLabViewModel"/>.
/// </summary>
public partial class TransitionLabPanel : UserControl
{
    public TransitionLabPanel()
    {
        InitializeComponent();
    }
}
