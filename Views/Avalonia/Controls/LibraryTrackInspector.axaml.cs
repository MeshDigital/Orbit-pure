using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Library Track Inspector. Real cue rows are loaded by
/// <see cref="LibraryTrackInspectorViewModel"/> once library selection settles; this view only
/// makes sure technical data is loaded for hosts that open the inspector some other way
/// (Now Playing "Open Inspector", Flow Builder). LoadAnalysisDataAsync is load-once per track.
/// </summary>
public partial class LibraryTrackInspector : UserControl
{
    public LibraryTrackInspector()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not PlaylistTrackViewModel vm) return;
        try
        {
            await vm.LoadAnalysisDataAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Inspector failed to load analysis data for {GlobalId}", vm.GlobalId);
        }
    }
}
