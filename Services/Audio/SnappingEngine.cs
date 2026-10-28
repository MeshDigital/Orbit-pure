using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Services.Audio
{
    public enum SnappingMode
    {
        Hard, // Force to grid
        Soft, // Sticky attraction
        Free  // No snapping
    }
    public class SnappingEngine
    {
        public static List<float> GetSnapCandidates(float bpm, IEnumerable<float> landmarks, float windowStart, float windowEnd)
        {
            var candidates = new List<float>();

            // 1. Grid Candidates (Bars/Beats) — beat duration/index math delegates to
            // BeatGridService (the shared beat<->seconds conversion) instead of re-deriving
            // `60/bpm` locally; only the windowed index-range scan (cheap for a live drag handler,
            // unlike generating a grid array from the start of the track) stays local to this method.
            if (bpm > 0)
            {
                const int beatsPerBar = 4;
                const int beatsPerPhrase = beatsPerBar * 16; // 16-bar phrase block

                // Beats within window
                int firstBeat = (int)Math.Max(0, Math.Floor(BeatGridService.SecondsToBeat(windowStart, bpm)));
                int lastBeat = (int)Math.Ceiling(BeatGridService.SecondsToBeat(windowEnd, bpm));
                for (int i = firstBeat; i <= lastBeat; i++)
                {
                    candidates.Add((float)BeatGridService.BeatToSeconds(i, bpm));
                }

                // Strong DJ phrase anchors: full bars and 16-bar blocks.
                int firstBar = (int)Math.Max(0, Math.Floor(BeatGridService.SecondsToBeat(windowStart, bpm) / beatsPerBar));
                int lastBar = (int)Math.Ceiling(BeatGridService.SecondsToBeat(windowEnd, bpm) / beatsPerBar);
                for (int i = firstBar; i <= lastBar; i++)
                    candidates.Add((float)BeatGridService.BeatToSeconds(i * beatsPerBar, bpm));

                int firstPhrase = (int)Math.Max(0, Math.Floor(BeatGridService.SecondsToBeat(windowStart, bpm) / beatsPerPhrase));
                int lastPhrase = (int)Math.Ceiling(BeatGridService.SecondsToBeat(windowEnd, bpm) / beatsPerPhrase);
                for (int i = firstPhrase; i <= lastPhrase; i++)
                    candidates.Add((float)BeatGridService.BeatToSeconds(i * beatsPerPhrase, bpm));
            }

            // 2. Structural Landmarks
            foreach (var landmark in landmarks)
            {
                if (landmark >= windowStart && landmark <= windowEnd)
                    candidates.Add(landmark);
            }

            return candidates.Distinct().OrderBy(c => c).ToList();
        }

        public static float Snap(float currentTime, SnappingMode mode, float bpm, IEnumerable<float> landmarks, float thresholdSeconds = 0.015f)
        {
            if (mode == SnappingMode.Free) return currentTime;

            // Define a search window around current time for efficiency and candidates
            float window = 1.0f; // 1 second window
            var candidates = GetSnapCandidates(bpm, landmarks, currentTime - window, currentTime + window);

            if (!candidates.Any()) return currentTime;

            float bestLandmark = currentTime;
            float minDistance = float.MaxValue;

            foreach (var candidate in candidates)
            {
                float dist = Math.Abs(candidate - currentTime);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    bestLandmark = candidate;
                }
            }

            if (mode == SnappingMode.Hard) return bestLandmark;

            // Soft mode logic
            if (minDistance < thresholdSeconds)
                return bestLandmark;

            return currentTime;
        }

        private static void Check(float landmark, ref float best, ref float minDistance, float target)
        {
            float dist = Math.Abs(landmark - target);
            if (dist < minDistance)
            {
                minDistance = dist;
                best = landmark;
            }
        }
    }
}
