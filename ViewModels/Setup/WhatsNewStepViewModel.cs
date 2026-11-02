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
        new WhatsNewItem("Sets has its own page", "Plan a gig as a row of slot cards: role, energy target and notes per slot, with a compatibility score between every pair. Drag to reorder; Ctrl+Z undoes any change."),
        new WhatsNewItem("Plan B for every slot", "Keep backup tracks under a slot and swap one in with a click. The slot keeps its role and energy target."),
        new WhatsNewItem("Track pool", "Search your library under the set, filter to tracks in key or within 4% BPM of the selected slot, or add a whole playlist at once."),
        new WhatsNewItem("Set Health that doesn't guess", "Scores appear only when there's data behind them, and a list points at what to fix: unanalysed tracks, missing files, risky transitions."),
        new WhatsNewItem("Design transitions inside a set", "Double-click the score between two slots to open the transition editor: mix points, blend, preview. It saves into the set and starts from what you saved for that pair before."),
        new WhatsNewItem("Rate your transitions", "Every transition editor now has a rating and a \"played it live\" box. ORBIT remembers them per track pair and shows them in your sets."),
        new WhatsNewItem("Add to Set from the Library", "Right-click tracks → Add to Set → pick a recent set or start a new one. Works on a whole selection."),
        new WhatsNewItem("Workstation is back to track prep", "Set planning moved to the Sets page, and Transition Lab is replaced by the one transition editor used everywhere."),
        new WhatsNewItem("Background scan is opt-in", "The transition-candidate scan after each analysis is off by default. Turn it on in Settings → Advanced → Knowledge agents."),
        new WhatsNewItem("One-click updates", "The Updates page now downloads and runs the installer for you."),
    };
}
