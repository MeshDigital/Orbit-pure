using System;
using System.Collections.Generic;
using System.Linq;

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

            // 1. Grid Candidates (Bars/Beats)
            if (bpm > 0)
            {
                float beatDuration = 60f / bpm;
                float barDuration = beatDuration * 4;
                float sixteenBarDuration = barDuration * 16;

                // Beats within window
                int firstBeat = (int)Math.Max(0, Math.Floor(windowStart / beatDuration));
                int lastBeat = (int)Math.Ceiling(windowEnd / beatDuration);
                for (int i = firstBeat; i <= lastBeat; i++)
                {
                    candidates.Add(i * beatDuration);
                }

                // Strong DJ phrase anchors: full bars and 16-bar blocks.
                int firstBar = (int)Math.Max(0, Math.Floor(windowStart / barDuration));
                int lastBar = (int)Math.Ceiling(windowEnd / barDuration);
                for (int i = firstBar; i <= lastBar; i++)
                    candidates.Add(i * barDuration);

                int firstPhrase = (int)Math.Max(0, Math.Floor(windowStart / sixteenBarDuration));
                int lastPhrase = (int)Math.Ceiling(windowEnd / sixteenBarDuration);
                for (int i = firstPhrase; i <= lastPhrase; i++)
                    candidates.Add(i * sixteenBarDuration);
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
