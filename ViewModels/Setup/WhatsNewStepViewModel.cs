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
        new WhatsNewItem("Sets has its own page", "Plan a gig as slot cards with a role, energy target and notes, and a compatibility score between every pair. Keep Plan B tracks per slot, search a track pool under the set, and add tracks from the Library with right-click → Add to Set."),
        new WhatsNewItem("Design transitions inside a set", "Double-click the score between two slots to open the transition editor. It's the same editor as Flow Builder (Transition Lab is gone), with a rating and a \"played it live\" box, and it starts from what you saved for that pair before."),
        new WhatsNewItem("Find a bridge track", "Select a transition in a set and choose Find a bridge track: analysed tracks close in BPM to both ends, ranked by how their intro and outro fit. Insert puts one between the slots."),
        new WhatsNewItem("Export a set", "Sets ⋯ → Export to Rekordbox XML or to a USB stick / folder: the slots in order as one Rekordbox playlist with cues and beat grid. Optionally add each transition's notes to the track comments."),
        new WhatsNewItem("Try a transition from the Library", "Select two tracks, right-click → Edit transition between these two: preview, mix points and a rating in the same editor as Flow Builder and Sets."),
        new WhatsNewItem("Flow Builder: drag the mix points", "Drag the marker on either deck to move the mix-out or mix-in. The incoming track snaps to its cues and to spots where one of its cues lands on a cue of the outgoing track; Shift snaps to beats, Alt moves freely."),
        new WhatsNewItem("Beat grids you can trust", "BPM fixes in Cue Forge update the grid itself, and a grid you edited or confirmed is never overwritten by re-analysis. The Library's Grid review chip lists tracks whose bar lines don't match the music."),
        new WhatsNewItem("Much better genre detection", "Genre now comes from the detailed style model plus a rhythm check (a kick on every beat vs backbeat vs half-time) instead of tempo ranges, so DnB, dubstep, techno and house are told apart far more often."),
        new WhatsNewItem("Refresh AI styles for your library", "A model input bug made earlier style, genre and mood guesses unreliable. In Library maintenance, tick \"Analyse tracks with missing or outdated AI style\" to redo them in the background."),
        new WhatsNewItem("Find exact duplicates", "Library ⋯ → Find exact duplicates compares the audio itself, so re-tagged or renamed copies are found too. Pick the copy to keep; its playlists, set slots and cues are merged. Files only go to the Recycle Bin if you tick that box."),
        new WhatsNewItem("Library maintenance", "One pass fixes malformed tags, fills missing metadata and groups unclassified tracks into style playlists, with a preview and a one-click undo (Library ⋯ menu or Settings → Maintenance)."),
        new WhatsNewItem("Already in your library", "When a download turns out to be audio you already have, the playlist points to your existing file and the row says so, instead of adding a second copy."),
        new WhatsNewItem("Steadier downloads", "Auto-download skips previews and cut-off files, two tracks from the same user no longer share progress, and download speed is measured correctly, so fewer downloads are wrongly treated as stalled."),
    };
}
