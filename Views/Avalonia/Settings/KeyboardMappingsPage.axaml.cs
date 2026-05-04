using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SLSKDONET.ViewModels.Settings;

namespace SLSKDONET.Views.Avalonia.Settings;

/// <summary>
/// Code-behind for <see cref="KeyboardMappingsPage"/>.
/// Handles key-capture events and file-picker dialogs.
/// </summary>
public partial class KeyboardMappingsPage : UserControl
{
    public KeyboardMappingsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private KeyboardMappingsViewModel? _vm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.ImportRequested -= OnImportRequested;
            _vm.ExportRequested -= OnExportRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as KeyboardMappingsViewModel;

        if (_vm != null)
        {
            _vm.ImportRequested += OnImportRequested;
            _vm.ExportRequested += OnExportRequested;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    // ─── Key capture ──────────────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyboardMappingsViewModel.IsCapturingKey)
            && _vm?.IsCapturingKey == true)
        {
            // Focus the overlay so it receives key events
            CaptureOverlay.Focus();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm?.IsCapturingKey == true)
        {
            // Always update the live preview (shows modifier combos as they are held)
            _vm.UpdateLiveCapture(e.Key, e.KeyModifiers);

            // Commit only when a non-modifier key is pressed
            if (!IsModifierKey(e.Key))
            {
                _vm.CommitCapture(e.Key, e.KeyModifiers);
                e.Handled = true;
            }
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        // When all modifier keys are released without a non-modifier key, clear preview
        if (_vm?.IsCapturingKey == true && e.KeyModifiers == KeyModifiers.None)
            _vm.UpdateLiveCapture(Key.None, KeyModifiers.None);
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;

    // ─── File dialogs ─────────────────────────────────────────────────────────

    private async void OnImportRequested(object? sender, EventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title           = "Import Keyboard Profile",
            AllowMultiple   = false,
            FileTypeFilter  = new[]
            {
                new FilePickerFileType("JSON profile") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All
            }
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path != null) _vm?.ImportFromPath(path);
        }
    }

    private async void OnExportRequested(object? sender, EventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title           = "Export Keyboard Profile",
            SuggestedFileName = "keyboard-profile",
            DefaultExtension  = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON profile") { Patterns = new[] { "*.json" } }
            }
        });

        if (file != null)
        {
            var path = file.TryGetLocalPath();
            if (path != null) _vm?.ExportToPath(path);
        }
    }
}
