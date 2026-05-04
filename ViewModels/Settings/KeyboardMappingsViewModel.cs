using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Input;
using SLSKDONET.Views; // RelayCommand, RelayCommand<T>, AsyncRelayCommand

namespace SLSKDONET.ViewModels.Settings;

// ─── Row ViewModel ─────────────────────────────────────────────────────────

/// <summary>One row in the keyboard mappings grid.</summary>
public sealed class BindingRowViewModel : INotifyPropertyChanged
{
    private bool _hasConflict;

    public KeyboardAction  Action   { get; init; }
    public ActionCategory  Category { get; init; }

    public string ActionLabel   => FormatAction(Action);
    public string CategoryLabel => Category.ToString();

    public KeyboardBinding? Binding { get; set; }

    public string BindingLabel => Binding?.ToString() ?? "(not assigned)";

    public bool HasConflict
    {
        get => _hasConflict;
        set { _hasConflict = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    private static string FormatAction(KeyboardAction action) =>
        action switch
        {
            KeyboardAction.PlayPause         => "Play / Pause",
            KeyboardAction.Cue               => "Cue",
            KeyboardAction.HotCue1           => "Hot Cue 1 (Jump)",
            KeyboardAction.HotCue2           => "Hot Cue 2 (Jump)",
            KeyboardAction.HotCue3           => "Hot Cue 3 (Jump)",
            KeyboardAction.HotCue4           => "Hot Cue 4 (Jump)",
            KeyboardAction.HotCue5           => "Hot Cue 5 (Jump)",
            KeyboardAction.HotCue6           => "Hot Cue 6 (Jump)",
            KeyboardAction.HotCue7           => "Hot Cue 7 (Jump)",
            KeyboardAction.HotCue8           => "Hot Cue 8 (Jump)",
            KeyboardAction.SetHotCue1        => "Set Hot Cue 1",
            KeyboardAction.SetHotCue2        => "Set Hot Cue 2",
            KeyboardAction.SetHotCue3        => "Set Hot Cue 3",
            KeyboardAction.SetHotCue4        => "Set Hot Cue 4",
            KeyboardAction.SetHotCue5        => "Set Hot Cue 5",
            KeyboardAction.SetHotCue6        => "Set Hot Cue 6",
            KeyboardAction.SetHotCue7        => "Set Hot Cue 7",
            KeyboardAction.SetHotCue8        => "Set Hot Cue 8",
            KeyboardAction.LoopIn            => "Loop In",
            KeyboardAction.LoopOut           => "Loop Out",
            KeyboardAction.LoopExit          => "Exit Loop",
            KeyboardAction.HalfLoop          => "Half Loop",
            KeyboardAction.DoubleLoop        => "Double Loop",
            KeyboardAction.LoopMoveForward   => "Move Loop Forward",
            KeyboardAction.LoopMoveBack      => "Move Loop Back",
            KeyboardAction.LoopRoll1         => "Loop Roll 1 beat",
            KeyboardAction.LoopRoll2         => "Loop Roll 2 beats",
            KeyboardAction.LoopRoll4         => "Loop Roll 4 beats",
            KeyboardAction.LoopRoll8         => "Loop Roll 8 beats",
            KeyboardAction.BeatJumpForward1  => "Beat Jump +1",
            KeyboardAction.BeatJumpBack1     => "Beat Jump -1",
            KeyboardAction.BeatJumpForward2  => "Beat Jump +2",
            KeyboardAction.BeatJumpBack2     => "Beat Jump -2",
            KeyboardAction.BeatJumpForward4  => "Beat Jump +4",
            KeyboardAction.BeatJumpBack4     => "Beat Jump -4",
            KeyboardAction.BeatJumpForward8  => "Beat Jump +8",
            KeyboardAction.BeatJumpBack8     => "Beat Jump -8",
            KeyboardAction.BeatJumpForward16 => "Beat Jump +16",
            KeyboardAction.BeatJumpBack16    => "Beat Jump -16",
            KeyboardAction.BeatJumpForward32 => "Beat Jump +32",
            KeyboardAction.BeatJumpBack32    => "Beat Jump -32",
            KeyboardAction.TempoUpSmall      => "Tempo + (fine)",
            KeyboardAction.TempoDownSmall    => "Tempo - (fine)",
            KeyboardAction.TempoUpLarge      => "Tempo + (coarse)",
            KeyboardAction.TempoDownLarge    => "Tempo - (coarse)",
            KeyboardAction.Sync              => "Sync BPM",
            KeyboardAction.MasterSync        => "Master Sync",
            KeyboardAction.KeyLock           => "Key Lock",
            KeyboardAction.Quantize          => "Quantize (Snap)",
            KeyboardAction.BrowseUp          => "Library: Browse Up",
            KeyboardAction.BrowseDown        => "Library: Browse Down",
            KeyboardAction.LoadToDeck1       => "Load to Deck A",
            KeyboardAction.LoadToDeck2       => "Load to Deck B",
            KeyboardAction.SearchFocus       => "Focus Search",
            KeyboardAction.NavigateWorkstation => "Go to Workstation",
            KeyboardAction.NavigateLibrary   => "Go to Library",
            KeyboardAction.NavigateDownloads => "Go to Downloads",
            KeyboardAction.NavigateSearch    => "Go to Search",
            KeyboardAction.NavigateSettings  => "Go to Settings",
            KeyboardAction.OpenSettings      => "Open Settings",
            KeyboardAction.ShowKeyboardOverlay => "Show Keyboard Overlay (Ctrl+F1)",
            _                                => action.ToString()
        };
}

// ─── Main ViewModel ────────────────────────────────────────────────────────

/// <summary>
/// Powers the keyboard mappings settings page (Epic #119, Tasks 9-11).
/// Exposes bindable rows grouped by category, preset loading, and
/// import/export commands.
/// </summary>
public sealed class KeyboardMappingsViewModel : INotifyPropertyChanged
{
    private readonly IKeyboardMappingService           _mapping;
    private readonly IKeyboardTelemetryService?        _telemetry;
    private readonly ILogger<KeyboardMappingsViewModel> _logger;

    private BindingRowViewModel? _selectedRow;
    private bool   _isCapturingKey;
    private string _captureHint   = string.Empty;
    private string _liveKeyCombo  = string.Empty;
    private string _importError   = string.Empty;

    public KeyboardMappingsViewModel(
        IKeyboardMappingService            mapping,
        IKeyboardTelemetryService?         telemetry,
        ILogger<KeyboardMappingsViewModel> logger)
    {
        _mapping   = mapping;
        _telemetry = telemetry;
        _logger    = logger;

        Bindings = new ObservableCollection<BindingRowViewModel>();
        Conflicts = new ObservableCollection<string>();
        TopActions = new ObservableCollection<KeyboardStatRow>();

        LoadPresetOrbitCommand    = new RelayCommand(() => LoadBuiltIn(BuiltInPreset.OrbitDefault));
        LoadPresetRekordboxCommand= new RelayCommand(() => LoadBuiltIn(BuiltInPreset.Rekordbox));
        LoadPresetDjStudioCommand = new RelayCommand(() => LoadBuiltIn(BuiltInPreset.DjStudio));
        LoadPresetRekordbox4DeckCommand = new RelayCommand(() => LoadBuiltIn(BuiltInPreset.RekordboxFourDeck));
        ResetToDefaultCommand     = new RelayCommand(() => LoadBuiltIn(BuiltInPreset.OrbitDefault));
        ImportProfileCommand      = new AsyncRelayCommand(ImportAsync);
        ExportProfileCommand      = new AsyncRelayCommand(ExportAsync);
        StartCaptureCommand       = new RelayCommand<BindingRowViewModel?>(StartCapture, r => r != null);
        CancelCaptureCommand      = new RelayCommand(CancelCapture);

        _mapping.ProfileChanged += (_, _) => Refresh();
        Refresh();
    }

    // ─── Properties ──────────────────────────────────────────────────────────

    public ObservableCollection<BindingRowViewModel> Bindings   { get; }
    public ObservableCollection<string>              Conflicts  { get; }
    /// <summary>Top keyboard actions by usage frequency (populated from opt-in telemetry).</summary>
    public ObservableCollection<KeyboardStatRow>     TopActions { get; }

    public string ActiveProfileName => _mapping.ActiveProfile.Name;

    public BindingRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { _selectedRow = value; OnPropertyChanged(); }
    }

    public bool IsCapturingKey
    {
        get => _isCapturingKey;
        private set { _isCapturingKey = value; OnPropertyChanged(); }
    }

    public string CaptureHint
    {
        get => _captureHint;
        private set { _captureHint = value; OnPropertyChanged(); }
    }

    /// <summary>Live preview of the key combo being pressed during capture mode.</summary>
    public string LiveKeyCombo
    {
        get => _liveKeyCombo;
        private set { _liveKeyCombo = value; OnPropertyChanged(); }
    }

    /// <summary>Set when the last profile import failed.  Cleared on next successful import.</summary>
    public string ImportError
    {
        get => _importError;
        private set { _importError = value; OnPropertyChanged(); }
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    public ICommand LoadPresetOrbitCommand     { get; }
    public ICommand LoadPresetRekordboxCommand { get; }
    public ICommand LoadPresetDjStudioCommand  { get; }
    public ICommand LoadPresetRekordbox4DeckCommand { get; }
    public ICommand ResetToDefaultCommand      { get; }
    public ICommand ImportProfileCommand       { get; }
    public ICommand ExportProfileCommand       { get; }
    public ICommand StartCaptureCommand        { get; }
    public ICommand CancelCaptureCommand       { get; }

    // ─── Key capture ─────────────────────────────────────────────────────────

    private void StartCapture(BindingRowViewModel? row)
    {
        if (row == null) return;
        SelectedRow      = row;
        IsCapturingKey   = true;
        CaptureHint      = $"Press a key for '{row.ActionLabel}'...";
    }

    private void CancelCapture()
    {
        IsCapturingKey = false;
        CaptureHint    = string.Empty;
        LiveKeyCombo   = string.Empty;
        SelectedRow    = null;
    }

    /// <summary>
    /// Called by the View on every KeyDown during capture to show a live preview
    /// of the key combo being held (modifier keys update the preview before commit).
    /// </summary>
    public void UpdateLiveCapture(Key key, KeyModifiers modifiers)
    {
        if (!IsCapturingKey) return;
        LiveKeyCombo = FormatKeyCombo(key, modifiers);
    }

    private static string FormatKeyCombo(Key key, KeyModifiers modifiers)
    {
        if (key == Key.None && modifiers == KeyModifiers.None) return string.Empty;
        var parts = new System.Collections.Generic.List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Shift))   parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Alt))     parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Meta))    parts.Add("Win");
        // Only include the key itself if it is not a standalone modifier key
        bool isModifierKey = key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;
        if (!isModifierKey && key != Key.None)
            parts.Add(key.ToString());
        return parts.Count > 0 ? string.Join(" + ", parts) : string.Empty;
    }

    /// <summary>
    /// Called by the View's KeyDown handler when <see cref="IsCapturingKey"/> is true.
    /// Assigns or removes a binding for the currently selected row.
    /// </summary>
    public void CommitCapture(Key key, KeyModifiers modifiers)
    {
        if (!IsCapturingKey || SelectedRow == null) return;

        // Escape cancels capture without saving
        if (key == Key.Escape) { CancelCapture(); return; }

        // Delete / Backspace removes the binding
        if (key is Key.Delete or Key.Back)
        {
            RemoveBinding(SelectedRow);
            CancelCapture();
            return;
        }

        var profile  = _mapping.ActiveProfile;
        var existing = SelectedRow.Binding;

        // Remove old binding for this action+deck combination
        if (existing != null)
            profile.Bindings.Remove(existing);

        var newBinding = new KeyboardBinding
        {
            Action    = SelectedRow.Action,
            Key       = key,
            Modifiers = modifiers,
            Deck      = 0  // global; could be extended with a deck selector
        };

        profile.Bindings.Add(newBinding);
        SaveProfile(profile);

        IsCapturingKey = false;
        CaptureHint    = string.Empty;
        LiveKeyCombo   = string.Empty;
        SelectedRow    = null;
    }

    // ─── Internal logic ──────────────────────────────────────────────────────

    private void LoadBuiltIn(BuiltInPreset preset)
    {
        _mapping.LoadProfile(KeyboardProfile.GetBuiltIn(preset));
        _logger.LogInformation("[KeyboardMappings] Loaded built-in preset: {Preset}", preset);
    }

    private void RemoveBinding(BindingRowViewModel row)
    {
        var profile = _mapping.ActiveProfile;
        profile.Bindings.RemoveAll(b => b.Action == row.Action);
        SaveProfile(profile);
    }

    private void SaveProfile(KeyboardProfile profile)
    {
        string dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ORBIT-Pure");
        string path = Path.Combine(dir, "keyboard-profile.json");
        profile.SaveToFile(path);
        _mapping.LoadProfile(profile);
    }

    private async System.Threading.Tasks.Task ImportAsync()
    {
        // Platform dialog is opened by the View; here we just demonstrate
        // that a path coming from the View can be used.
        // This event lets the View know it should open a file picker.
        ImportRequested?.Invoke(this, EventArgs.Empty);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task ExportAsync()
    {
        ExportRequested?.Invoke(this, EventArgs.Empty);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// Called by the View after the user picks an import file path.
    public void ImportFromPath(string path)
    {
        try
        {
            var profile = KeyboardProfile.LoadFromFile(path);
            _mapping.LoadProfile(profile);
            ImportError = string.Empty; // clear any previous error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[KeyboardMappings] Import failed: {Path}", path);
            ImportError = $"Import failed: {ex.Message}";
        }
    }

    /// Called by the View after the user picks an export file path.
    public void ExportToPath(string path)
    {
        try
        {
            _mapping.ActiveProfile.SaveToFile(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[KeyboardMappings] Export failed: {Path}", path);
        }
    }

    // ─── Events for View dialog coordination ─────────────────────────────────

    public event EventHandler? ImportRequested;
    public event EventHandler? ExportRequested;

    // ─── Refresh ─────────────────────────────────────────────────────────────

    private void Refresh()
    {
        // Rebuild all rows from the current profile
        var profile  = _mapping.ActiveProfile;
        var bindings = profile.Bindings;

        // Build conflict set
        var conflictPairs = _mapping.GetConflicts().ToList();
        var conflictedKeys = new HashSet<(Key, KeyModifiers, int)>(
            conflictPairs.SelectMany(p => new[]
            {
                (p.a.Key, p.a.Modifiers, p.a.Deck),
                (p.b.Key, p.b.Modifiers, p.b.Deck)
            }));

        Bindings.Clear();
        Conflicts.Clear();

        foreach (var conflict in conflictPairs)
            Conflicts.Add($"Conflict: {conflict.a} ↔ {conflict.b}");

        foreach (var action in KeyboardActionInfo.All)
        {
            var binding = bindings.FirstOrDefault(b => b.Action == action);
            bool hasConflict = binding != null &&
                conflictedKeys.Contains((binding.Key, binding.Modifiers, binding.Deck));

            Bindings.Add(new BindingRowViewModel
            {
                Action      = action,
                Category    = KeyboardActionInfo.GetCategory(action),
                Binding     = binding,
                HasConflict = hasConflict
            });
        }

        OnPropertyChanged(nameof(ActiveProfileName));

        // Refresh telemetry stats (only populated when opt-in telemetry is enabled)
        TopActions.Clear();
        if (_telemetry != null)
        {
            foreach (var (name, count) in _telemetry.GetTopActions(5))
                TopActions.Add(new KeyboardStatRow { ActionName = name, Count = count });
        }
    }

    // ─── INotifyPropertyChanged ───────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

// ─── Telemetry stat row ─────────────────────────────────────────────────────

/// <summary>One row in the "Your most used actions" list.</summary>
public sealed class KeyboardStatRow
{
    public string ActionName { get; init; } = string.Empty;
    public int    Count      { get; init; }
    public string Label      => $"{ActionName}  ×{Count}";
}
