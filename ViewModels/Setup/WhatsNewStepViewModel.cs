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
        new WhatsNewItem("Find exact duplicates", "Library ⋯ → Find exact duplicates compares the audio itself, so re-tagged or renamed copies are found too. Pick the copy to keep; its playlists, set slots and cues are merged. Files only go to the Recycle Bin if you tick that box."),
        new WhatsNewItem("Already in your library", "When a download turns out to be audio you already have, the playlist points to your existing file and the row says so, instead of adding a second copy."),
        new WhatsNewItem("No more previews", "Auto-download now skips previews and cut-off files: a file too small for its length and bitrate is rejected before it downloads."),
        new WhatsNewItem("Transitions snap to cues", "Dragging the incoming track in the transition editor snaps to its cues, and to spots where one of its cues lands on a cue of the outgoing track; otherwise it moves bar by bar. Shift snaps to beats, Alt moves freely."),
        new WhatsNewItem("Steadier downloads", "Two tracks from the same user no longer share progress, and download speed is measured correctly, so fewer downloads are wrongly treated as stalled and retried."),
        new WhatsNewItem("Library maintenance opens", "The Library maintenance window (Library ⋯ menu or Settings → Maintenance) now actually opens; before, the button did nothing."),
    };
}
