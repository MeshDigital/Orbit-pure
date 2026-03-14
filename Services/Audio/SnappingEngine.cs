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

                // Beats within window
                int firstBeat = (int)Math.Max(0, Math.Floor(windowStart / beatDuration));
                int lastBeat = (int)Math.Ceiling(windowEnd / beatDuration);
                for (int i = firstBeat; i <= lastBeat; i++)
                {
                    candidates.Add(i * beatDuration);
                }

                // Bars within window (already covered by beats, but we might want to prioritize them)
                // for visual feedback we'd distinguish these.
            }

            // 2. Structural Landmarks
            foreach (var landmark in landmarks)
            {
                if (landmark >= windowStart && landmark <= windowEnd)
                    candidates.Add(landmark);
            }

            return candidates.Distinct().OrderBy(c => c).ToList();
        }

        public static float Snap(float currentTime, SnappingMode mode, float bpm, IEnumerable<float> landmarks, float thresholdSeconds = 0.05f)
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
