using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public class EnergyGauge : Control
    {
        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<EnergyGauge, double>(nameof(Value), 0.0);

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public static readonly StyledProperty<int> LevelProperty =
            AvaloniaProperty.Register<EnergyGauge, int>(nameof(Level), 5); // 1-10

        public int Level
        {
            get => GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        static EnergyGauge()
        {
            AffectsRender<EnergyGauge>(ValueProperty, LevelProperty);
        }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            var radius = Math.Min(size.Width, size.Height) / 2 - 10;
            var center = new Point(size.Width / 2, size.Height / 2);

            // MIK Palette
            var color = Level switch
            {
                <= 3 => Color.FromRgb(30, 136, 229),  // Deep Blue
                <= 7 => Color.FromRgb(78, 201, 176), // Sea Green
                _ => Color.FromRgb(255, 82, 82)      // Bright Red
            };

            var brush = new SolidColorBrush(color);
            var transparentBrush = new SolidColorBrush(color, 0.2f);
            var pen = new Pen(brush, 4, lineCap: PenLineCap.Round);
            var bgPen = new Pen(new SolidColorBrush(Colors.Gray, 0.2f), 4, lineCap: PenLineCap.Round);

            // Draw Background Arc (180 degrees)
            var bgStart = new Point(center.X - radius, center.Y);
            var bgEnd = new Point(center.X + radius, center.Y);
            
            // Avalonia PathGeometry or direct drawing
            // Simplified: Draw a semi-circle using segments
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(bgStart, false);
                ctx.ArcTo(bgEnd, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            }
            context.DrawGeometry(null, bgPen, geometry);

            // Draw Value Arc
            var angle = (Value / 1.0) * Math.PI; // 0 to 180 degrees
            var endPoint = new Point(
                center.X - radius * Math.Cos(angle),
                center.Y - radius * Math.Sin(angle));

            var valueGeometry = new StreamGeometry();
            using (var ctx = valueGeometry.Open())
            {
                ctx.BeginFigure(bgStart, false);
                ctx.ArcTo(endPoint, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            }
            context.DrawGeometry(null, pen, valueGeometry);

            // Draw Needle
            context.DrawLine(new Pen(Brushes.White, 2), center, endPoint);

            // Center Bolt
            context.DrawEllipse(Brushes.White, null, new Rect(center.X - 4, center.Y - 4, 8, 8));

            // Level Text
            var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Black);
            var formattedText = new FormattedText(
                Level.ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                24,
                brush);
            
            context.DrawText(formattedText, new Point(center.X - formattedText.Width / 2, center.Y - formattedText.Height / 2 - 20));
            
            var labelText = new FormattedText(
                "ENERGY",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                8,
                new SolidColorBrush(Colors.Gray, 0.6f));
            context.DrawText(labelText, new Point(center.X - labelText.Width / 2, center.Y + 5));
        }
    }
}
