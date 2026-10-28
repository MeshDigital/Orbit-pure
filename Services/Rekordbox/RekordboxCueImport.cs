using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>
/// Turns a track's cues from Rekordbox's library into ORBIT cues and merges them with what the
/// editor has. Rekordbox's cues are the DJ's own work, so they win: ORBIT's auto cues give way, an
/// ORBIT cue on the same spot is replaced, and one holding a pad Rekordbox uses becomes a memory cue.
/// Colours come from Rekordbox's colour table (<see cref="RekordboxCueColors"/>); a cue without one
/// gets ORBIT's pad colour (hot cue) or the default green (memory cue).
/// </summary>
public static class RekordboxCueImport
{
    private const double SameSpotSeconds = 0.05;
    private const string MemoryColor = "#28E214";

    public static List<OrbitCue> ToOrbitCues(RekordboxDbTrack track) =>
        track.Cues.Select(c =>
        {
            int slot = c.IsLoop ? -1 : c.Slot;
            string label = slot >= 0 ? ((char)('A' + slot)).ToString() : c.IsLoop ? "loop" : "memory";
            return new OrbitCue
            {
                Timestamp = c.InMsec / 1000.0,
                IsLoop = c.IsLoop,
                LoopEndSeconds = c.IsLoop ? c.OutMsec / 1000.0 : 0,
                SlotIndex = slot,
                Name = string.IsNullOrWhiteSpace(c.Comment) ? $"Rekordbox {label}" : c.Comment!.Trim(),
                Color = RekordboxCueColors.HexForIndex(c.ColorTableIndex)
                        ?? (slot >= 0 ? CueEditorSession.CueColorSwatches[slot] : MemoryColor),
                Role = CueRole.Custom,
                Source = CueSource.User,
                Confidence = 1.0,
            };
        }).OrderBy(c => c.Timestamp).ToList();

    public static List<OrbitCue> Merge(IEnumerable<OrbitCue> current, IReadOnlyList<OrbitCue> imported)
    {
        var importedPads = imported.Where(c => !c.IsLoop && c.SlotIndex >= 0).Select(c => c.SlotIndex).ToHashSet();
        var merged = new List<OrbitCue>(imported);
        foreach (var cue in current)
        {
            if (cue.Source == CueSource.Auto && !cue.IsSectionMarker) continue;
            if (!cue.IsSectionMarker && imported.Any(i => i.IsLoop == cue.IsLoop && Math.Abs(i.Timestamp - cue.Timestamp) <= SameSpotSeconds)) continue;
            if (!cue.IsLoop && importedPads.Contains(cue.SlotIndex)) cue.SlotIndex = -1;
            merged.Add(cue);
        }
        return merged.OrderBy(c => c.Timestamp).ToList();
    }
}
