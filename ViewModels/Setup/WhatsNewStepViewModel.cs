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
        new WhatsNewItem("One-click updates", "The Updates page now downloads and runs the installer for you."),
        new WhatsNewItem("New Set from the picker", "Create a fresh set right from Set Projects, not just open existing ones."),
        new WhatsNewItem("Updates: pick your channel", "Choose Stable, Beta or Any, then check GitHub for a newer version from Settings → Advanced → Updates."),
        new WhatsNewItem("FFmpeg + .NET self-heal", "ORBIT installs FFmpeg and the .NET runtime automatically when they're missing."),
        new WhatsNewItem("Story Graph: plan your sets", "Branching set paths on a visual canvas — slot roles and energy targets with undo/redo."),
        new WhatsNewItem("Transition Lab: rate your mixes", "Audition the overlap, edit DSP parameters, and rate transitions so ORBIT learns what works."),
        new WhatsNewItem("Set Health: know your narrative", "Live narrative, technical and risk scores for every set, docked beside the Story Graph."),
        new WhatsNewItem("Copilot: let ORBIT curate", "Auto-build a set spine, find bridges between two tracks, and flag vocal/harmonic clashes."),
        new WhatsNewItem("Rekordbox export with knowledge", "Sets export with confidence and freshness baked into each track's comment."),
    };
}
