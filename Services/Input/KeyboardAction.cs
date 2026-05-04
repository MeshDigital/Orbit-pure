using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services.Input;

/// <summary>Grouping category for <see cref="KeyboardAction"/> values (Epic #119).</summary>
public enum ActionCategory
{
    Deck,
    Mixer,
    Browser,
    FX,
    Global
}

/// <summary>All bindable DJ actions for the keyboard mapping system (Epic #119, Task 1).</summary>
public enum KeyboardAction
{
    // ── Deck actions ─────────────────────────────────────────────────────────
    PlayPause,
    Cue,
    // Jump to hot cue
    HotCue1, HotCue2, HotCue3, HotCue4, HotCue5, HotCue6, HotCue7, HotCue8,
    // Set hot cue (Shift-variant)
    SetHotCue1, SetHotCue2, SetHotCue3, SetHotCue4,
    SetHotCue5, SetHotCue6, SetHotCue7, SetHotCue8,
    // Loop
    LoopIn, LoopOut, LoopExit,
    LoopMoveForward, LoopMoveBack,
    HalfLoop, DoubleLoop,
    LoopRoll1, LoopRoll2, LoopRoll4, LoopRoll8,
    // Beat jump
    BeatJumpForward1,  BeatJumpBack1,
    BeatJumpForward2,  BeatJumpBack2,
    BeatJumpForward4,  BeatJumpBack4,
    BeatJumpForward8,  BeatJumpBack8,
    BeatJumpForward16, BeatJumpBack16,
    BeatJumpForward32, BeatJumpBack32,
    // Pitch / tempo
    PitchBendUp, PitchBendDown,
    TempoUpSmall, TempoDownSmall,
    TempoUpLarge, TempoDownLarge,
    // Sync
    Sync, MasterSync,
    // Slip / reverse
    SlipMode, Reverse, SlipReverse,
    // Quantize & key
    Quantize,
    KeyShiftUp, KeyShiftDown,
    KeyLock,
    // Extended deck controls (Rekordbox 4-Deck parity)
    JumpToBeginning,
    JumpForward,
    JumpBackward,
    AutoCue,
    VinylMode,
    TempoRange,
    KeyReset,
    AutoBeatLoop,

    // ── Mixer actions ─────────────────────────────────────────────────────────
    VolumeUp, VolumeDown,
    EqHighUp, EqHighDown,
    EqMidUp,  EqMidDown,
    EqLowUp,  EqLowDown,
    FilterUp, FilterDown,
    CrossfaderLeft, CrossfaderRight,
    HeadphoneCue,

    // ── Browser actions ───────────────────────────────────────────────────────
    BrowseUp, BrowseDown,
    LoadToDeck1, LoadToDeck2, LoadToDeck3, LoadToDeck4,
    SearchFocus,
    TagList,
    PlaylistNavigateUp, PlaylistNavigateDown,

    // ── FX actions ────────────────────────────────────────────────────────────
    Fx1Toggle, Fx2Toggle, Fx3Toggle,
    Fx1ParamUp, Fx1ParamDown,
    Fx2ParamUp, Fx2ParamDown,
    Fx3ParamUp, Fx3ParamDown,

    // ── Global app navigation ─────────────────────────────────────────────────
    NavigateWorkstation,
    NavigateLibrary,
    NavigateDownloads,
    NavigateSearch,
    NavigateSettings,
    OpenSettings,

    // ── Overlay ──────────────────────────────────────────────────────────────
    /// <summary>Toggle the keyboard-shortcut cheat-sheet overlay (default: F1).</summary>
    ShowKeyboardOverlay
}

/// <summary>
/// MIDI-ready abstraction layer (Epic #119, Task 17).
/// Any input source (keyboard or future MIDI) resolves to a <see cref="KeyboardAction"/>;
/// the rest of the system works only with this interface.
/// </summary>
public interface IDJAction
{
    KeyboardAction Action { get; }
    /// <summary>0 = Global (apply to focused deck), 1-4 = specific deck.</summary>
    int DeckTarget { get; }
}

/// <summary>Static category + metadata lookup helpers for <see cref="KeyboardAction"/>.</summary>
public static class KeyboardActionInfo
{
    private static readonly Dictionary<KeyboardAction, ActionCategory> _categories = BuildCategoryMap();

    public static ActionCategory GetCategory(KeyboardAction action) =>
        _categories.TryGetValue(action, out var cat) ? cat : ActionCategory.Global;

    public static IEnumerable<KeyboardAction> GetByCategory(ActionCategory category) =>
        _categories.Where(kv => kv.Value == category).Select(kv => kv.Key);

    /// <summary>All actions in enum order.</summary>
    public static IEnumerable<KeyboardAction> All =>
        System.Enum.GetValues<KeyboardAction>();

