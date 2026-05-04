using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Input;
using SLSKDONET.Services.Input;

namespace SLSKDONET.ViewModels.Workstation;

/// <summary>
/// ViewModel for the F1 keyboard-shortcut overlay.
/// Builds a grouped, human-readable table of every binding in the active profile.
/// </summary>
public sealed class KeyboardOverlayViewModel : INotifyPropertyChanged
{
    // ─── INPC ─────────────────────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ─── Visibility ───────────────────────────────────────────────────────────

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        private set { _isVisible = value; Notify(); }
    }

    // ─── Data ─────────────────────────────────────────────────────────────────

    private IReadOnlyList<KeyGroupViewModel> _groups = Array.Empty<KeyGroupViewModel>();
    public IReadOnlyList<KeyGroupViewModel> Groups
    {
        get => _groups;
        private set { _groups = value; Notify(); }
    }

    private string _profileName = "";
    public string ProfileName
    {
        get => _profileName;
        private set { _profileName = value; Notify(); }
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Toggle visibility. Rebuilds binding groups whenever opening.</summary>
    public void Toggle(IKeyboardMappingService mapping)
    {
        if (_isVisible)
        {
            IsVisible = false;
        }
        else
        {
            Rebuild(mapping);
            IsVisible = true;
        }
    }

    public void Hide() => IsVisible = false;

    // ─── Build binding groups ─────────────────────────────────────────────────

    private void Rebuild(IKeyboardMappingService mapping)
    {
        var profile = mapping.ActiveProfile;
        ProfileName = profile.Name;

        var groups = profile.Bindings
            .GroupBy(b => KeyboardActionInfo.GetCategory(b.Action))
            .OrderBy(g => CategoryOrder(g.Key))
            .Select(g => new KeyGroupViewModel(
                CategoryLabel(g.Key),
                g.OrderBy(b => b.Action.ToString())
                  .Select(b => new OverlayBindingRow(
                      FormatAction(b.Action),
                      FormatKey(b.Key, b.Modifiers),
                      b.Deck))
                  .ToList()))
            .ToList();

        Groups = groups;
    }

    // ─── Category ordering / labelling ────────────────────────────────────────

    private static int CategoryOrder(ActionCategory cat) => cat switch
    {
        ActionCategory.Deck    => 0,
        ActionCategory.Mixer   => 1,
        ActionCategory.Browser => 2,
        ActionCategory.FX      => 3,
        ActionCategory.Global  => 4,
        _                      => 99
    };

    private static string CategoryLabel(ActionCategory cat) => cat switch
    {
        ActionCategory.Deck    => "Deck Controls",
        ActionCategory.Mixer   => "Mixer",
        ActionCategory.Browser => "Browser & Library",
        ActionCategory.FX      => "FX",
        ActionCategory.Global  => "Navigation",
        _                      => cat.ToString()
    };

    // ─── Formatting ──────────────────────────────────────────────────────────

    internal static string FormatKey(Key key, KeyModifiers mods)
    {
        var parts = new List<string>(4);
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt))     parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift))   parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta))    parts.Add("Win");

        string keyStr = key switch
        {
            Key.Space            => "Space",
            Key.Return           => "Enter",
            Key.Back             => "Backspace",
            Key.Tab              => "Tab",
            Key.Escape           => "Esc",
            Key.Up               => "↑",
            Key.Down             => "↓",
            Key.Left             => "←",
            Key.Right            => "→",
            Key.OemPeriod        => ".",
            Key.OemComma        => ",",
            Key.OemMinus         => "-",
            Key.OemPlus          => "=",
            Key.OemOpenBrackets  => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon     => ";",
            Key.OemQuotes        => "'",
            Key.OemQuestion      => "/",
            Key.OemPipe          => "\\",
            Key.OemTilde         => "`",
            _ => key.ToString()
        };
        parts.Add(keyStr);
        return string.Join("+", parts);
    }

    internal static string FormatAction(KeyboardAction action) => action switch
    {
        KeyboardAction.PlayPause        => "Play / Pause",
        KeyboardAction.LoopIn           => "Loop In",
        KeyboardAction.LoopOut          => "Loop Out",
        KeyboardAction.LoopExit         => "Loop Exit",
        KeyboardAction.LoopMoveForward  => "Loop Move →",
        KeyboardAction.LoopMoveBack     => "Loop Move ←",
        KeyboardAction.HalfLoop         => "½ Loop",
        KeyboardAction.DoubleLoop       => "2× Loop",
        KeyboardAction.AutoBeatLoop     => "Auto Beat Loop",
        KeyboardAction.JumpToBeginning  => "Jump to Start",
        KeyboardAction.JumpForward      => "Jump Forward",
        KeyboardAction.JumpBackward     => "Jump Backward",
        KeyboardAction.AutoCue          => "Auto Cue",
        KeyboardAction.VinylMode        => "Vinyl Mode",
        KeyboardAction.TempoRange       => "Cycle Tempo Range",
        KeyboardAction.KeyReset         => "Reset Key",
        KeyboardAction.MasterSync       => "Master Sync",
        KeyboardAction.HeadphoneCue     => "Headphone Cue",
        KeyboardAction.SlipMode         => "Slip Mode",
        KeyboardAction.SlipReverse      => "Slip Reverse",
        KeyboardAction.SearchFocus      => "Focus Search",
        KeyboardAction.PitchBendUp      => "Pitch Bend +",
        KeyboardAction.PitchBendDown    => "Pitch Bend −",
        KeyboardAction.TempoUpSmall     => "Tempo +0.1%",
        KeyboardAction.TempoDownSmall   => "Tempo −0.1%",
        KeyboardAction.TempoUpLarge     => "Tempo +1%",
        KeyboardAction.TempoDownLarge   => "Tempo −1%",
        KeyboardAction.CrossfaderLeft   => "Crossfader ←",
        KeyboardAction.CrossfaderRight  => "Crossfader →",
        KeyboardAction.EqHighUp         => "EQ High +",
        KeyboardAction.EqHighDown       => "EQ High −",
        KeyboardAction.EqMidUp          => "EQ Mid +",
        KeyboardAction.EqMidDown        => "EQ Mid −",
        KeyboardAction.EqLowUp          => "EQ Low +",
        KeyboardAction.EqLowDown        => "EQ Low −",
        KeyboardAction.FilterUp         => "Filter +",
        KeyboardAction.FilterDown       => "Filter −",
        KeyboardAction.ShowKeyboardOverlay => "Show Keyboard Overlay",
        _ => SplitPascalCase(action.ToString())
    };

    private static readonly Regex _pascalSplit =
        new(@"(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    private static string SplitPascalCase(string s)
    {
        // Insert spaces before uppercase runs and digits
        var spaced = _pascalSplit.Replace(s, " ");
        // Separate trailing digits: "HotCue1" → "Hot Cue 1"
        return Regex.Replace(spaced, @"(\D)(\d)", "$1 $2");
    }
}

// ─── Supporting display models ────────────────────────────────────────────────

public sealed class KeyGroupViewModel
{
    public string Category { get; }
    public IReadOnlyList<OverlayBindingRow> Bindings { get; }

    public KeyGroupViewModel(string category, IReadOnlyList<OverlayBindingRow> bindings)
    {
        Category = category;
        Bindings = bindings;
    }
}

public sealed class OverlayBindingRow
{
    public string Action   { get; }
    public string KeyCombo { get; }
    public int    Deck     { get; }

    /// <summary>"A", "B", "C", "D", or empty for global bindings.</summary>
    public string DeckTag => Deck switch { 1 => "A", 2 => "B", 3 => "C", 4 => "D", _ => "" };

    public OverlayBindingRow(string action, string keyCombo, int deck)
    {
        Action   = action;
        KeyCombo = keyCombo;
        Deck     = deck;
    }
}
