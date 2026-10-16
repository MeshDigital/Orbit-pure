using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// One hot cue pad (slots A–H) of a <see cref="CueEditorSession"/> (Cue Forge and the Flow Builder
/// deck strips). The eight instances live for the session's lifetime and are updated in place from
/// its cues, so the pad buttons (and any open context menu) aren't torn down and rebuilt on every edit.
/// </summary>
public sealed class HotCuePadInfo : ReactiveObject
{
    public int Slot { get; init; }
    public string PadLabel { get; init; } = "";

    /// <summary>Keyboard shortcut shown on the pad (1–8).</summary>
    public string KeyHint => (Slot + 1).ToString();

    private OrbitCue? _cue;
    /// <summary>The cue currently held by this pad, or null when the pad is empty.</summary>
    public OrbitCue? Cue
    {
        get => _cue;
        private set => this.RaiseAndSetIfChanged(ref _cue, value);
    }

    private string _cueName = "";
    public string CueName { get => _cueName; private set => this.RaiseAndSetIfChanged(ref _cueName, value); }

    private string _timestampDisplay = "";
    public string TimestampDisplay { get => _timestampDisplay; private set => this.RaiseAndSetIfChanged(ref _timestampDisplay, value); }

    /// <summary>The cue's own colour (data, e.g. a Rekordbox hot cue colour). Empty when unassigned.</summary>
    private string _color = "";
    public string Color { get => _color; private set => this.RaiseAndSetIfChanged(ref _color, value); }

    private bool _isAssigned;
    public bool IsAssigned { get => _isAssigned; private set => this.RaiseAndSetIfChanged(ref _isAssigned, value); }

    private bool _isSelected;
    /// <summary>True when the pad's cue is the one open in the inspector.</summary>
    public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

    public string ToolTip => IsAssigned
        ? $"{PadLabel}: {CueName} at {TimestampDisplay}. Click to play from here (key {KeyHint}); right-click for more."
        : $"{PadLabel}: empty. Click to set a hot cue at the playhead (key {KeyHint}).";

    public void Assign(OrbitCue? cue)
    {
        Cue = cue;
        IsAssigned = cue != null;
        CueName = cue == null ? "" : string.IsNullOrWhiteSpace(cue.Name) ? cue.Role.ToString() : cue.Name;
        TimestampDisplay = cue == null ? "" : CueForgeGrid.FormatTime(cue.Timestamp);
        Color = cue?.Color ?? "";
        this.RaisePropertyChanged(nameof(ToolTip));
    }
}
