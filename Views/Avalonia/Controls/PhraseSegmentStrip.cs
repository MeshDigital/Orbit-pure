using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SLSKDONET.Models;

namespace SLSKDONET.Views.Avalonia.Controls
{
    /// <summary>
    /// Renders a horizontal strip of colored phrase segments (Intro/Build/Drop/Outro)
    /// proportional to their relative duration. Designed for track inspector and deck headers.
    /// </summary>
    public class PhraseSegmentStrip : Control
    {
        public static readonly StyledProperty<IReadOnlyList<PhraseSegment>?> SegmentsProperty =
            AvaloniaProperty.Register<PhraseSegmentStrip, IReadOnlyList<PhraseSegment>?>(nameof(Segments));

        public IReadOnlyList<PhraseSegment>? Segments
        {
            get => GetValue(SegmentsProperty);
            set => SetValue(SegmentsProperty, value);
        }

        static PhraseSegmentStrip()
        {
            AffectsRender<PhraseSegmentStrip>(SegmentsProperty);
            AffectsMeasure<PhraseSegmentStrip>(SegmentsProperty);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
            return new Size(width, 12.0);
        }

        // Brushes are cached per colour so Render never allocates one per segment per frame.
        private static readonly System.Collections.Generic.Dictionary<string, IBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

        private static IBrush BrushFor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out var color)) return Brushes.Gray;
            if (!BrushCache.TryGetValue(hex, out var brush))
                BrushCache[hex] = brush = new SolidColorBrush(color).ToImmutable();
            return brush;
        }

        public override void Render(DrawingContext context)
        {
            var segments = Segments;
            if (segments == null || segments.Count == 0) return;

            double totalDuration = segments.Sum(s => s.Duration);
            if (totalDuration <= 0) return;

            double totalWidth = Bounds.Width;
            double height = Bounds.Height;
            double gap = 1.5;
            double x = 0;

            foreach (var seg in segments)
            {
                double ratio = seg.Duration / totalDuration;
                double segWidth = Math.Max(2, totalWidth * ratio - gap);

                var brush = BrushFor(seg.Color);

                var rect = new Rect(x, 0, segWidth, height);
                context.DrawRectangle(brush, null, rect, 2, 2);

                x += segWidth + gap;
            }
        }
    }
}
