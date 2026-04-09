using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels;
using SLSKDONET.Models;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class LibraryTrackInspector : UserControl
{
    public LibraryTrackInspector()
    {
        InitializeComponent();
        // Trigger analysis data loading when the inspector becomes visible
        this.DataContextChanged += OnDataContextChanged;
        // Save cue label when a TextBox inside the cue list loses focus
        this.AddHandler(TextBox.LostFocusEvent, OnCueLabelLostFocus, handledEventsToo: true);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is PlaylistTrackViewModel vm)
            await vm.LoadAnalysisDataAsync();
    }

    /// <summary>
    /// When a cue label TextBox loses focus, persist the updated name to the DB.
    /// </summary>
    private async void OnCueLabelLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox tb && tb.DataContext is OrbitCue cue
            && DataContext is PlaylistTrackViewModel vm)
        {
            await vm.SaveCueLabelAsync(cue, tb.Text ?? string.Empty);
        }
    }
}
