using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public class CamelotWheelControl : Control
    {
        public static readonly StyledProperty<string?> SelectedKeyProperty =
            AvaloniaProperty.Register<CamelotWheelControl, string?>(nameof(SelectedKey));

        public static readonly StyledProperty<ICommand?> KeyClickedCommandProperty =
            AvaloniaProperty.Register<CamelotWheelControl, ICommand?>(nameof(KeyClickedCommand));

        public string? SelectedKey
        {
            get => GetValue(SelectedKeyProperty);
            set => SetValue(SelectedKeyProperty, value);
        }

        public ICommand? KeyClickedCommand
        {
            get => GetValue(KeyClickedCommandProperty);
            set => SetValue(KeyClickedCommandProperty, value);
        }

        static CamelotWheelControl()
        {
            AffectsRender<CamelotWheelControl>(SelectedKeyProperty);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            var cmd = KeyClickedCommand;
            if (cmd == null) return;

            var pos = e.GetPosition(this);
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 0 || h <= 0) return;

            double cx = w / 2;
            double cy = h / 2;
            double unit = Math.Min(w, h) / 2 - 1;

            double dx = pos.X - cx;
            double dy = pos.Y - cy;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            double bOuterR = unit;
            double bInnerR = unit * 0.725;
            double aOuterR = unit * 0.685;
            double aInnerR = unit * 0.43;

            bool inAring = dist >= aInnerR && dist <= aOuterR;
            bool inBring = dist >= bInnerR && dist <= bOuterR;
            if (!inAring && !inBring) return;

            // Angle: 0 = right, clockwise. Position 1 is at top (-90°).
            double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            double adjusted = angleDeg + 90.0;
            if (adjusted < 0) adjusted += 360.0;
            if (adjusted >= 360.0) adjusted -= 360.0;

            int n = (int)Math.Floor(adjusted / 30.0) + 1;
            if (n < 1 || n > 12) return;

            string key = inAring ? $"{n}A" : $"{n}B";
            if (cmd.CanExecute(key))
                cmd.Execute(key);

            e.Handled = true;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double size = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
            if (!double.IsInfinity(availableSize.Height))
                size = Math.Min(size, availableSize.Height);
            return new Size(size, size);
        }

        // One color per Camelot position 1–12 matching the GetHarmonicColor palette
        private static readonly Color[] PositionColors =
        {
            Color.Parse("#008080"), // 1  Teal
            Color.Parse("#4682B4"), // 2  SteelBlue
            Color.Parse("#4169E1"), // 3  RoyalBlue
            Color.Parse("#6A0DAD"), // 4  Purple
            Color.Parse("#9400D3"), // 5  DarkViolet
            Color.Parse("#C71585"), // 6  MediumVioletRed
            Color.Parse("#DC143C"), // 7  Crimson
            Color.Parse("#FF8C00"), // 8  DarkOrange
            Color.Parse("#DAA520"), // 9  Goldenrod
            Color.Parse("#6B8E23"), // 10 OliveDrab
            Color.Parse("#3CB371"), // 11 MediumSeaGreen
            Color.Parse("#008B8B"), // 12 DarkCyan
        };

        private static bool ParseCamelot(string? key, out int number, out bool isMinor)
        {
            number = 0;
            isMinor = false;
            if (string.IsNullOrWhiteSpace(key)) return false;
            var k = key.Trim().ToUpperInvariant();
            if (k.Length < 2) return false;
            char ring = k[k.Length - 1];
            if (ring != 'A' && ring != 'B') return false;
            if (!int.TryParse(k.Substring(0, k.Length - 1), out int n)) return false;
            if (n < 1 || n > 12) return false;
            number = n;
            isMinor = ring == 'A';
            return true;
        }

        // Compatible: adjacent ±1 on same ring, or same number opposite ring (relative)
        private static bool IsCompatible(int n, bool isMinor, int selN, bool selIsMinor)
        {
            if (selN == 0) return false;
            if (isMinor == selIsMinor)
            {
                int prev = selN == 1 ? 12 : selN - 1;
                int next = selN == 12 ? 1 : selN + 1;
                if (n == prev || n == next) return true;
            }
            if (n == selN && isMinor != selIsMinor) return true;
            return false;
        }

        public override void Render(DrawingContext context)
        {
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 0 || h <= 0) return;

            double cx = w / 2;
            double cy = h / 2;
            double unit = Math.Min(w, h) / 2 - 1;

            double bOuterR = unit;           // outer edge of B (major) ring
            double bInnerR = unit * 0.725;   // inner edge of B ring
            double aOuterR = unit * 0.685;   // outer edge of A (minor) ring (small gap to B)
            double aInnerR = unit * 0.43;    // inner edge of A ring

            // Dark center circle
            context.DrawEllipse(
                new SolidColorBrush(Color.Parse("#1A1B1C")),
                null,
                new Point(cx, cy),
                aInnerR - 1,
                aInnerR - 1);

            ParseCamelot(SelectedKey, out int selN, out bool selIsMinor);
            bool hasSelection = selN != 0;

            const double gapDeg = 2.5;

            for (int n = 1; n <= 12; n++)
            {
                double midDeg = (n - 1) * 30.0 - 90.0; // 1 at top, clockwise
                double startDeg = midDeg - 15.0 + gapDeg / 2.0;
                double endDeg   = midDeg + 15.0 - gapDeg / 2.0;

                Color col = PositionColors[n - 1];

                bool isSelectedA  = selN == n && selIsMinor;
                bool isCompatA    = IsCompatible(n, true,  selN, selIsMinor);
                bool isSelectedB  = selN == n && !selIsMinor;
                bool isCompatB    = IsCompatible(n, false, selN, selIsMinor);

                // Inner A (minor) ring segment
                var brushA = MakeBrush(col, isSelectedA, isCompatA, hasSelection);
                DrawRingSegment(context, cx, cy, aInnerR, aOuterR, startDeg, endDeg, brushA);

                // Outer B (major) ring segment — slightly more transparent base
                var brushB = MakeBrush(col, isSelectedB, isCompatB, hasSelection, majorRing: true);
                DrawRingSegment(context, cx, cy, bInnerR, bOuterR, startDeg, endDeg, brushB);

                // Labels
                double midRad = midDeg * Math.PI / 180.0;
                double fs = Math.Max(7, unit * 0.115);

                DrawSectorLabel(context,
                    cx + (aInnerR + aOuterR) / 2.0 * Math.Cos(midRad),
                    cy + (aInnerR + aOuterR) / 2.0 * Math.Sin(midRad),
                    $"{n}A", fs, isSelectedA, isCompatA, hasSelection);

                DrawSectorLabel(context,
                    cx + (bInnerR + bOuterR) / 2.0 * Math.Cos(midRad),
                    cy + (bInnerR + bOuterR) / 2.0 * Math.Sin(midRad),
                    $"{n}B", fs, isSelectedB, isCompatB, hasSelection);
            }

            // Center label — shows selected key text
            if (hasSelection)
            {
                string label = SelectedKey?.ToUpperInvariant() ?? string.Empty;
                double fs = Math.Max(10, unit * 0.18);
                var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
                Color col = selN >= 1 && selN <= 12 ? PositionColors[selN - 1] : Colors.White;
                var ft = new FormattedText(
                    label,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fs,
                    new SolidColorBrush(col));
                context.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
            }
        }

        private static IBrush MakeBrush(Color col, bool isSelected, bool isCompat, bool hasSelection, bool majorRing = false)
        {
            byte baseAlpha = majorRing ? (byte)85 : (byte)100;
            if (isSelected) return new SolidColorBrush(Color.FromArgb(255, col.R, col.G, col.B));
            if (isCompat)   return new SolidColorBrush(Color.FromArgb(170, col.R, col.G, col.B));
            if (hasSelection) return new SolidColorBrush(Color.FromArgb(28, col.R, col.G, col.B));
            return new SolidColorBrush(Color.FromArgb(baseAlpha, col.R, col.G, col.B));
        }

        private static void DrawSectorLabel(DrawingContext ctx, double x, double y, string text, double fontSize,
            bool isSelected, bool isCompat, bool hasSelection)
        {
            byte alpha = isSelected ? (byte)255 : isCompat ? (byte)210 : hasSelection ? (byte)45 : (byte)150;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, isSelected ? FontWeight.Bold : FontWeight.Normal),
                fontSize,
                brush);
            ctx.DrawText(ft, new Point(x - ft.Width / 2, y - ft.Height / 2));
        }

        private static void DrawRingSegment(DrawingContext ctx, double cx, double cy,
            double r1, double r2, double startDeg, double endDeg, IBrush fill)
        {
            double startRad = startDeg * Math.PI / 180.0;
            double endRad   = endDeg   * Math.PI / 180.0;

            var p1 = new Point(cx + r1 * Math.Cos(startRad), cy + r1 * Math.Sin(startRad)); // inner start
            var p2 = new Point(cx + r2 * Math.Cos(startRad), cy + r2 * Math.Sin(startRad)); // outer start
            var p3 = new Point(cx + r2 * Math.Cos(endRad),   cy + r2 * Math.Sin(endRad));   // outer end
            var p4 = new Point(cx + r1 * Math.Cos(endRad),   cy + r1 * Math.Sin(endRad));   // inner end

            bool isLargeArc = (endDeg - startDeg) > 180.0;

            var geo = new StreamGeometry();
            using (var sgc = geo.Open())
            {
                sgc.BeginFigure(p1, isFilled: true);
                sgc.LineTo(p2);
                sgc.ArcTo(p3, new Size(r2, r2), 0, isLargeArc, SweepDirection.Clockwise);
                sgc.LineTo(p4);
                sgc.ArcTo(p1, new Size(r1, r1), 0, isLargeArc, SweepDirection.CounterClockwise);
                sgc.EndFigure(true);
            }

            ctx.DrawGeometry(fill, null, geo);
        }
    }
}