    private static Dictionary<KeyboardAction, ActionCategory> BuildCategoryMap()
    {
        var map = new Dictionary<KeyboardAction, ActionCategory>();

        void Add(ActionCategory cat, KeyboardAction[] actions)
        {
            foreach (var a in actions) map[a] = cat;
        }

        Add(ActionCategory.Deck, new[]
        {
            KeyboardAction.PlayPause, KeyboardAction.Cue,
            KeyboardAction.HotCue1,    KeyboardAction.HotCue2,    KeyboardAction.HotCue3,    KeyboardAction.HotCue4,
            KeyboardAction.HotCue5,    KeyboardAction.HotCue6,    KeyboardAction.HotCue7,    KeyboardAction.HotCue8,
            KeyboardAction.SetHotCue1, KeyboardAction.SetHotCue2, KeyboardAction.SetHotCue3, KeyboardAction.SetHotCue4,
            KeyboardAction.SetHotCue5, KeyboardAction.SetHotCue6, KeyboardAction.SetHotCue7, KeyboardAction.SetHotCue8,
            KeyboardAction.LoopIn,     KeyboardAction.LoopOut,     KeyboardAction.LoopExit,
            KeyboardAction.LoopMoveForward, KeyboardAction.LoopMoveBack,
            KeyboardAction.HalfLoop, KeyboardAction.DoubleLoop,
            KeyboardAction.LoopRoll1, KeyboardAction.LoopRoll2, KeyboardAction.LoopRoll4, KeyboardAction.LoopRoll8,
            KeyboardAction.BeatJumpForward1,  KeyboardAction.BeatJumpBack1,
            KeyboardAction.BeatJumpForward2,  KeyboardAction.BeatJumpBack2,
            KeyboardAction.BeatJumpForward4,  KeyboardAction.BeatJumpBack4,
            KeyboardAction.BeatJumpForward8,  KeyboardAction.BeatJumpBack8,
            KeyboardAction.BeatJumpForward16, KeyboardAction.BeatJumpBack16,
            KeyboardAction.BeatJumpForward32, KeyboardAction.BeatJumpBack32,
            KeyboardAction.PitchBendUp,    KeyboardAction.PitchBendDown,
            KeyboardAction.TempoUpSmall,   KeyboardAction.TempoDownSmall,
            KeyboardAction.TempoUpLarge,   KeyboardAction.TempoDownLarge,
            KeyboardAction.Sync,           KeyboardAction.MasterSync,
            KeyboardAction.SlipMode,       KeyboardAction.Reverse,    KeyboardAction.SlipReverse,
            KeyboardAction.Quantize,
            KeyboardAction.KeyShiftUp,     KeyboardAction.KeyShiftDown,
            KeyboardAction.KeyLock,
            KeyboardAction.JumpToBeginning, KeyboardAction.JumpForward, KeyboardAction.JumpBackward,
            KeyboardAction.AutoCue, KeyboardAction.VinylMode, KeyboardAction.TempoRange,
            KeyboardAction.KeyReset, KeyboardAction.AutoBeatLoop,
        });

        Add(ActionCategory.Mixer, new[]
        {
            KeyboardAction.VolumeUp,       KeyboardAction.VolumeDown,
            KeyboardAction.EqHighUp,       KeyboardAction.EqHighDown,
            KeyboardAction.EqMidUp,        KeyboardAction.EqMidDown,
            KeyboardAction.EqLowUp,        KeyboardAction.EqLowDown,
            KeyboardAction.FilterUp,       KeyboardAction.FilterDown,
            KeyboardAction.CrossfaderLeft, KeyboardAction.CrossfaderRight,
            KeyboardAction.HeadphoneCue,
        });

        Add(ActionCategory.Browser, new[]
        {
            KeyboardAction.BrowseUp,            KeyboardAction.BrowseDown,
            KeyboardAction.LoadToDeck1,         KeyboardAction.LoadToDeck2,
            KeyboardAction.LoadToDeck3,         KeyboardAction.LoadToDeck4,
            KeyboardAction.SearchFocus,         KeyboardAction.TagList,
            KeyboardAction.PlaylistNavigateUp,  KeyboardAction.PlaylistNavigateDown,
        });

        Add(ActionCategory.FX, new[]
        {
            KeyboardAction.Fx1Toggle,    KeyboardAction.Fx2Toggle,    KeyboardAction.Fx3Toggle,
            KeyboardAction.Fx1ParamUp,   KeyboardAction.Fx1ParamDown,
            KeyboardAction.Fx2ParamUp,   KeyboardAction.Fx2ParamDown,
            KeyboardAction.Fx3ParamUp,   KeyboardAction.Fx3ParamDown,
        });

        Add(ActionCategory.Global, new[]
        {
            KeyboardAction.NavigateWorkstation,
            KeyboardAction.NavigateLibrary,
            KeyboardAction.NavigateDownloads,
            KeyboardAction.NavigateSearch,
            KeyboardAction.NavigateSettings,
            KeyboardAction.OpenSettings,
            KeyboardAction.ShowKeyboardOverlay,
        });

        return map;
    }
}
