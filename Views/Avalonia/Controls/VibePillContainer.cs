using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SLSKDONET.ViewModels; // For VibePill record

namespace SLSKDONET.Views.Avalonia.Controls
{
    /// <summary>
    /// A lightweight control that renders VibePills directly to the drawing context
    /// to avoid the heavy overhead of ItemsControl/ItemContainerGenerator in virtualized lists.
    /// </summary>
    public class VibePillContainer : Control
    {
        // Cache typefaces to avoid lookups on every render
        private static readonly Typeface _typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
        private static readonly double _fontSize = 10.0;
        
        public static readonly StyledProperty<IEnumerable<VibePill>> ItemsProperty =
            AvaloniaProperty.Register<VibePillContainer, IEnumerable<VibePill>>(nameof(Items));

        public IEnumerable<VibePill> Items
        {
            get => GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        static VibePillContainer()
        {
            AffectsRender<VibePillContainer>(ItemsProperty);
            AffectsMeasure<VibePillContainer>(ItemsProperty);
        }

        private struct CachedPill
        {
            public FormattedText Text;
            public IBrush Background;
            public double Width;
        }

        private List<CachedPill>? _cachedPills;
        private double _cachedWidth;

        private void UpdateCache()
        {
            var items = Items;
            if (items == null)
            {
                _cachedPills = null;
                _cachedWidth = 0;
                return;
            }

            _cachedPills = new List<CachedPill>();
            double totalWidth = 0;
            double spacing = 4.0;

            foreach (var item in items)
            {
                var text = $"{item.Icon} {item.Label}";
                var formattedText = new FormattedText(
                    text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    _fontSize,
                    Brushes.White
                );

                double pillWidth = formattedText.Width + 16;
                _cachedPills.Add(new CachedPill
                {
                    Text = formattedText,
                    Background = item.Color ?? Brushes.Gray,
                    Width = pillWidth
                });

                totalWidth += pillWidth + spacing;
            }

            if (totalWidth > 0) totalWidth -= spacing;
            _cachedWidth = totalWidth;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            UpdateCache();
            return new Size(_cachedWidth, 18.0);
        }

        public override void Render(DrawingContext context)
        {
            if (_cachedPills == null) return;

            double x = 0;
            double spacing = 4.0;
            double pillHeight = 18.0;
            double cornerRadius = 9.0;

            foreach (var pill in _cachedPills)
            {
                var rect = new Rect(x, 0, pill.Width, pillHeight);
                context.DrawRectangle(pill.Background, null, rect, cornerRadius, cornerRadius);

                double textY = (pillHeight - pill.Text.Height) / 2;
                context.DrawText(pill.Text, new Point(x + 8, textY));

                x += pill.Width + spacing;
            }
        }
    }
}
