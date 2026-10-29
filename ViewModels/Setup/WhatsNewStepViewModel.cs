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
        new WhatsNewItem("Cue Forge: the right track, every time", "Auditioning a cue now always plays the track on screen, even after an auto-mix cross (the player attachment is fixed)."),
        new WhatsNewItem("Analysis with real progress", "A track analysis now shows its stage (for example \"Stage 3 of 8 · Extracting waveform\") instead of sitting at 20%."),
        new WhatsNewItem("Essentia-missing alert", "If the Essentia extractor isn't installed, ORBIT says so up front — BPM, key and mood are skipped rather than failing quietly."),
        new WhatsNewItem("Re-analysis survives track switches", "Queue a force re-analysis, switch tracks, and come back: the live status is still there (the run never cancels)."),
        new WhatsNewItem("Mix panel: fix and analyse", "The Mix sidepanel flags a deck with missing analysis or no cues and can open it in Cue Forge ready to analyse."),
        new WhatsNewItem("Flow Mode plans your mixes", "Automatically picks the next track from key, tempo, energy and mood — and explains each choice."),
        new WhatsNewItem("Honest analysis coverage", "Eight analysis engines with per-track coverage; fill only what's missing instead of re-running everything."),
        new WhatsNewItem("Rekordbox import", "Read Rekordbox 6/7 cues and loops through the RekordboxBridge companion."),
    };
}
