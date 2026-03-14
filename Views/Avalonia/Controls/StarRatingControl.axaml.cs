using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public partial class StarRatingControl : UserControl
    {
        public static readonly StyledProperty<int> RatingProperty =
            AvaloniaProperty.Register<StarRatingControl, int>(nameof(Rating), defaultValue: 0);

        public static readonly StyledProperty<double> StarSizeProperty =
            AvaloniaProperty.Register<StarRatingControl, double>(nameof(StarSize), defaultValue: 20.0);

        public static readonly StyledProperty<IBrush> StarColorProperty =
            AvaloniaProperty.Register<StarRatingControl, IBrush>(nameof(StarColor), defaultValue: Brushes.Gold);

        public static readonly StyledProperty<bool> IsReadOnlyProperty =
            AvaloniaProperty.Register<StarRatingControl, bool>(nameof(IsReadOnly), defaultValue: false);

        public int Rating
        {
            get => GetValue(RatingProperty);
            set => SetValue(RatingProperty, value);
        }

        public double StarSize
        {
            get => GetValue(StarSizeProperty);
            set => SetValue(StarSizeProperty, value);
        }

        public IBrush StarColor
        {
            get => GetValue(StarColorProperty);
            set => SetValue(StarColorProperty, value);
        }

        public bool IsReadOnly
        {
            get => GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        public event EventHandler<int>? RatingChanged;

        public StarRatingControl()
        {
            InitializeComponent();
            
            // Subscribe to property changes
            RatingProperty.Changed.AddClassHandler<StarRatingControl>((control, e) =>
            {
                control.UpdateStars();
            });
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            UpdateStars();
        }

        private void OnStarClick(object? sender, RoutedEventArgs e)
        {
            if (IsReadOnly) return;

            if (sender is Button button && button.Tag is string tagStr && int.TryParse(tagStr, out int starValue))
            {
                // Toggle: if clicking the current rating, set to 0 (unrate)
                Rating = (Rating == starValue) ? 0 : starValue;
                RatingChanged?.Invoke(this, Rating);
            }
        }

        private void UpdateStars()
        {
            var stars = new[]
            {
                this.FindControl<TextBlock>("Star1Text"),
                this.FindControl<TextBlock>("Star2Text"),
                this.FindControl<TextBlock>("Star3Text"),
                this.FindControl<TextBlock>("Star4Text"),
                this.FindControl<TextBlock>("Star5Text")
            };

            for (int i = 0; i < stars.Length; i++)
            {
                if (stars[i] != null)
                {
                    stars[i]!.Text = (i < Rating) ? "★" : "☆";
                }
            }
        }
    }
}
