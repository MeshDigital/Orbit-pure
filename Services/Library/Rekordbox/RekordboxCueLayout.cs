using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Library.Rekordbox;

/// <summary>
/// One Rekordbox cue as ORBIT hands it over: a point cue or a loop, on a hot cue pad
/// (<see cref="Num"/> 0–7 = A–H) or a memory cue (-1). Times in seconds, colour "#RRGGBB".
/// </summary>
public sealed record RekordboxMark(string Name, bool IsLoop, double StartSeconds, double EndSeconds, int Num, string ColorHex);

/// <summary>
/// How ORBIT's cues become Rekordbox cues — shared by the XML export (POSITION_MARK) and the
/// opt-in direct database write (djmdCue), so the two can never place a cue differently.
/// </summary>
public static class RekordboxCueLayout
{
    /// <summary>
    /// The track's cues from ORBIT's cue table plus any legacy user cues stored as JSON on the
    /// playlist entry (a JSON cue within 50 ms of a table cue is the same cue and is skipped).
    /// A malformed JSON blob is logged and ignored; the table cues are unaffected.
    /// </summary>
    public static List<OrbitCue> MergeCueSources(IEnumerable<CuePointEntity> dbCues, string? cuePointsJson, string trackName, ILogger logger)
    {
        var result = new List<OrbitCue>();

        foreach (var c in dbCues.OrderBy(c => c.TimestampInSeconds))
        {
            result.Add(new OrbitCue
            {
                Timestamp      = c.TimestampInSeconds,
                Name           = c.Label,
                Color          = c.Color,
                Source         = CueSource.Auto,
                IsLoop         = c.IsLoop,
                LoopEndSeconds = c.LoopEndSeconds,
                SlotIndex      = c.SlotIndex,
                // Only the section markers carry their role into the export; other types keep the
                // previous behaviour.
                Role           = c.Type is CuePointType.IntroEnd or CuePointType.OutroStart ? OrbitCue.RoleFromType(c.Type) : CueRole.Custom,
            });
        }

        if (!string.IsNullOrWhiteSpace(cuePointsJson))
        {
            try
            {
                var userCues = JsonSerializer.Deserialize<List<OrbitCue>>(cuePointsJson);
                if (userCues != null)
                {
                    foreach (var uc in userCues.OrderBy(u => u.Timestamp))
                    {
                        bool duplicate = result.Any(e => Math.Abs(e.Timestamp - uc.Timestamp) < 0.05);
                        if (!duplicate) result.Add(uc);
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Rekordbox export: malformed CuePointsJson for '{Track}' — user-placed cues for this track were dropped (auto-generated cues are unaffected).", trackName);
            }
        }

        return result.OrderBy(c => c.Timestamp).ToList();
    }

    /// <summary>
    /// The Rekordbox cues for a track, in export order:
    ///   1. section markers (Intro end / Outro) as named memory cues;
    ///   2. countdown cues ([IN -16], [IN2 -8], …) as memory cues so hot-cue pads stay clean for
    ///      jump points (Intro, Drop 1, Drop 2, Out);
    ///   3. point cues — on their own pad when SlotIndex is 0–7, else the next free pad, else a
    ///      memory cue once all eight pads are taken (nothing is ever dropped). When
    ///      <paramref name="duplicateHotCuesAsMemory"/> is true (opt-in), pad cues also get a
    ///      memory-cue copy;
    ///   4. "16 bars to …" memory cues before each Drop (when there's a BPM and room);
    ///   5. loops — a hot loop on an explicit pad, else a memory loop.
    /// </summary>
    public static List<RekordboxMark> Build(IReadOnlyList<OrbitCue> allCues, double bpm, bool duplicateHotCuesAsMemory = false)
    {
        var marks = new List<RekordboxMark>();
        var pointCues = allCues.Where(c => !c.IsLoop).ToList();
        var loopCues  = allCues.Where(c => c.IsLoop).ToList();

        static bool IsMemoryStripCue(OrbitCue c) =>
            Engine.Cueing.DropCountdownCues.CountdownBars(c.Name) != null
            || Engine.Cueing.DropCountdownCues.PostDropBars(c.Name) != null;

        // Reserve every pad explicitly claimed by a loop OR a non-countdown point cue first, so
        // auto-assignment can never clobber a pad another cue/loop deliberately set via SlotIndex.
        var reservedPads = new HashSet<int>(allCues
            .Where(c => (c.IsLoop || !IsMemoryStripCue(c)) && c.SlotIndex is >= 0 and <= 7)
            .Select(c => c.SlotIndex));
        int nextFreePad = 0;
        int NextAvailablePad()
        {
            while (nextFreePad < 8 && reservedPads.Contains(nextFreePad))
                nextFreePad++;
            return nextFreePad < 8 ? nextFreePad++ : -1;
        }

        // Section markers are memory cues with a fixed name, so they show on the CDJ's memory strip
        // the same way everywhere. One that was also put on a pad keeps that pad.
        foreach (var marker in pointCues.Where(c => c.IsSectionMarker))
            marks.Add(new RekordboxMark(Engine.Cueing.SectionMarkers.ExportName(marker.Role)!, false, marker.Timestamp, 0, -1, marker.Color));
        pointCues = pointCues.Where(c => !c.IsSectionMarker || c.SlotIndex is >= 0 and <= 7).ToList();

        foreach (var cue in pointCues)
        {
            // Countdowns ([IN -16], [IN2 -8], ...) and post-drop phrase cues ([D1 +16], ...) belong
            // on the memory strip so hot-cue pads stay clean for jump points (Intro, Drop 1, Drop 2, Out).
            if (IsMemoryStripCue(cue))
            {
                marks.Add(new RekordboxMark(cue.Name, false, cue.Timestamp, 0, -1, cue.Color));
                continue;
            }

            int num = cue.SlotIndex is >= 0 and <= 7 ? cue.SlotIndex : NextAvailablePad();
            marks.Add(new RekordboxMark(cue.Name, false, cue.Timestamp, 0, num, cue.Color));
            if (duplicateHotCuesAsMemory && num >= 0 && !cue.IsSectionMarker) // opt-in memory copy
                marks.Add(new RekordboxMark(cue.Name, false, cue.Timestamp, 0, -1, cue.Color));
        }

        // Phrase-countdown memory cues: 16 bars before each Drop, so Rekordbox/CDJs show a "bars
        // remaining" countdown into the drop without using a pad. Skipped if it would land before the
        // track start or within 2 bars of another cue (short edits, early DnB drops).
        if (bpm > 0)
        {
            double barSeconds = 60.0 / bpm * 4;
            foreach (var cue in pointCues.Where(c => c.Role == CueRole.Drop))
            {
                double countdownAt = cue.Timestamp - barSeconds * 16;
                if (countdownAt < 0) continue;
                if (allCues.Any(c => Math.Abs(c.Timestamp - countdownAt) < barSeconds * 2)) continue;
                marks.Add(new RekordboxMark($"16 bars to {cue.Name}", false, countdownAt, 0, -1, cue.Color));
            }
        }

        // Loops honour an explicit SlotIndex (0-7) as a hot loop; otherwise a memory loop.
        foreach (var loop in loopCues)
            marks.Add(new RekordboxMark(loop.Name, true, loop.Timestamp, loop.LoopEndSeconds,
                loop.SlotIndex is >= 0 and <= 7 ? loop.SlotIndex : -1, loop.Color));

        return marks;
    }
}
