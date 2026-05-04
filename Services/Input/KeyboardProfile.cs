using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace SLSKDONET.Services.Input;

/// <summary>Built-in preset identifiers.</summary>
public enum BuiltInPreset
{
    OrbitDefault,
    Rekordbox,
    DjStudio,
    RekordboxFourDeck
}

/// <summary>
/// A named collection of <see cref="KeyboardBinding"/>s that can be serialised to/from JSON
/// and saved to disk (Epic #119, Task 3).
/// </summary>
public class KeyboardProfile
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Name        { get; set; } = "Orbit Default";
    public string Version     { get; set; } = "1.0";
    public string Description { get; set; } = "";

    public List<KeyboardBinding> Bindings { get; set; } = new();

    // ─── JSON I/O ─────────────────────────────────────────────────────────────

    public string ToJson() => JsonSerializer.Serialize(this, _jsonOptions);

    public static KeyboardProfile FromJson(string json) =>
        JsonSerializer.Deserialize<KeyboardProfile>(json, _jsonOptions)
        ?? throw new InvalidOperationException("Failed to deserialize keyboard profile.");

    public void SaveToFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }

    public static KeyboardProfile LoadFromFile(string path) =>
        FromJson(File.ReadAllText(path));

    // ─── Built-in presets ─────────────────────────────────────────────────────

    public static KeyboardProfile GetBuiltIn(BuiltInPreset preset) => preset switch
    {
        BuiltInPreset.Rekordbox        => CreateRekordboxPreset(),
        BuiltInPreset.DjStudio         => CreateDjStudioPreset(),
        BuiltInPreset.RekordboxFourDeck => CreateRekordboxFourDeckPreset(),
        _                              => CreateOrbitDefaultPreset()
    };

    // ── Orbit Default ──────────────────────────────────────────────────────────
    // Space=Play/Pause, Q=Cue, F1-F8=HotCues, [=LoopIn, ]=LoopOut
    // Ctrl+←/→=BeatJump±4, ↑↓=Browse, Ctrl+1-5=App nav

    private static KeyboardProfile CreateOrbitDefaultPreset()
    {
        var bindings = new List<KeyboardBinding>
        {
            // Playback
            Bind(Key.Space,   KeyModifiers.None,    0, KeyboardAction.PlayPause),
            Bind(Key.Q,       KeyModifiers.None,    0, KeyboardAction.Cue),

            // Hot cues F1-F8 (jump)
            Bind(Key.F1,  KeyModifiers.None,    0, KeyboardAction.HotCue1),
            Bind(Key.F2,  KeyModifiers.None,    0, KeyboardAction.HotCue2),
            Bind(Key.F3,  KeyModifiers.None,    0, KeyboardAction.HotCue3),
            Bind(Key.F4,  KeyModifiers.None,    0, KeyboardAction.HotCue4),
            Bind(Key.F5,  KeyModifiers.None,    0, KeyboardAction.HotCue5),
            Bind(Key.F6,  KeyModifiers.None,    0, KeyboardAction.HotCue6),
            Bind(Key.F7,  KeyModifiers.None,    0, KeyboardAction.HotCue7),
            Bind(Key.F8,  KeyModifiers.None,    0, KeyboardAction.HotCue8),

            // Hot cues Shift+F1-F8 (set)
            Bind(Key.F1,  KeyModifiers.Shift,   0, KeyboardAction.SetHotCue1),
            Bind(Key.F2,  KeyModifiers.Shift,   0, KeyboardAction.SetHotCue2),
            Bind(Key.F3,  KeyModifiers.Shift,   0, KeyboardAction.SetHotCue3),
            Bind(Key.F4,  KeyModifiers.Shift,   0, KeyboardAction.SetHotCue4),
            Bind(Key.F5,  KeyModifiers.Shift,   0, KeyboardAction.SetHotCue5),
            Bind(Key.F6,  KeyModifiers.Shift,   0, KeyboardAction.SetHotCue6),
            Bind(Key.F7,  KeyModifiers.Shift,   0, KeyboardAction.SetHotCue7),
            Bind(Key.F8,  KeyModifiers.Shift,   0, KeyboardAction.SetHotCue8),

            // Loop
            Bind(Key.OemOpenBrackets,  KeyModifiers.None,    0, KeyboardAction.LoopIn),
            Bind(Key.OemCloseBrackets, KeyModifiers.None,    0, KeyboardAction.LoopOut),
            Bind(Key.OemOpenBrackets,  KeyModifiers.Shift,   0, KeyboardAction.LoopExit),
            Bind(Key.H,                KeyModifiers.None,    0, KeyboardAction.HalfLoop),
            Bind(Key.J,                KeyModifiers.None,    0, KeyboardAction.DoubleLoop),

            // Beat jump Ctrl+arrows (±4 bars)
            Bind(Key.Right, KeyModifiers.Control, 0, KeyboardAction.BeatJumpForward4),
            Bind(Key.Left,  KeyModifiers.Control, 0, KeyboardAction.BeatJumpBack4),

            // Sync
            Bind(Key.S, KeyModifiers.None,    0, KeyboardAction.Sync),
            Bind(Key.S, KeyModifiers.Shift,   0, KeyboardAction.MasterSync),

            // Key lock
            Bind(Key.K, KeyModifiers.None,    0, KeyboardAction.KeyLock),

            // Browser: Up/Down arrows
            Bind(Key.Up,    KeyModifiers.None, 0, KeyboardAction.BrowseUp),
            Bind(Key.Down,  KeyModifiers.None, 0, KeyboardAction.BrowseDown),
            Bind(Key.Enter, KeyModifiers.None, 0, KeyboardAction.LoadToDeck1),

            // Search focus
            Bind(Key.F, KeyModifiers.Control, 0, KeyboardAction.SearchFocus),

            // Global navigation Ctrl+1-5 (mirrors existing shortcuts)
            Bind(Key.D1, KeyModifiers.Control, 0, KeyboardAction.NavigateWorkstation),
            Bind(Key.D2, KeyModifiers.Control, 0, KeyboardAction.NavigateLibrary),
            Bind(Key.D3, KeyModifiers.Control, 0, KeyboardAction.NavigateDownloads),
            Bind(Key.D4, KeyModifiers.Control, 0, KeyboardAction.NavigateSearch),
            Bind(Key.D5, KeyModifiers.Control, 0, KeyboardAction.NavigateSettings),

            // Keyboard overlay cheat-sheet
            Bind(Key.F1, KeyModifiers.Control, 0, KeyboardAction.ShowKeyboardOverlay),
        };

        return new KeyboardProfile
        {
            Name = "Orbit Default",
            Version = "1.0",
            Description = "Default Orbit layout. Space=Play, F1-F8=Hot Cues, Ctrl+←/→=Beat Jump.",
            Bindings = bindings
        };
    }

    // ── Rekordbox-style ────────────────────────────────────────────────────────
    // Q=Play/Pause, W=Cue, A-K hotcues (A=1…K=8), Z=LoopIn, X=LoopOut, ←→=BeatJump, ↑↓=Browse

    private static KeyboardProfile CreateRekordboxPreset()
    {
        var bindings = new List<KeyboardBinding>
        {
            Bind(Key.Q, KeyModifiers.None, 0, KeyboardAction.PlayPause),
            Bind(Key.W, KeyModifiers.None, 0, KeyboardAction.Cue),

            // Hot cues – A S D F G H J K (home row)
            Bind(Key.A, KeyModifiers.None,  0, KeyboardAction.HotCue1),
            Bind(Key.S, KeyModifiers.None,  0, KeyboardAction.HotCue2),
            Bind(Key.D, KeyModifiers.None,  0, KeyboardAction.HotCue3),
            Bind(Key.F, KeyModifiers.None,  0, KeyboardAction.HotCue4),
            Bind(Key.G, KeyModifiers.None,  0, KeyboardAction.HotCue5),
            Bind(Key.H, KeyModifiers.None,  0, KeyboardAction.HotCue6),
            Bind(Key.J, KeyModifiers.None,  0, KeyboardAction.HotCue7),
            Bind(Key.K, KeyModifiers.None,  0, KeyboardAction.HotCue8),

            // Set hot cues – Shift+homerow
            Bind(Key.A, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue1),
            Bind(Key.S, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue2),
            Bind(Key.D, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue3),
            Bind(Key.F, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue4),
            Bind(Key.G, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue5),
            Bind(Key.H, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue6),
            Bind(Key.J, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue7),
            Bind(Key.K, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue8),

            // Loop – Z/X/C
            Bind(Key.Z, KeyModifiers.None,  0, KeyboardAction.LoopIn),
            Bind(Key.X, KeyModifiers.None,  0, KeyboardAction.LoopOut),
            Bind(Key.C, KeyModifiers.None,  0, KeyboardAction.LoopExit),
            Bind(Key.V, KeyModifiers.None,  0, KeyboardAction.HalfLoop),
            Bind(Key.B, KeyModifiers.None,  0, KeyboardAction.DoubleLoop),

            // Beat jump – arrows
            Bind(Key.Right, KeyModifiers.None,  0, KeyboardAction.BeatJumpForward1),
            Bind(Key.Left,  KeyModifiers.None,  0, KeyboardAction.BeatJumpBack1),
            Bind(Key.Right, KeyModifiers.Shift, 0, KeyboardAction.BeatJumpForward4),
            Bind(Key.Left,  KeyModifiers.Shift, 0, KeyboardAction.BeatJumpBack4),

            // Sync
            Bind(Key.T, KeyModifiers.None, 0, KeyboardAction.Sync),

            // Browse – up/down arrows
            Bind(Key.Up,    KeyModifiers.None, 0, KeyboardAction.BrowseUp),
            Bind(Key.Down,  KeyModifiers.None, 0, KeyboardAction.BrowseDown),
            Bind(Key.Enter, KeyModifiers.None, 0, KeyboardAction.LoadToDeck1),

            // Keyboard overlay cheat-sheet
            Bind(Key.F1, KeyModifiers.Control, 0, KeyboardAction.ShowKeyboardOverlay),
        };

        return new KeyboardProfile
        {
            Name = "Rekordbox Style",
            Version = "1.0",
            Description = "Inspired by Rekordbox DJ keyboard layout. Q=Play, W=Cue, A-K=Hot Cues, Z/X=Loop.",
            Bindings = bindings
        };
    }

    // ── DJ.Studio-style ────────────────────────────────────────────────────────
    // Space=Play, 1-8=HotCues, Q=Cue, A=LoopIn, D=LoopOut, S=LoopExit, ←→=BeatJump, ↑↓=Browse

    private static KeyboardProfile CreateDjStudioPreset()
    {
        var bindings = new List<KeyboardBinding>
        {
            Bind(Key.Space, KeyModifiers.None, 0, KeyboardAction.PlayPause),
            Bind(Key.Q,     KeyModifiers.None, 0, KeyboardAction.Cue),

            // Hot cues – 1-8 numeric row
            Bind(Key.D1, KeyModifiers.None,  0, KeyboardAction.HotCue1),
            Bind(Key.D2, KeyModifiers.None,  0, KeyboardAction.HotCue2),
            Bind(Key.D3, KeyModifiers.None,  0, KeyboardAction.HotCue3),
            Bind(Key.D4, KeyModifiers.None,  0, KeyboardAction.HotCue4),
            Bind(Key.D5, KeyModifiers.None,  0, KeyboardAction.HotCue5),
            Bind(Key.D6, KeyModifiers.None,  0, KeyboardAction.HotCue6),
            Bind(Key.D7, KeyModifiers.None,  0, KeyboardAction.HotCue7),
            Bind(Key.D8, KeyModifiers.None,  0, KeyboardAction.HotCue8),

            // Set hot cues – Shift+1-8
            Bind(Key.D1, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue1),
            Bind(Key.D2, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue2),
            Bind(Key.D3, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue3),
            Bind(Key.D4, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue4),
            Bind(Key.D5, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue5),
            Bind(Key.D6, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue6),
            Bind(Key.D7, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue7),
            Bind(Key.D8, KeyModifiers.Shift, 0, KeyboardAction.SetHotCue8),

            // Loop – A / D / S
            Bind(Key.A, KeyModifiers.None, 0, KeyboardAction.LoopIn),
            Bind(Key.D, KeyModifiers.None, 0, KeyboardAction.LoopOut),
            Bind(Key.S, KeyModifiers.None, 0, KeyboardAction.LoopExit),
            Bind(Key.W, KeyModifiers.None, 0, KeyboardAction.HalfLoop),
            Bind(Key.E, KeyModifiers.None, 0, KeyboardAction.DoubleLoop),

            // Beat jump – arrows
            Bind(Key.Right, KeyModifiers.None,  0, KeyboardAction.BeatJumpForward1),
            Bind(Key.Left,  KeyModifiers.None,  0, KeyboardAction.BeatJumpBack1),
            Bind(Key.Right, KeyModifiers.Shift, 0, KeyboardAction.BeatJumpForward4),
            Bind(Key.Left,  KeyModifiers.Shift, 0, KeyboardAction.BeatJumpBack4),

            // Sync
            Bind(Key.F, KeyModifiers.None, 0, KeyboardAction.Sync),

            // Browse – up/down
            Bind(Key.Up,    KeyModifiers.None, 0, KeyboardAction.BrowseUp),
            Bind(Key.Down,  KeyModifiers.None, 0, KeyboardAction.BrowseDown),
            Bind(Key.Enter, KeyModifiers.None, 0, KeyboardAction.LoadToDeck1),

            // Search
            Bind(Key.OemQuestion, KeyModifiers.None, 0, KeyboardAction.SearchFocus),

            // Keyboard overlay cheat-sheet
            Bind(Key.F1, KeyModifiers.Control, 0, KeyboardAction.ShowKeyboardOverlay),
        };

        return new KeyboardProfile
        {
            Name = "DJ.Studio Style",
            Version = "1.0",
            Description = "Inspired by DJ.Studio Pro keyboard layout. Space=Play, 1-8=Hot Cues, A/D=Loop.",
            Bindings = bindings
        };
    }

    // ─── Factory helper ───────────────────────────────────────────────────────

    private static KeyboardBinding Bind(Key key, KeyModifiers mods, int deck, KeyboardAction action) =>
        new() { Key = key, Modifiers = mods, Deck = deck, Action = action };

    // ── Rekordbox 4-Deck (Nexu layout) ────────────────────────────────────────
    // Based on Performance Keyboard (4 Channels) for Windows PC v1.1 by Nexu.
    // CH1 = no modifier, CH2 = separate base keys, CH3 = Alt+CH1, CH4 = Alt+CH2.
    // Bindings use explicit Deck=1-4 so the router ignores focus for these keys.

    private static KeyboardProfile CreateRekordboxFourDeckPreset()
    {
        var b = new List<KeyboardBinding>();

        // ─ Helper: add binding for all 4 decks ───────────────────────────────
        // CH1/CH2 use distinct base keys; CH3 = Alt+CH1key, CH4 = Alt+CH2key.
        void Deck4(Key ch1, Key ch2, KeyModifiers mods, KeyboardAction action)
        {
            b.Add(Bind(ch1, mods,                     1, action));
            b.Add(Bind(ch2, mods,                     2, action));
            b.Add(Bind(ch1, mods | KeyModifiers.Alt,  3, action));
            b.Add(Bind(ch2, mods | KeyModifiers.Alt,  4, action));
        }

        // ── Play / Cue ────────────────────────────────────────────────────────
        Deck4(Key.X, Key.M, KeyModifiers.None,  KeyboardAction.PlayPause);
        Deck4(Key.Z, Key.N, KeyModifiers.None,  KeyboardAction.Cue);
        Deck4(Key.Z, Key.N, KeyModifiers.Shift, KeyboardAction.JumpToBeginning);
        Deck4(Key.Z, Key.N, KeyModifiers.Control, KeyboardAction.AutoCue);

        // ── Reverse / Slip ────────────────────────────────────────────────────
        Deck4(Key.X, Key.M, KeyModifiers.Shift,   KeyboardAction.Reverse);
        Deck4(Key.X, Key.M, KeyModifiers.Control, KeyboardAction.SlipReverse);

        // ── Quantize / Slip / Vinyl ───────────────────────────────────────────
        Deck4(Key.D5, Key.D0, KeyModifiers.None,                     KeyboardAction.Quantize);
        Deck4(Key.D5, Key.D0, KeyModifiers.Shift,                    KeyboardAction.SlipMode);
        Deck4(Key.D5, Key.D0, KeyModifiers.Control | KeyModifiers.Shift, KeyboardAction.VinylMode);

        // ── Sync / TempoRange / MasterTempo ──────────────────────────────────
        Deck4(Key.B, Key.OemQuestion, KeyModifiers.None,                       KeyboardAction.Sync);
        Deck4(Key.B, Key.OemQuestion, KeyModifiers.Control,                    KeyboardAction.MasterSync);
        Deck4(Key.B, Key.OemQuestion, KeyModifiers.Shift,                      KeyboardAction.TempoRange);
        Deck4(Key.B, Key.OemQuestion, KeyModifiers.Control | KeyModifiers.Shift, KeyboardAction.KeyLock);

        // ── Pitch bend / Tempo fine ───────────────────────────────────────────
        Deck4(Key.V, Key.OemPeriod, KeyModifiers.Control,                    KeyboardAction.PitchBendUp);
        Deck4(Key.C, Key.OemComma,  KeyModifiers.Control,                    KeyboardAction.PitchBendDown);
        Deck4(Key.V, Key.OemPeriod, KeyModifiers.Control | KeyModifiers.Shift, KeyboardAction.TempoUpSmall);
        Deck4(Key.C, Key.OemComma,  KeyModifiers.Control | KeyModifiers.Shift, KeyboardAction.TempoDownSmall);

        // ── Jump fwd/back (coarse) ────────────────────────────────────────────
        Deck4(Key.V, Key.OemPeriod, KeyModifiers.Shift, KeyboardAction.JumpForward);
        Deck4(Key.C, Key.OemComma,  KeyModifiers.Shift, KeyboardAction.JumpBackward);

        // ── Semitone / Key reset ──────────────────────────────────────────────
        Deck4(Key.S, Key.J, KeyModifiers.Control, KeyboardAction.KeyShiftUp);
        Deck4(Key.A, Key.H, KeyModifiers.Control, KeyboardAction.KeyShiftDown);
        Deck4(Key.D, Key.K, KeyModifiers.Control, KeyboardAction.KeyReset);

        // ── Loop controls ─────────────────────────────────────────────────────
        Deck4(Key.A, Key.H, KeyModifiers.None,  KeyboardAction.LoopIn);
        Deck4(Key.S, Key.J, KeyModifiers.None,  KeyboardAction.LoopOut);
        Deck4(Key.D, Key.K, KeyModifiers.None,  KeyboardAction.LoopExit);
        Deck4(Key.D, Key.K, KeyModifiers.Shift, KeyboardAction.AutoBeatLoop);
        Deck4(Key.F, Key.L, KeyModifiers.None,  KeyboardAction.HalfLoop);
        // G / OemSemicolon = Double Loop
        b.Add(Bind(Key.G,            KeyModifiers.None,                    1, KeyboardAction.DoubleLoop));
        b.Add(Bind(Key.OemSemicolon, KeyModifiers.None,                    2, KeyboardAction.DoubleLoop));
        b.Add(Bind(Key.G,            KeyModifiers.Alt,                     3, KeyboardAction.DoubleLoop));
        b.Add(Bind(Key.OemSemicolon, KeyModifiers.Alt,                     4, KeyboardAction.DoubleLoop));

        // ── Headphone cue (Mixer) ─────────────────────────────────────────────
        Deck4(Key.T, Key.P, KeyModifiers.None, KeyboardAction.HeadphoneCue);

        // ── Volume fader (Mixer) ──────────────────────────────────────────────
        Deck4(Key.V, Key.OemPeriod, KeyModifiers.None, KeyboardAction.VolumeUp);
        Deck4(Key.C, Key.OemComma,  KeyModifiers.None, KeyboardAction.VolumeDown);

        // ── Hot cues — pads A–H (jump) ────────────────────────────────────────
        // CH1: 1,2,3,4,Q,W,E,R  |  CH2: 6,7,8,9,Y,U,I,O
        var ch1PadKeys = new[] { Key.D1, Key.D2, Key.D3, Key.D4, Key.Q, Key.W, Key.E, Key.R };
        var ch2PadKeys = new[] { Key.D6, Key.D7, Key.D8, Key.D9, Key.Y, Key.U, Key.I, Key.O };
        var hotCueJump = new[] {
            KeyboardAction.HotCue1, KeyboardAction.HotCue2, KeyboardAction.HotCue3, KeyboardAction.HotCue4,
            KeyboardAction.HotCue5, KeyboardAction.HotCue6, KeyboardAction.HotCue7, KeyboardAction.HotCue8,
        };
        var hotCueSet = new[] {
            KeyboardAction.SetHotCue1, KeyboardAction.SetHotCue2, KeyboardAction.SetHotCue3, KeyboardAction.SetHotCue4,
            KeyboardAction.SetHotCue5, KeyboardAction.SetHotCue6, KeyboardAction.SetHotCue7, KeyboardAction.SetHotCue8,
        };
        for (int i = 0; i < 8; i++)
        {
            b.Add(Bind(ch1PadKeys[i], KeyModifiers.None,  1, hotCueJump[i]));
            b.Add(Bind(ch2PadKeys[i], KeyModifiers.None,  2, hotCueJump[i]));
            b.Add(Bind(ch1PadKeys[i], KeyModifiers.Alt,   3, hotCueJump[i]));
            b.Add(Bind(ch2PadKeys[i], KeyModifiers.Alt,   4, hotCueJump[i]));
            // Shift variants = set hot cue
            b.Add(Bind(ch1PadKeys[i], KeyModifiers.Shift,                   1, hotCueSet[i]));
            b.Add(Bind(ch2PadKeys[i], KeyModifiers.Shift,                   2, hotCueSet[i]));
            b.Add(Bind(ch1PadKeys[i], KeyModifiers.Shift | KeyModifiers.Alt, 3, hotCueSet[i]));
            b.Add(Bind(ch2PadKeys[i], KeyModifiers.Shift | KeyModifiers.Alt, 4, hotCueSet[i]));
        }

        // ── Crossfader ────────────────────────────────────────────────────────
        b.Add(Bind(Key.Left,  KeyModifiers.None, 0, KeyboardAction.CrossfaderLeft));
        b.Add(Bind(Key.Right, KeyModifiers.None, 0, KeyboardAction.CrossfaderRight));

        // ── Browser (global) ──────────────────────────────────────────────────
        b.Add(Bind(Key.Up,    KeyModifiers.None,  0, KeyboardAction.BrowseUp));
        b.Add(Bind(Key.Down,  KeyModifiers.None,  0, KeyboardAction.BrowseDown));
        b.Add(Bind(Key.Left,  KeyModifiers.Shift, 0, KeyboardAction.LoadToDeck1));
        b.Add(Bind(Key.Right, KeyModifiers.Shift, 0, KeyboardAction.LoadToDeck2));

        // ── Search ────────────────────────────────────────────────────────────
        b.Add(Bind(Key.Space, KeyModifiers.Control, 0, KeyboardAction.SearchFocus));

        // ── Keyboard overlay cheat-sheet ──────────────────────────────────────
        b.Add(Bind(Key.F1, KeyModifiers.Control, 0, KeyboardAction.ShowKeyboardOverlay));

        return new KeyboardProfile
        {
            Name        = "Rekordbox 4-Deck",
            Version     = "1.1",
            Description = "Full 4-channel Rekordbox DJ Performance layout (Nexu v1.1). " +
                          "CH1=no modifier, CH2=separate base keys, CH3=Alt+CH1, CH4=Alt+CH2. " +
                          "Keys are hardwired to their channel regardless of focused deck.",
            Bindings    = b
        };
    }
}
