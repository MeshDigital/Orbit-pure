using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public partial class CreateSmartPlaylistDialog : Window
{
    private CreateSmartPlaylistViewModel? _boundVm;

    public CreateSmartPlaylistDialog()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.OnSave -= OnVmSave;
            _boundVm.OnCancel -= OnVmCancel;
        }

        _boundVm = DataContext as CreateSmartPlaylistViewModel;
        if (_boundVm != null)
        {
            _boundVm.OnSave += OnVmSave;
            _boundVm.OnCancel += OnVmCancel;
        }
    }

    private void OnVmSave(object? sender, SmartPlaylistCriteria criteria)
    {
        var name = _boundVm?.Name;
        Close(string.IsNullOrWhiteSpace(name) ? null : ((string Name, SmartPlaylistCriteria Criteria)?)(name!, criteria));
    }

    private void OnVmCancel(object? sender, EventArgs e)
    {
        Close(null);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
