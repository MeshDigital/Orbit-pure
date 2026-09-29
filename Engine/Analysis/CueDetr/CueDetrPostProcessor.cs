using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Analysis.CueDetr;

/// <summary>A cue point CUE-DETR found: spectrogram frame, time, and the peak's min-max-scaled
/// detection score (≥ the sensitivity, 1.0 = the most confident detection in the track).</summary>
public readonly record struct CueDetrPoint(int Frame, double Seconds, double Score);

/// <summary>
/// Output side of CUE-DETR, as the reference predict.py does it: every query box of every window
/// becomes a candidate at its horizontal centre (window-relative → track frames), scored with the
/// softmax probability of the "cue" class. All candidates are sorted by position, their scores
/// min-max scaled over the track, and scipy.signal.find_peaks(height=sensitivity, distance=radius)
/// picks the cues. Note the distance is counted in candidates (list positions), not frames — that
/// is what the model was tuned with, so it is kept.
/// </summary>
public static class CueDetrPostProcessor
{
    public const double DefaultSensitivity = 0.9;
    public const int DefaultRadius = 16;

    /// <param name="logits">[windows, queries, classes + 1] (last class = "no object").</param>
    /// <param name="boxes">[windows, queries, 4] as (cx, cy, w, h), normalised to the window.</param>
    public static List<CueDetrPoint> Process(
        ReadOnlySpan<float> logits, ReadOnlySpan<float> boxes, IReadOnlyList<int> borders,
        int queries, int classesWithNoObject,
        double sensitivity = DefaultSensitivity, int radius = DefaultRadius)
    {
        int n = borders.Count * queries;
        var candidates = new (long Position, double Score)[n];
        var exps = new float[classesWithNoObject];
        for (int w = 0; w < borders.Count; w++)
        {
            for (int q = 0; q < queries; q++)
            {
                int i = w * queries + q;
                // softmax over classes (float32, like torch), best real class
                var row = logits.Slice(i * classesWithNoObject, classesWithNoObject);
                float max = float.NegativeInfinity;
                foreach (var v in row) if (v > max) max = v;
                float sum = 0;
                for (int c = 0; c < row.Length; c++) { exps[c] = MathF.Exp(row[c] - max); sum += exps[c]; }
                float best = 0;
                for (int c = 0; c < row.Length - 1; c++) best = Math.Max(best, exps[c] / sum);

                // centre_to_corners, × 355, (x1 + x2) // 2 + left  (float32 floor division)
                float cx = boxes[i * 4], bw = boxes[i * 4 + 2];
                float x1 = (cx - 0.5f * bw) * CueDetrFrontEnd.WindowWidth;
                float x2 = (cx + 0.5f * bw) * CueDetrFrontEnd.WindowWidth;
                float centre = MathF.Floor((x1 + x2) / 2f) + borders[w];
                candidates[i] = ((long)centre, best);
            }
        }
        if (n == 0) return new List<CueDetrPoint>();

        double lo = candidates.Min(c => c.Score), hi = candidates.Max(c => c.Score);
        double span = hi - lo;
        for (int i = 0; i < n; i++)
            candidates[i].Score = span > 0 ? (candidates[i].Score - lo) / span : 0;
        // Python sorts the (position, score) tuples: by position, then score.
        Array.Sort(candidates, (a, b) => a.Position != b.Position ? a.Position.CompareTo(b.Position) : a.Score.CompareTo(b.Score));

        var scores = candidates.Select(c => c.Score).ToArray();
        var result = new List<CueDetrPoint>();
        foreach (int p in FindPeaks(scores, sensitivity, radius))
        {
            int frame = (int)candidates[p].Position;
            result.Add(new CueDetrPoint(frame, CueDetrFrontEnd.FramesToSeconds(frame), scores[p]));
        }
        return result;
    }

    /// <summary>scipy.signal.find_peaks(x, height=minHeight, distance=distance): local maxima
    /// (plateaus report their middle sample), filtered by height, then thinned so no two are
    /// closer than <paramref name="distance"/> samples, higher peaks winning.</summary>
    public static List<int> FindPeaks(IReadOnlyList<double> x, double minHeight, int distance)
    {
        // _local_maxima_1d
        var peaks = new List<int>();
        int i = 1, last = x.Count - 1;
        while (i < last)
        {
            if (x[i - 1] < x[i])
            {
                int ahead = i + 1;
                while (ahead < last && x[ahead] == x[i]) ahead++;
                if (x[ahead] < x[i])
                {
                    peaks.Add((i + ahead - 1) / 2);
                    i = ahead;
                }
            }
            i++;
        }

        peaks.RemoveAll(p => x[p] < minHeight);
        if (distance <= 1 || peaks.Count < 2) return peaks;

        // _select_by_peak_distance: visit peaks from highest; each kept peak removes its neighbours.
        var keep = Enumerable.Repeat(true, peaks.Count).ToArray();
        // np.argsort (quicksort) order for equal heights isn't defined; stable order is used here.
        var order = Enumerable.Range(0, peaks.Count).OrderBy(k => x[peaks[k]]).ToArray();
        for (int o = order.Length - 1; o >= 0; o--)
        {
            int j = order[o];
            if (!keep[j]) continue;
            for (int k = j - 1; k >= 0 && peaks[j] - peaks[k] < distance; k--) keep[k] = false;
            for (int k = j + 1; k < peaks.Count && peaks[k] - peaks[j] < distance; k++) keep[k] = false;
        }
        return peaks.Where((_, k) => keep[k]).ToList();
    }
}
