using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

using SLSKDONET.Models;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public enum VisualizerMode { Mini, Full }
    // VisualizerStyle moved to Models namespace

    public class VibeVisualizer : Control
    {
        public static readonly StyledProperty<float> VuLeftProperty =
            AvaloniaProperty.Register<VibeVisualizer, float>(nameof(VuLeft), 0f);

        public float VuLeft { get => GetValue(VuLeftProperty); set => SetValue(VuLeftProperty, value); }

        public static readonly StyledProperty<float> VuRightProperty =
            AvaloniaProperty.Register<VibeVisualizer, float>(nameof(VuRight), 0f);

        public float VuRight { get => GetValue(VuRightProperty); set => SetValue(VuRightProperty, value); }

        public static readonly StyledProperty<float[]?> SpectrumDataProperty =
            AvaloniaProperty.Register<VibeVisualizer, float[]?>(nameof(SpectrumData));

        public float[]? SpectrumData { get => GetValue(SpectrumDataProperty); set => SetValue(SpectrumDataProperty, value); }

        public static readonly StyledProperty<double> EnergyProperty =
            AvaloniaProperty.Register<VibeVisualizer, double>(nameof(Energy), 0.5);

        public double Energy { get => GetValue(EnergyProperty); set => SetValue(EnergyProperty, value); }

        public static readonly StyledProperty<string?> MoodTagProperty =
            AvaloniaProperty.Register<VibeVisualizer, string?>(nameof(MoodTag));

        public string? MoodTag { get => GetValue(MoodTagProperty); set => SetValue(MoodTagProperty, value); }

        public static readonly StyledProperty<bool> IsPlayingProperty =
            AvaloniaProperty.Register<VibeVisualizer, bool>(nameof(IsPlaying), false);

        public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

        public static readonly StyledProperty<VisualizerMode> ModeProperty =
            AvaloniaProperty.Register<VibeVisualizer, VisualizerMode>(nameof(Mode), VisualizerMode.Mini);

        public VisualizerMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

        public static readonly StyledProperty<VisualizerStyle> VisualStyleProperty =
            AvaloniaProperty.Register<VibeVisualizer, VisualizerStyle>(nameof(VisualStyle), VisualizerStyle.Glow);

        public VisualizerStyle VisualStyle { get => GetValue(VisualStyleProperty); set => SetValue(VisualStyleProperty, value); }
        
        public static readonly StyledProperty<double> VisualIntensityProperty =
            AvaloniaProperty.Register<VibeVisualizer, double>(nameof(VisualIntensity), 1.0);

        public double VisualIntensity { get => GetValue(VisualIntensityProperty); set => SetValue(VisualIntensityProperty, value); }

        public static readonly StyledProperty<bool> TriggerResetProperty =
            AvaloniaProperty.Register<VibeVisualizer, bool>(nameof(TriggerReset), false);

        public bool TriggerReset { get => GetValue(TriggerResetProperty); set => SetValue(TriggerResetProperty, value); }

        private readonly Random _random = new();
        private readonly List<Particle> _particles = new();
        private float[] _smoothedSpectrum = Array.Empty<float>();
        private float _heartbeatValue = 0f;
        private readonly DispatcherTimer _renderTimer;
        private long _frameCount = 0;
        private DateTime _lastDataTime = DateTime.Now;
        private DateTime _lastVuTime = DateTime.Now;
        private readonly List<float[]> _spectrumHistory = new();
        private const int MaxHistory = 30;

        static VibeVisualizer()
        {
            // Only affect render on properties that don't change 50 times a second
            // Vu and Spectrum are handled by the DispatcherTimer loop
            AffectsRender<VibeVisualizer>(EnergyProperty, IsPlayingProperty, MoodTagProperty, ModeProperty, VisualStyleProperty, VisualIntensityProperty);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SpectrumDataProperty)
            {
                _lastDataTime = DateTime.Now;
            }
            else if (change.Property == VuLeftProperty || change.Property == VuRightProperty)
            {
                _lastVuTime = DateTime.Now;
            }
            else if (change.Property == TriggerResetProperty)
            {
                if (change.GetNewValue<bool>())
                {
                    Reset();
                    // Auto-reset the flag
                    Dispatcher.UIThread.Post(() => TriggerReset = false);
                }
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _renderTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _renderTimer.Stop();
        }

        public void Reset()
        {
            _smoothedSpectrum = new float[64];
            _heartbeatValue = 0f;
            _particles.Clear();
            int limit = Mode == VisualizerMode.Full ? 50 : 20;
            for (int i = 0; i < limit; i++) _particles.Add(CreateParticle());
        }

        public VibeVisualizer()
        {
            for (int i = 0; i < 50; i++) _particles.Add(CreateParticle());
            
            // Throttle render loop to ~30 FPS instead of running at max speed
            _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _renderTimer.Tick += (_, _) => 
            {
                // Always invalidate to allow for ambient animations (breathing/floating)
                InvalidateVisual();
            };
            _renderTimer.Start();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // Fill all available space
            return availableSize;
        }

        private Particle CreateParticle() => new()
        {
            X = _random.NextDouble(),
            Y = _random.NextDouble(),
            Size = _random.NextDouble() * 3 + 1,
            SpeedX = (_random.NextDouble() - 0.5) * 0.005,
            SpeedY = (_random.NextDouble() - 0.5) * 0.005,
            Opacity = _random.NextDouble() * 0.4 + 0.1
        };

        public override void Render(DrawingContext context)
        {
            try
            {
                RenderInternal(context);
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[VibeVisualizer] Render Exception: {ex.Message}");
#endif
                _ = ex;
            }
        }

        private void RenderInternal(DrawingContext context)
        {
            _frameCount++;
            // Only log randomly once in a while to avoid smashing the FPS
            /*
#if DEBUG
            if (_random.NextDouble() > 0.98)
                System.Diagnostics.Debug.WriteLine($"[VibeVisualizer] Render called - IsPlaying:{IsPlaying}, Bounds:{Bounds.Width}x{Bounds.Height}, SpectrumData:{SpectrumData?.Length ?? 0}, Style:{VisualStyle}");
#endif
            */
            if (!IsPlaying)
            {
                // Render "paused" state
                RenderPausedState(context);
                return;
            }

            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w < 10 || h < 10)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[VibeVisualizer] Bounds too small: {w}x{h}");
#endif
                return;
            }

            var baseColor = GetVibeColor();
            var intensity = ((VuLeft + VuRight) / 2.0f) * (float)VisualIntensity;
            
            // 1. CRT Background (Darker base)
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(10, 10, 15)), null, new Rect(0, 0, w, h));

            var baseBrush = new SolidColorBrush(baseColor);

            // Style Logic
            switch (VisualStyle)
            {
                case VisualizerStyle.Glow:
                    // 2. Radial Glow (Heartbeat pulse)
                    UpdateHeartbeat(intensity);
                    var pulseScale = 0.8 + (_heartbeatValue * 0.4);
                    var auraBrush = new RadialGradientBrush
                    {
                        GradientStops = new GradientStops
                        {
                            new GradientStop(Color.FromArgb((byte)(160 * intensity), baseColor.R, baseColor.G, baseColor.B), 0.0),
                            new GradientStop(Color.FromArgb((byte)(40 * intensity), baseColor.R, baseColor.G, baseColor.B), 0.7 * pulseScale),
                            new GradientStop(Colors.Transparent, 1.0)
                        }
                    };
                    context.DrawRectangle(auraBrush, null, new Rect(0, 0, w, h));
                    
                    // 3. Spectrum Bars
                    RenderSpectrum(context, w, h, baseColor);
                    
                    // 5. Retro Post-Processing (Scanlines) - Glow has scanlines
                    RenderScanlines(context, w, h);
                    break;

                case VisualizerStyle.Particles:
                     // 4. Cosmic Bloom (Particles) - Particles focused
                    UpdateParticles();
                    foreach (var p in _particles)
                    {
                        double px = p.X * w;
                        double py = p.Y * h;
                        double pSize = p.Size * (1.0 + intensity * 4.0); // Bigger particles
                        context.DrawEllipse(new SolidColorBrush(baseColor, (float)p.Opacity), null, new Point(px, py), pSize, pSize);
                        
                        // Add star burst lines for loud moments
                        if (intensity > 0.8 && p.Opacity > 0.4)
                        {
                             context.DrawLine(new Pen(baseBrush, 0.5), new Point(px - pSize, py), new Point(px + pSize, py));
                             context.DrawLine(new Pen(baseBrush, 0.5), new Point(px, py - pSize), new Point(px, py + pSize));
                        }
                    }
                    
                    // Subtle background glow
                    UpdateHeartbeat(intensity);
                    var particleGlow = new RadialGradientBrush
                    {
                        GradientStops = new GradientStops
                        {
                            new GradientStop(Color.FromArgb((byte)(80 * intensity), baseColor.R, baseColor.G, baseColor.B), 0.0),
                            new GradientStop(Colors.Transparent, 1.0)
                        }
                    };
                    context.DrawRectangle(particleGlow, null, new Rect(0, 0, w, h));
                    break;

                case VisualizerStyle.Waves:
                    // 6. Style-Specific Effects - Waves Focus
                    RenderSpectrumWaves(context, w, h, baseBrush);
                    
                    // Scanlines for retro feel
                    RenderScanlines(context, w, h);
                    break;

                case VisualizerStyle.Forensics:
                    RenderSpectralWaterfall(context, w, h, baseColor);
                    RenderStereoPhase(context, w, h, baseColor);
                    RenderScanlines(context, w, h);
                    break;
            }

            // 7. Camera Shake (Full Mode only) - Keep for all styles
            if (Mode == VisualizerMode.Full && intensity > 0.85)
            {
                 // We can't easily transform the whole drawing context here without affecting everything, 
                 // but we've already drawn. For real shake we'd need to wrap the whole Render logic.
                 // Just a flash.
                 if (_random.NextDouble() > 0.90)
                 {
                      context.DrawRectangle(new SolidColorBrush(Colors.White, 0.15f), null, new Rect(0, 0, w, h));
                 }
            }

            // Render loop is now driven by DispatcherTimer, not by self-invalidation
        }

        private void RenderSpectrum(DrawingContext context, double w, double h, Color baseColor)
        {
            var data = SpectrumData;
            if (data == null || data.Length == 0) return;

            if (_smoothedSpectrum.Length != 64) _smoothedSpectrum = new float[64];

            // Map and smooth data to 64 bins
            int samplesPerBin = data.Length / 64;
            for (int i = 0; i < 64; i++)
            {
                float sum = 0;
                for (int j = 0; j < samplesPerBin; j++) sum += data[i * samplesPerBin + j];
                float val = (sum / samplesPerBin) * 20.0f * (float)VisualIntensity; // Scale up for visibility
                _smoothedSpectrum[i] = _smoothedSpectrum[i] * 0.6f + val * 0.4f; // Temporal smoothing
            }

            double barW = w / 64.0;
            var barBrush = new SolidColorBrush(baseColor, 0.6f);
            var glowBrush = new SolidColorBrush(Colors.White, 0.3f);

            for (int i = 0; i < 64; i++)
            {
                double barH = Math.Min(h * 0.5, _smoothedSpectrum[i] * h * 0.4);
                if (barH < 1) continue;

                var rect = new Rect(i * barW, h - barH, barW - 1, barH);
                context.DrawRectangle(barBrush, null, rect);
                
                // Top cap glow
                context.DrawRectangle(glowBrush, null, new Rect(i * barW, h - barH, barW - 1, 2));
            }
        }

        private void RenderSpectrumWaves(DrawingContext context, double w, double h, IBrush brush)
        {
            var data = SpectrumData;
            if (data == null || data.Length < 10) return;

            // Simple wave path
            var points = new List<Point>();
            double step = w / (data.Length - 1);
            for (int i = 0; i < data.Length; i++)
            {
                double val = data[i] * h * 0.4 * VisualIntensity;
                points.Add(new Point(i * step, h / 2 - val));
            }

            for (int i = 0; i < points.Count - 1; i++)
            {
                context.DrawLine(new Pen(brush, 2), points[i], points[i + 1]);
            }
        }

        private void RenderScanlines(DrawingContext context, double w, double h)
        {
            // Optimize scanlines: draw fewer or use a more efficient approach
            // On high resolution screens, drawing 500+ lines is expensive
            // INCREASING STEP to 12 for better performance
            var scanlinePen = new Pen(new SolidColorBrush(Colors.Black, 0.08f), 1);
            double step = 12; 
            for (double y = 0; y < h; y += step)
            {
                context.DrawLine(scanlinePen, new Point(0, y), new Point(w, y));
            }

            // Vignette - use a cached brush if possible, but for now just leave as is
            var vignetteBrush = new RadialGradientBrush
            {
                GradientStops = new GradientStops
                {
                    new GradientStop(Colors.Transparent, 0.5),
                    new GradientStop(Color.FromArgb(100, 0, 0, 0), 1.0)
                }
            };
            context.DrawRectangle(vignetteBrush, null, new Rect(0, 0, w, h));

#if DEBUG
            // Diagnostic Overlay (Only in Debug)
            var specLen = SpectrumData?.Length ?? 0;
            var maxSpec = specLen > 0 ? SpectrumData!.Max() : 0;
            var dataAge = DateTime.Now - _lastDataTime;
            var vuAge = DateTime.Now - _lastVuTime;
            var debugInfo = $"F:{_frameCount} | S-AGE:{dataAge.TotalSeconds:F1}s | V-AGE:{vuAge.TotalSeconds:F1}s | PLAY:{IsPlaying} | VU:{VuLeft:F2}/{VuRight:F2} | SPEC:{specLen} (MAX:{maxSpec:F4}) | STYLE:{VisualStyle} | INT:{VisualIntensity:F1}";
            var typeface = new Typeface(FontFamily.Default);
            var formattedText = new FormattedText(debugInfo, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, Brushes.Lime);
            context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.5f), null, new Rect(10, 10, formattedText.Width + 10, formattedText.Height + 10));
            context.DrawText(formattedText, new Point(15, 15));
