using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Views.Avalonia.Controls
{
    /// <summary>Axis-aligned box in canvas pixels.</summary>
    internal readonly record struct LabelBox(float Left, float Top, float Right, float Bottom)
    {
        public float Width => Right - Left;
        public float Height => Bottom - Top;
        public float CenterX => (Left + Right) / 2f;
        public float CenterY => (Top + Bottom) / 2f;

        public bool Intersects(LabelBox o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;

        public float OverlapArea(LabelBox o)
        {
            float w = Math.Min(Right, o.Right) - Math.Max(Left, o.Left);
            float h = Math.Min(Bottom, o.Bottom) - Math.Max(Top, o.Top);
            return w > 0 && h > 0 ? w * h : 0f;
        }

        public bool IntersectsCircle(float cx, float cy, float r)
        {
            float nx = Math.Clamp(cx, Left, Right), ny = Math.Clamp(cy, Top, Bottom);
            float dx = cx - nx, dy = cy - ny;
            return dx * dx + dy * dy < r * r;
        }
    }

    /// <summary>One bubble to label: centre, radius, genre name and its count text.</summary>
    internal readonly record struct GalaxyLabelInput(string Key, float X, float Y, float Radius, string Name, string Count);

    /// <summary>Where a bubble's label (and count) goes.</summary>
    /// <param name="Text">"jungle" when the count sits inside the bubble, "jungle · 12" when it does not.</param>
    /// <param name="Leader">True when the label sits away from its bubble and needs a leader line.</param>
    internal sealed record GalaxyLabelPlacement(
        string Key, string Text, LabelBox Box, bool Leader,
        float LeaderFromX, float LeaderFromY, float LeaderToX, float LeaderToY,
        bool CountInside, LabelBox CountBox, string Count, int Candidate);

    /// <summary>
    /// Label collision avoidance for the Dashboard's Genre Galaxy. Bubbles overlap as they orbit,
    /// so drawing every name above its bubble put "drumstep" on top of "uk garage" and the inner
    /// counts on top of each other. Greedy placement, biggest bubble first: the count goes inside
    /// the bubble only when it does not collide with a bigger bubble's count (else it moves into
    /// the label, "jungle · 12"); each label tries above / below / right / left, then the same
    /// further out with a leader line, and takes the first spot that hits no other label or count
    /// (and, if possible, no other bubble). The previous frame's choice is tried first so labels
    /// don't flicker between spots while the planets move.
    /// </summary>
    internal static class GenreGalaxyLabelLayout
    {
        public const float Gap = 4f;
        private static readonly float[] FarSteps = { 16f, 32f, 48f };

        public static List<GalaxyLabelPlacement> Layout(
            IReadOnlyList<GalaxyLabelInput> nodes,
            float width, float height,
            Func<string, float> measureLabel, float labelHeight,
            Func<string, float> measureCount, float countHeight,
            IDictionary<string, int>? previousCandidate = null)
        {
            var order = Enumerable.Range(0, nodes.Count).OrderByDescending(i => nodes[i].Radius).ThenBy(i => i).ToList();

            // 1. Counts inside bubbles, biggest bubble first; a count that would collide moves into the label.
            var countInside = new bool[nodes.Count];
            var countBoxes = new LabelBox[nodes.Count];
            var placedCounts = new List<LabelBox>();
            foreach (var i in order)
            {
                var n = nodes[i];
                float w = measureCount(n.Count);
                var box = new LabelBox(n.X - w / 2f - 2f, n.Y - countHeight / 2f, n.X + w / 2f + 2f, n.Y + countHeight / 2f);
                bool fits = n.Radius >= 10f && w + 4f <= n.Radius * 2f && !placedCounts.Any(b => b.Intersects(box));
                countInside[i] = fits;
                countBoxes[i] = box;
                if (fits) placedCounts.Add(box);
            }

            // 2. Labels.
            var placedLabels = new List<LabelBox>();
            var result = new GalaxyLabelPlacement?[nodes.Count];
            foreach (var i in order)
            {
                var n = nodes[i];
                string text = countInside[i] ? n.Name : $"{n.Name} · {n.Count}";
                float w = measureLabel(text);
                var candidates = Candidates(n, w, labelHeight);

                int? prev = previousCandidate != null && previousCandidate.TryGetValue(n.Key, out var p) && p >= 0 && p < candidates.Count ? p : null;
                int chosen = -1;
                for (int pass = 0; pass < 2 && chosen < 0; pass++)
                {
                    bool avoidBubbles = pass == 0;
                    if (prev is int pv && Fits(candidates[pv], avoidBubbles)) { chosen = pv; break; }
                    for (int c = 0; c < candidates.Count; c++)
                        if (Fits(candidates[c], avoidBubbles)) { chosen = c; break; }
                }
                if (chosen < 0)
                {
                    // Nothing is free: take the spot with the least overlap (kept inside the canvas).
                    chosen = Enumerable.Range(0, candidates.Count)
                        .OrderBy(c => placedLabels.Concat(placedCounts).Sum(b => b.OverlapArea(Clamp(candidates[c], width, height))))
                        .ThenBy(c => c)
                        .First();
                }

                var boxChosen = Clamp(candidates[chosen], width, height);
                placedLabels.Add(boxChosen);
                if (previousCandidate != null) previousCandidate[n.Key] = chosen;

                bool leader = chosen >= 4;
                float toX = Math.Clamp(n.X, boxChosen.Left, boxChosen.Right);
                float toY = Math.Clamp(n.Y, boxChosen.Top, boxChosen.Bottom);
                float dx = toX - n.X, dy = toY - n.Y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                float fromX = len > 0.01f ? n.X + dx / len * n.Radius : n.X;
                float fromY = len > 0.01f ? n.Y + dy / len * n.Radius : n.Y;

                result[i] = new GalaxyLabelPlacement(n.Key, text, boxChosen, leader, fromX, fromY, toX, toY,
                    countInside[i], countBoxes[i], n.Count, chosen);
            }

            return result.Select(r => r!).ToList();

            bool Fits(LabelBox box, bool avoidBubbles)
            {
                if (box.Left < 0 || box.Top < 0 || box.Right > width || box.Bottom > height) return false;
                if (placedLabels.Any(b => b.Intersects(box))) return false;
                if (placedCounts.Any(b => b.Intersects(box))) return false;
                if (avoidBubbles)
                {
                    for (int j = 0; j < nodes.Count; j++)
                        if (box.IntersectsCircle(nodes[j].X, nodes[j].Y, nodes[j].Radius)) return false;
                }
                return true;
            }
        }

        /// <summary>0–3: above, below, right, left (adjacent). 4+: the same, further out (leader line).</summary>
        internal static List<LabelBox> Candidates(GalaxyLabelInput n, float w, float h)
        {
            var list = new List<LabelBox>(4 + FarSteps.Length * 6);
            float r = n.Radius;
            list.Add(Above(0)); list.Add(Below(0));
            list.Add(new LabelBox(n.X + r + Gap + 2, n.Y - h / 2f, n.X + r + Gap + 2 + w, n.Y + h / 2f));
            list.Add(new LabelBox(n.X - r - Gap - 2 - w, n.Y - h / 2f, n.X - r - Gap - 2, n.Y + h / 2f));
            foreach (var d in FarSteps)
            {
                list.Add(Above(d));
                list.Add(Below(d));
                list.Add(new LabelBox(n.X + r * 0.7f + d, n.Y - r * 0.7f - d - h, n.X + r * 0.7f + d + w, n.Y - r * 0.7f - d)); // up-right
                list.Add(new LabelBox(n.X - r * 0.7f - d - w, n.Y - r * 0.7f - d - h, n.X - r * 0.7f - d, n.Y - r * 0.7f - d)); // up-left
                list.Add(new LabelBox(n.X + r * 0.7f + d, n.Y + r * 0.7f + d, n.X + r * 0.7f + d + w, n.Y + r * 0.7f + d + h)); // down-right
                list.Add(new LabelBox(n.X - r * 0.7f - d - w, n.Y + r * 0.7f + d, n.X - r * 0.7f - d, n.Y + r * 0.7f + d + h)); // down-left
            }
            return list;

            LabelBox Above(float d) => new(n.X - w / 2f, n.Y - r - Gap - d - h, n.X + w / 2f, n.Y - r - Gap - d);
            LabelBox Below(float d) => new(n.X - w / 2f, n.Y + r + Gap + d, n.X + w / 2f, n.Y + r + Gap + d + h);
        }

        private static LabelBox Clamp(LabelBox b, float width, float height)
        {
            float dx = b.Left < 0 ? -b.Left : b.Right > width ? width - b.Right : 0f;
            float dy = b.Top < 0 ? -b.Top : b.Bottom > height ? height - b.Bottom : 0f;
            return new LabelBox(b.Left + dx, b.Top + dy, b.Right + dx, b.Bottom + dy);
        }
    }
}
