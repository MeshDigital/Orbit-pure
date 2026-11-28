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
        new WhatsNewItem("Sample-accurate Auto-Mix bus with brickwall limiter", "Both decks now mix through one unified stereo float bus running at your output device's native sample rate (no device restarts between 44.1 / 48 / 96 kHz tracks), with 0-sample-jitter deck starts, sub-6 ms EQ/gain automation, a −1 dBFS stereo-linked brickwall limiter preventing overlap clipping, and fixed FLAC/M4A background-seek disposal."),
        new WhatsNewItem("Pro Cue Forge: post-drop rules, 2nd-drop fallback & quieter hints", "Configure [OUT] bar offsets (+16/+32/+48/+64/Custom) and optional post-drop memory cues ([D1 +16]) right in the DROP RULES bar, automatic Drop 2 detection falls back to the stored energy curve when phrases miss it, section suggestions stay hidden once mix cues exist, and the live incoming-mix banner tracks active crossfades."),
        new WhatsNewItem("Sharper downbeat & bar-line detection", "Fixed a 1-beat early downbeat bias in BeatGridFitter by compensating sub-bass return window lead, removing over-shifted dropout lag, and adding structural-stripping returns (lifting bar-line accuracy from 24% → 43% vs Rekordbox and 43% → 54% vs hand-placed drops without touching user grids)."),
        new WhatsNewItem("Rekordbox playlist sync, backup/restore & missing-track XML export", "Direct Rekordbox writes keep countdowns and post-drop cues on the memory strip, sync Rekordbox's companion masterPlaylists6.xml atomically alongside master.db (included in backup & restore), and offer one-click rekordbox.xml export for tracks not yet in your Rekordbox collection."),
        new WhatsNewItem("Export playlists directly to USB / folder", "Right-click any playlist in Library → Export → Export to USB / folder… to copy its audio files and PIONEER/rekordbox.xml straight to a USB drive or external SSD."),
        new WhatsNewItem("Faster large libraries (30k+ tracks)", "Added a descending AddedAt index and single-pass genre index rebuild, cutting initial library page loads by 15× and multi-page scrolling by 22×."),
        new WhatsNewItem("Browse, play & collect by Genre", "A collapsible GENRES tree now sits under PLAYLISTS in the Library sidebar, synced with a removable filter chip and the Dashboard's Genre Galaxy. Header Play/Mix now plays the active filtered/sorted view (including All Tracks), and any genre can be saved as a snapshot playlist or live smart crate."),
        new WhatsNewItem("Library polish: tag cleanup, bulk relocate & playlist tools", "Tag cleanup now strips URLs, promo text, URL/HTML escapes, mojibake, garbage characters, normalizes mix parentheses, and copies remixers without breaking titles. Missing files moved to a new drive can be re-pointed in bulk with undo, the Track Inspector shows which playlists contain a track, and playlists can be compared or exported as plain-text tracklists and CSV."),
        new WhatsNewItem("DJ ecosystem exports (Traktor, VirtualDJ & Serato)", "Shared target-neutral export projection and 24-key converter powering standalone Traktor NML playlists, VirtualDJ .vdjfolder + guarded database.xml merges, and binary Serato .crate writers with multi-drive support."),
        new WhatsNewItem("Cleaner Search & quality settings and smarter matching", "Removed Ranking strategy and Fuzzy normalization toggles, combined File integrity and Upscale detection into one File checks card, added a Custom quality profile chip, fixed non-Latin title matching, and unified the remix/Extended-vs-Radio-Edit version gate across both matchers."),
        new WhatsNewItem("Public Spotify links work without sign-in", "Paste any public Spotify playlist or album link in Import with no Spotify login required. For Liked Songs, private playlists, or playlists over 100 tracks, connect your own free Spotify app in the wizard or Settings → Connection."),
        new WhatsNewItem("Mix playlists with one click", "Every playlist header now has Mix beside Play, a Ready badge (e.g. Ready 42/45) with Prep for Mix, and clear transition labels in the queue and Now Playing."),
        new WhatsNewItem("Focused sidebar & Labs toggle", "Pages are grouped into Acquire, Library & Prep, and Play & Mix (Flow Builder is now Transitions). Sets, Workstation, and Edit Lab live under Settings → Labs — kept on automatically if you already have saved sets."),
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