#endif
        }

        private void UpdateHeartbeat(float intensity)
        {
            // Specifically look at low bins if spectrum is available
            if (SpectrumData != null && SpectrumData.Length > 10)
            {
                float bass = 0;
                for (int i = 0; i < 5; i++) bass += SpectrumData[i];
                bass = (bass / 5.0f) * 15.0f * (float)VisualIntensity;
                _heartbeatValue = _heartbeatValue * 0.7f + Math.Clamp(bass, 0, 1) * 0.3f;
            }
            else
            {
                _heartbeatValue = _heartbeatValue * 0.8f + intensity * 0.2f;
            }
        }

        private void UpdateParticles()
        {
            int limit = Mode == VisualizerMode.Full ? 50 : 20;
            // Adaptive particle count
            while (_particles.Count < limit) _particles.Add(CreateParticle());
            while (_particles.Count > limit) _particles.RemoveAt(0);

            foreach (var p in _particles)
            {
                p.X += p.SpeedX; p.Y += p.SpeedY;
                if (p.X < 0) p.X = 1; if (p.X > 1) p.X = 0;
                if (p.Y < 0) p.Y = 1; if (p.Y > 1) p.Y = 0;
            }
        }

        private void RenderPausedState(DrawingContext context)
        {
            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w < 10 || h < 10) return;

            // Ambient breathing effect
            var time = DateTime.Now.TimeOfDay.TotalSeconds;
            var breathe = (Math.Sin(time * 0.5) + 1.0) / 2.0; // 0.0 to 1.0 cycle over ~12s
            
            var baseColor = Color.FromRgb(78, 201, 176); // ORBIT Teal
            var opacity = (byte)(10 + (breathe * 20)); // 10 to 30 opacity

            var gradient = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(opacity, baseColor.R, baseColor.G, baseColor.B), 0),
                    new GradientStop(Colors.Transparent, 1.0)
                }
            };
            
            // Draw a large breathing glow
            var radius = Math.Min(w, h) * (0.3 + (breathe * 0.1));
            context.DrawEllipse(gradient, null, new Point(w/2, h/2), radius, radius);

            // Subtle scanlines even when paused
            RenderScanlines(context, w, h);
        }

        private void RenderFallbackVisualization(DrawingContext context, double w, double h)
        {
            // Render a pulsing circle when spectrum data is missing but audio is playing
            var intensity = ((VuLeft + VuRight) / 2.0f) * (float)VisualIntensity;
            var radius = Math.Max(50, Math.Min(w, h) * 0.15 * (0.7 + intensity * 0.6));
            var center = new Point(w / 2, h / 2);
            
            var baseColor = GetVibeColor();
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(intensity * 150), baseColor.R, baseColor.G, baseColor.B), 0),
                    new GradientStop(Color.FromArgb((byte)(intensity * 50), baseColor.R, baseColor.G, baseColor.B), 0.7),
                    new GradientStop(Colors.Transparent, 1)
                }
            };
            
            context.DrawEllipse(brush, null, center, radius, radius);
            
            // Add pulsing ring
            var ringRadius = radius * 1.5;
            var ringPen = new Pen(new SolidColorBrush(baseColor, intensity * 0.5f), 2);
            context.DrawEllipse(null, ringPen, center, ringRadius, ringRadius);
        }

        private readonly List<Point> _phaseHistory = new();
        private const int MaxPhaseHistory = 50;

        private void RenderSpectralWaterfall(DrawingContext context, double w, double h, Color baseColor)
        {
            var data = SpectrumData;
            if (data == null || data.Length == 0) return;

            // Maintain history
            var currentFrame = new float[64];
            int samplesPerBin = data.Length / 64;
            for (int i = 0; i < 64; i++)
            {
                float sum = 0;
                for (int j = 0; j < samplesPerBin; j++) sum += data[i * samplesPerBin + j];
                currentFrame[i] = (sum / samplesPerBin) * 20.0f * (float)VisualIntensity;
            }

            _spectrumHistory.Insert(0, currentFrame);
            if (_spectrumHistory.Count > MaxHistory) _spectrumHistory.RemoveAt(_spectrumHistory.Count - 1);

            // Draw waterfall with heatmap effect
            double binW = w / 64.0;
            double rowH = (h * 0.45) / MaxHistory;

            for (int r = _spectrumHistory.Count - 1; r >= 0; r--)
            {
                var frame = _spectrumHistory[r];
                float rowOpacity = 1.0f - ((float)r / MaxHistory);
                
                // Vertical position for this row
                double rowY = h * 0.5 - (r * rowH);

                for (int i = 0; i < 64; i++)
                {
                    double val = frame[i];
                    if (val < 0.01) continue;

                    // Heatmap color calculation
                    // High energy = White, Mid = BaseColor, Low = DarkerBase
                    Color heatColor;
                    if (val > 0.8) heatColor = Colors.White;
                    else if (val > 0.4) heatColor = baseColor;
                    else heatColor = Color.FromArgb((byte)(baseColor.A * 0.4), baseColor.R, baseColor.G, baseColor.B);

                    var colorBrush = new SolidColorBrush(heatColor, rowOpacity);
                    var rect = new Rect(i * binW, rowY - 2, binW, 2);
                    context.DrawRectangle(colorBrush, null, rect);
                }
            }
        }

        private void RenderStereoPhase(DrawingContext context, double w, double h, Color baseColor)
        {
            // Vector Scope / Phase Meter (Lissajous)
            double size = Math.Min(w, h) * 0.35;
            Point center = new Point(w * 0.5, h * 0.75); // Place it below the waterfall
            
            // Compass/Scale
            var scalePen = new Pen(new SolidColorBrush(baseColor, 0.2f), 1);
            context.DrawEllipse(null, scalePen, center, size / 2, size / 2);
            context.DrawLine(scalePen, new Point(center.X - size / 2, center.Y), new Point(center.X + size / 2, center.Y));
            context.DrawLine(scalePen, new Point(center.X, center.Y - size / 2), new Point(center.X, center.Y + size / 2));
            
            // Phase Text
            var typeface = new Typeface(FontFamily.Default);
            var leftText = new FormattedText("L", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, new SolidColorBrush(baseColor, 0.5f));
            var rightText = new FormattedText("R", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, new SolidColorBrush(baseColor, 0.5f));
            context.DrawText(leftText, new Point(center.X - size / 2 - 15, center.Y - 5));
            context.DrawText(rightText, new Point(center.X + size / 2 + 5, center.Y - 5));

            // X/Y Calculation (Stereo Difference vs Sum)
            float L = VuLeft * (float)VisualIntensity;
            float R = VuRight * (float)VisualIntensity;
            
            // Rotate 45 degrees for standard goniometer view
            // Mid = (L + R) / sqrt(2)
            // Side = (L - R) / sqrt(2)
            float m = (L + R) * 0.707f;
            float s = (L - R) * 0.707f;
            
            double vx = s * (size / 2);
            double vy = -m * (size / 2); // Up is positive mid

            // Noise for "live" feel if values are static
            if (IsPlaying)
            {
                vx += (_random.NextDouble() - 0.5) * 5;
                vy += (_random.NextDouble() - 0.5) * 5;
            }

            var currentPoint = new Point(center.X + vx, center.Y + vy);
            _phaseHistory.Insert(0, currentPoint);
            if (_phaseHistory.Count > MaxPhaseHistory) _phaseHistory.RemoveAt(_phaseHistory.Count - 1);

            // Draw trace
            for (int i = 0; i < _phaseHistory.Count - 1; i++)
            {
                float opacity = 1.0f - ((float)i / MaxPhaseHistory);
                var tracePen = new Pen(new SolidColorBrush(Colors.White, opacity), 1.5);
                context.DrawLine(tracePen, _phaseHistory[i], _phaseHistory[i + 1]);
            }

            // Current head glow
            context.DrawEllipse(new SolidColorBrush(Colors.White), null, currentPoint, 2, 2);
            context.DrawEllipse(new SolidColorBrush(baseColor, 0.4f), null, currentPoint, 6, 6);
        }

        private Color GetVibeColor()
        {
            if (Energy < 0.35) return Color.FromRgb(65, 105, 225); // RoyalBlue
            if (Energy < 0.65) return Color.FromRgb(50, 205, 50);  // LimeGreen
            return Color.FromRgb(255, 45, 0);                      // Neon Red
        }

        private class Particle { public double X, Y, Size, SpeedX, SpeedY, Opacity; }
    }
}
