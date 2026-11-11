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
        new WhatsNewItem("Set edits now stick", "Role and energy changes on the Story Graph are saved straight away, and adding a track no longer undoes them. Click a slot to select it."),
        new WhatsNewItem("Find any track for a set", "The Add Track picker now searches your whole library by title or artist."),
        new WhatsNewItem("Transition Lab is moving", "It's hidden for now while it merges into Flow Builder's transition editor, so there's one place to edit and rate transitions."),
        new WhatsNewItem("Background scan is opt-in", "The transition-candidate scan after each analysis is off by default. Turn it on in Settings → Advanced → Knowledge agents."),
        new WhatsNewItem("One-click updates", "The Updates page now downloads and runs the installer for you."),
        new WhatsNewItem("Updates: pick your channel", "Choose Stable, Beta or Any, then check GitHub for a newer version from Settings → Advanced → Updates."),
        new WhatsNewItem("FFmpeg + .NET self-heal", "ORBIT installs FFmpeg and the .NET runtime automatically when they're missing."),
        new WhatsNewItem("Set Health: know your narrative", "Narrative, technical and risk scores for the open set, docked beside the Story Graph."),
        new WhatsNewItem("Library maintenance", "One pass fixes malformed tags, fills missing metadata and groups unclassified tracks into style playlists — with a preview and a one-click undo."),
    };
}
