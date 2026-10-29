using System.Collections.Generic;

namespace SLSKDONET.ViewModels.Setup;

/// <summary>One highlight on the "What's new" page.</summary>
public sealed record WhatsNewItem(string Title, string Detail);

/// <summary>
/// Landing page for the upgrade re-onboarding: a short summary of what changed since the
/// first-run wizard. Informational only (like Welcome) — it writes nothing.
/// </summary>
public sealed class WhatsNewStepViewModel : SetupStepViewModel
{
    public override string Title => "What's new in ORBIT";
    public override string ShortTitle => "What's new";
    public override string Subtitle => "Here's what changed since you last set ORBIT up. Your settings are already in place; this page just points out what's new.";
    public override bool AppearsInSummary => false;

    public IReadOnlyList<WhatsNewItem> Items { get; } = new[]
    {
        new WhatsNewItem("Cue Forge overhaul", "Drop cues land on the real drop, with a docked track list, Preparation menu, layers, grid repair and a tri-band waveform."),
        new WhatsNewItem("Flow Mode plans your mixes", "Automatically picks the next track from key, tempo, energy and mood — and explains each choice."),
        new WhatsNewItem("Honest analysis coverage", "Eight analysis engines with per-track coverage; fill only what's missing instead of re-running everything."),
        new WhatsNewItem("Rekordbox import", "Read Rekordbox 6/7 cues and loops through the new RekordboxBridge companion."),
        new WhatsNewItem("Edit Lab", "Stem-based DJ edits render offline and land back in your library."),
        new WhatsNewItem("A faster, clearer app", "Quicker startup, per-playlist view state, a music-reactive visualizer, and a decluttered Library."),
        new WhatsNewItem("FFmpeg and THIS PC", "FFmpeg installs itself; the wizard shows which heavy features this machine can run."),
        new WhatsNewItem("Send feedback", "Ctrl+Shift+F zips a screenshot, note and scrubbed logs to your clipboard — nothing is uploaded."),
    };
}
