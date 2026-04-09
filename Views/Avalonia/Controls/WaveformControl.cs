using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using SLSKDONET.Models;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Timeline;
using SkiaSharp;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public class WaveformControl : Control
    {
        public static readonly StyledProperty<WaveformAnalysisData> WaveformDataProperty =
            AvaloniaProperty.Register<WaveformControl, WaveformAnalysisData>(nameof(WaveformData));

        public WaveformAnalysisData WaveformData
        {
            get => GetValue(WaveformDataProperty);
            set => SetValue(WaveformDataProperty, value);
        }

        public static readonly StyledProperty<float> ProgressProperty =
            AvaloniaProperty.Register<WaveformControl, float>(nameof(Progress), 0f);

        public float Progress
        {
            get => GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        public static readonly StyledProperty<bool> IsRollingProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(IsRolling), false);

        public bool IsRolling
        {
            get => GetValue(IsRollingProperty);
            set => SetValue(IsRollingProperty, value);
        }

        public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
            AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(PlayheadBrush), Brushes.White);

        public IBrush? PlayheadBrush
        {
            get => GetValue(PlayheadBrushProperty);
            set => SetValue(PlayheadBrushProperty, value);
        }

        public static readonly StyledProperty<System.Windows.Input.ICommand?> SeekCommandProperty =
            AvaloniaProperty.Register<WaveformControl, System.Windows.Input.ICommand?>(nameof(SeekCommand));

        public System.Windows.Input.ICommand? SeekCommand
        {
            get => GetValue(SeekCommandProperty);
            set => SetValue(SeekCommandProperty, value);
        }

        // Band Properties (Low, Mid, High) for RGB rendering
        public static readonly StyledProperty<byte[]?> LowBandProperty = AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(LowBand));
        public byte[]? LowBand { get => GetValue(LowBandProperty); set => SetValue(LowBandProperty, value); }
        public static readonly StyledProperty<byte[]?> MidBandProperty = AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(MidBand));
        public byte[]? MidBand { get => GetValue(MidBandProperty); set => SetValue(MidBandProperty, value); }
        public static readonly StyledProperty<byte[]?> HighBandProperty = AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(HighBand));
        public byte[]? HighBand { get => GetValue(HighBandProperty); set => SetValue(HighBandProperty, value); }

        public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(Foreground));
        public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(Background));
        public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<OrbitCue>?> CuesProperty =
            AvaloniaProperty.Register<WaveformControl, System.Collections.Generic.IEnumerable<OrbitCue>?>(nameof(Cues));

        public System.Collections.Generic.IEnumerable<OrbitCue>? Cues
        {
            get => GetValue(CuesProperty);
            set => SetValue(CuesProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<PhraseSegment>?> PhraseSegmentsProperty =
            AvaloniaProperty.Register<WaveformControl, System.Collections.Generic.IEnumerable<PhraseSegment>?>(nameof(PhraseSegments));

        public System.Collections.Generic.IEnumerable<PhraseSegment>? PhraseSegments
        {
            get => GetValue(PhraseSegmentsProperty);
            set => SetValue(PhraseSegmentsProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<float>?> EnergyCurveProperty =
            AvaloniaProperty.Register<WaveformControl, System.Collections.Generic.IEnumerable<float>?>(nameof(EnergyCurve));

        public System.Collections.Generic.IEnumerable<float>? EnergyCurve
        {
            get => GetValue(EnergyCurveProperty);
            set => SetValue(EnergyCurveProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<float>?> VocalDensityCurveProperty =
            AvaloniaProperty.Register<WaveformControl, System.Collections.Generic.IEnumerable<float>?>(nameof(VocalDensityCurve));

        public System.Collections.Generic.IEnumerable<float>? VocalDensityCurve
        {
            get => GetValue(VocalDensityCurveProperty);
            set => SetValue(VocalDensityCurveProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<int>?> SegmentedEnergyProperty =
            AvaloniaProperty.Register<WaveformControl, System.Collections.Generic.IEnumerable<int>?>(nameof(SegmentedEnergy));

        public System.Collections.Generic.IEnumerable<int>? SegmentedEnergy
        {
            get => GetValue(SegmentedEnergyProperty);
            set => SetValue(SegmentedEnergyProperty, value);
        }

        public static readonly StyledProperty<bool> IsEditingProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(IsEditing), false);

        public bool IsEditing
        {
            get => GetValue(IsEditingProperty);
            set => SetValue(IsEditingProperty, value);
        }

        public static readonly StyledProperty<SnappingMode> SnappingModeProperty =
            AvaloniaProperty.Register<WaveformControl, SnappingMode>(nameof(SnappingMode), SnappingMode.Soft);

        public SnappingMode SnappingMode
        {
            get => GetValue(SnappingModeProperty);
            set => SetValue(SnappingModeProperty, value);
        }

        public static readonly StyledProperty<float> BpmProperty =
            AvaloniaProperty.Register<WaveformControl, float>(nameof(Bpm), 0f);

        public float Bpm
        {
            get => GetValue(BpmProperty);
            set => SetValue(BpmProperty, value);
        }

        public static readonly StyledProperty<System.Windows.Input.ICommand?> SegmentUpdatedCommandProperty =
            AvaloniaProperty.Register<WaveformControl, System.Windows.Input.ICommand?>(nameof(SegmentUpdatedCommand));

        public System.Windows.Input.ICommand? SegmentUpdatedCommand
        {
            get => GetValue(SegmentUpdatedCommandProperty);
            set => SetValue(SegmentUpdatedCommandProperty, value);
        }

        // Sprint 2: Zoom Properties
        public static readonly StyledProperty<double> ZoomLevelProperty =
            AvaloniaProperty.Register<WaveformControl, double>(nameof(ZoomLevel), 1.0);

        /// <summary>
        /// Zoom level (1.0 = full track, 4.0 = 4x zoom, etc.)
        /// </summary>
        public double ZoomLevel
        {
            get => GetValue(ZoomLevelProperty);
            set => SetValue(ZoomLevelProperty, Math.Clamp(value, 1.0, 16.0));
        }

        public static readonly StyledProperty<double> ViewOffsetProperty =
            AvaloniaProperty.Register<WaveformControl, double>(nameof(ViewOffset), 0.0);

        /// <summary>
        /// Horizontal offset as fraction of track (0.0 = start, 1.0 = end)
        /// </summary>
        public double ViewOffset
        {
            get => GetValue(ViewOffsetProperty);
            set => SetValue(ViewOffsetProperty, Math.Clamp(value, 0.0, Math.Max(0, 1.0 - (1.0 / ZoomLevel))));
        }

        public static readonly StyledProperty<System.Windows.Input.ICommand?> CueClickedCommandProperty =
            AvaloniaProperty.Register<WaveformControl, System.Windows.Input.ICommand?>(nameof(CueClickedCommand));

        /// <summary>
        /// Command triggered when a cue marker is clicked (for instant audition)
        /// </summary>
        public System.Windows.Input.ICommand? CueClickedCommand
        {
            get => GetValue(CueClickedCommandProperty);
            set => SetValue(CueClickedCommandProperty, value);
        }

        /// <summary>
        /// When <c>true</c>, cues released within <see cref="SnapRadiusSeconds"/>
        /// of a beat are automatically snapped to that beat.
        /// </summary>
        public static readonly StyledProperty<bool> SnapToGridEnabledProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(SnapToGridEnabled), true);

        public bool SnapToGridEnabled
        {
            get => GetValue(SnapToGridEnabledProperty);
            set => SetValue(SnapToGridEnabledProperty, value);
        }

        /// <summary>
        /// Maximum distance (seconds) within which a cue snaps to the nearest beat.
        /// Default is 50 ms.
        /// </summary>
        public static readonly StyledProperty<double> SnapRadiusSecondsProperty =
            AvaloniaProperty.Register<WaveformControl, double>(nameof(SnapRadiusSeconds), 0.05);

        public double SnapRadiusSeconds
        {
            get => GetValue(SnapRadiusSecondsProperty);
            set => SetValue(SnapRadiusSecondsProperty, Math.Max(0, value));
        }


        static WaveformControl()
        {
            AffectsRender<WaveformControl>(
                WaveformDataProperty, 
                ProgressProperty, 
                IsRollingProperty, 
                LowBandProperty, 
                MidBandProperty, 
                HighBandProperty, 
                CuesProperty, 
                PhraseSegmentsProperty,
                EnergyCurveProperty,
                VocalDensityCurveProperty,
                SegmentedEnergyProperty,
                ForegroundProperty,
                BackgroundProperty,
                PlayheadBrushProperty,
                ZoomLevelProperty,
                ViewOffsetProperty,
                FrequencyColorModeProperty);
        }


        public static readonly StyledProperty<System.Windows.Input.ICommand?> CueUpdatedCommandProperty =
            AvaloniaProperty.Register<WaveformControl, System.Windows.Input.ICommand?>(nameof(CueUpdatedCommand));

        public System.Windows.Input.ICommand? CueUpdatedCommand
        {
            get => GetValue(CueUpdatedCommandProperty);
            set => SetValue(CueUpdatedCommandProperty, value);
        }

        private OrbitCue? _draggedCue;
        private PhraseSegment? _draggedSegment;
        private bool _isDraggingStart; // True if dragging start handle, False if end
        private bool _isDraggingCue;
        private bool _isDraggingProgress;
        private bool _isDraggingSegment;
        private double _hoverX = -1; // -1 = not hovering
        private const double CueHitThreshold = 10.0;
        private const double HandleWidth = 8.0;

        // Beat-snap highlight state
        private double _snapHighlightSeconds = -1.0; // ≥0 while highlight is active
        private float _snapHighlightAlpha = 0f;
        private DispatcherTimer? _snapHighlightTimer;

        // Bitmap Cache
        private RenderTargetBitmap? _baseBitmap;
        private RenderTargetBitmap? _activeBitmap;
        private Size _lastRenderSize;
        private bool _isDirty = true;

        // Sprint 4: Performance Optimizations
        private double _lastZoomLevel = 1.0;

        private DateTime _lastRenderTime = DateTime.MinValue;
        private const double FrameThrottleMs = 33.33; // ~30 FPS max
        private const double SizeTolerance = 5.0; // Pixels tolerance for bitmap reuse

        /// <summary>
        /// When <c>true</c>, the waveform is rendered using tri-band frequency colours
        /// (Low=hot-pink/red, Mid=neon-green, High=cyan/blue). If no <see cref="LowBand"/>
        /// data is bound, a synthetic approximation is generated from the peak data.
        /// </summary>
        public static readonly StyledProperty<bool> FrequencyColorModeProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(FrequencyColorMode), false);

        public bool FrequencyColorMode
        {
            get => GetValue(FrequencyColorModeProperty);
            set => SetValue(FrequencyColorModeProperty, value);
        }

        // Sprint 4: Vocal Ghost Layer
        public static readonly StyledProperty<bool> ShowVocalGhostProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(ShowVocalGhost), false);

        public bool ShowVocalGhost
        {
            get => GetValue(ShowVocalGhostProperty);
            set => SetValue(ShowVocalGhostProperty, value);
        }

        private DispatcherTimer? _ghostPulseTimer;
        private float _ghostOpacity = 0.6f;
        private bool _ghostPulseUp = true;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_ghostPulseTimer == null)
            {
                _ghostPulseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (s, ev) =>
                {
                    if (ShowVocalGhost)
                    {
                        // Sine wave pulse logic
                        // Simple linear approximation for now or actual sine
                        // 0.6 to 1.0
                        if (_ghostPulseUp)
                        {
                            _ghostOpacity += 0.02f;
                            if (_ghostOpacity >= 1.0f) { _ghostOpacity = 1.0f; _ghostPulseUp = false; }
                        }
                        else
                        {
                            _ghostOpacity -= 0.02f;
                            if (_ghostOpacity <= 0.6f) { _ghostOpacity = 0.6f; _ghostPulseUp = true; }
                        }
                        InvalidateVisual();
                    }
                });
                _ghostPulseTimer.Start();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _ghostPulseTimer?.Stop();
            _ghostPulseTimer = null;
            _snapHighlightTimer?.Stop();
            _snapHighlightTimer = null;
        }


        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == WaveformDataProperty ||
                change.Property == BoundsProperty ||
                change.Property == LowBandProperty ||
                change.Property == MidBandProperty ||
                change.Property == HighBandProperty)
            {
                _isDirty = true;
                InvalidateVisual();
            }
            // Sprint 4: Only invalidate on significant zoom changes (>5%)
            else if (change.Property == ZoomLevelProperty)
            {
                double newZoom = (double)(change.NewValue ?? 1.0);
                if (Math.Abs(newZoom - _lastZoomLevel) / _lastZoomLevel > 0.05)
                {
                    _isDirty = true;
                    _lastZoomLevel = newZoom;
                }
                InvalidateVisual();
            }
            else if (change.Property == ViewOffsetProperty)
            {
                // Offset changes don't require bitmap rebuild, just redraw
                InvalidateVisual();
            }
        }


        // Sprint 2: Scroll-to-Zoom
        protected override void OnPointerWheelChanged(global::Avalonia.Input.PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            
            // Zoom centered on mouse position
            var point = e.GetPosition(this);
            double mouseRatio = point.X / Bounds.Width;
            
            // Calculate zoom delta (Ctrl+scroll for faster zoom)
            double zoomDelta = e.Delta.Y > 0 ? 1.2 : 0.8;
            double oldZoom = ZoomLevel;
            double newZoom = Math.Clamp(oldZoom * zoomDelta, 1.0, 16.0);
            
            if (Math.Abs(newZoom - oldZoom) > 0.01)
            {
                // Adjust offset to keep mouse position anchored
                double visibleFraction = 1.0 / oldZoom;
                double mousePosition = ViewOffset + (mouseRatio * visibleFraction);
                
                double newVisibleFraction = 1.0 / newZoom;
                double newOffset = mousePosition - (mouseRatio * newVisibleFraction);
                
                ZoomLevel = newZoom;
                ViewOffset = Math.Clamp(newOffset, 0, Math.Max(0, 1.0 - newVisibleFraction));
                
                _isDirty = true;
                InvalidateVisual();
            }
            e.Handled = true;
        }


        protected override void OnPointerPressed(global::Avalonia.Input.PointerPressedEventArgs e)
        {
            var point = e.GetPosition(this);
            var data = WaveformData;
            var cues = Cues;

            // 1. Hit Test for Cues - Click triggers instant audition
            if (cues != null && data != null && data.DurationSeconds > 0)
            {
                foreach (var cue in cues)
                {
                    double x = GetCueX(cue, data);
                    if (Math.Abs(point.X - x) <= CueHitThreshold)
                    {
                        // Sprint 2: Instant Hot-Cue Audition on click
                        if (CueClickedCommand != null && CueClickedCommand.CanExecute(cue))
                        {
                            CueClickedCommand.Execute(cue);
                        }
                        _draggedCue = cue;
                        _isDraggingCue = true;
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                }
            }


            // 2. Hit Test for Phrase Boundaries (New: Phase 2)
            if (IsEditing && PhraseSegments != null && data != null && data.DurationSeconds > 0)
            {
                foreach (var seg in PhraseSegments)
                {
                    double startX = (seg.Start / data.DurationSeconds) * Bounds.Width;
                    double endX = ((seg.Start + seg.Duration) / data.DurationSeconds) * Bounds.Width;

                    if (Math.Abs(point.X - startX) <= CueHitThreshold)
                    {
                        _draggedSegment = seg;
                        _isDraggingSegment = true;
                        _isDraggingStart = true;
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                    if (Math.Abs(point.X - endX) <= CueHitThreshold)
                    {
                        _draggedSegment = seg;
                        _isDraggingSegment = true;
                        _isDraggingStart = false;
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // 3. Progress Dragging / Click-Seek
            _isDraggingProgress = true;
            e.Pointer.Capture(this);
            UpdateProgressFromPoint(point);
            e.Handled = true;
        }

        protected override void OnPointerMoved(global::Avalonia.Input.PointerEventArgs e)
        {
            var point = e.GetPosition(this);
            var data = WaveformData;
            var cues = Cues;

            bool hoverCue = false;
            if (cues != null && data != null && data.DurationSeconds > 0)
            {
                foreach (var cue in cues)
                {
                    double cx = GetCueX(cue, data);
                    if (Math.Abs(point.X - cx) <= CueHitThreshold)
                    {
                        hoverCue = true;
                        break;
                    }
                }
            }
            Cursor = hoverCue || _isDraggingCue ? new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.SizeWestEast) : null;

            if (_isDraggingCue && _draggedCue != null && data != null && data.DurationSeconds > 0)
            {
                double x = Math.Clamp(point.X, 0, Bounds.Width);
                if (IsRolling)
                {
                     // (Rolling logic)
                }
                else
                {
                    _draggedCue.Timestamp = (x / Bounds.Width) * data.DurationSeconds;
                }
                InvalidateVisual();
            }
            else if (_isDraggingSegment && _draggedSegment != null && data != null && data.DurationSeconds > 0)
            {
                double x = Math.Clamp(point.X, 0, Bounds.Width);
                float newTime = (float)((x / Bounds.Width) * data.DurationSeconds);
                
                // Landmarks for snapping
                var landmarks = PhraseSegments?.SelectMany(s => new[] { s.Start, s.Start + s.Duration }) ?? Enumerable.Empty<float>();
                newTime = SnappingEngine.Snap(newTime, SnappingMode, Bpm, landmarks);


                if (_isDraggingStart)
                {
                    float maxStart = _draggedSegment.Start + _draggedSegment.Duration - 0.1f;
                    _draggedSegment.Start = Math.Min(newTime, maxStart);
                }
                else
                {
                    float minEnd = _draggedSegment.Start + 0.1f;
                    _draggedSegment.Duration = Math.Max(newTime - _draggedSegment.Start, 0.1f);
                }
                
                InvalidateVisual();
            }
            else if (_isDraggingProgress)
            {
                UpdateProgressFromPoint(point);
            }

            // Update hover cursor (only when not dragging a cue or segment)
            if (!_isDraggingCue && !_isDraggingSegment)
            {
                _hoverX = point.X;
                InvalidateVisual();
            }
        }

        protected override void OnPointerEntered(global::Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _hoverX = e.GetPosition(this).X;
            InvalidateVisual();
        }

        protected override void OnPointerExited(global::Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverX = -1;
            InvalidateVisual();
        }

        private void UpdateProgressFromPoint(Point point)
        {
             if (IsRolling)
             {
                  // In Rolling mode, clicking left/right of center seeks relative to playhead?
                  // Or we just treat the whole strip as 0-1 range still? 
                  // Usually, clicking a rolling waveform seeks to that spot.
                  // For simplicity, let's keep the click 0-1 range for the static view, 
                  // and maybe a relative seek for rolling.
             }
             
             var progress = (float)(point.X / Bounds.Width);
             progress = Math.Clamp(progress, 0f, 1f);
             
             if (SeekCommand != null && SeekCommand.CanExecute(progress))
             {
                 SeekCommand.Execute(progress);
             }
             Progress = progress; // Immediate UI feedback
        }

        protected override void OnPointerReleased(global::Avalonia.Input.PointerReleasedEventArgs e)
        {
            if (_isDraggingCue)
            {
                _isDraggingCue = false;

                // Magnetic beat-grid snapping: snap the cue to the nearest beat when
                // within SnapRadiusSeconds (default 50 ms).
                if (SnapToGridEnabled && _draggedCue != null && Bpm > 0 && WaveformData != null)
                {
                    double? snapped = BeatGridService.GetNearestBeatSeconds(
                        _draggedCue.Timestamp, Bpm, SnapRadiusSeconds);
                    if (snapped.HasValue)
                    {
                        _draggedCue.Timestamp = snapped.Value;
                        ShowSnapHighlight(snapped.Value);
                    }
                }

                // Mark as user-edited so auto-analysis never overwrites it
                if (_draggedCue != null) _draggedCue.Source = CueSource.User;
                if (CueUpdatedCommand != null && CueUpdatedCommand.CanExecute(_draggedCue))
                    CueUpdatedCommand.Execute(_draggedCue);
                _draggedCue = null;
            }
            else if (_isDraggingSegment)
            {
                _isDraggingSegment = false;
                if (SegmentUpdatedCommand != null && SegmentUpdatedCommand.CanExecute(_draggedSegment))
                    SegmentUpdatedCommand.Execute(_draggedSegment);
                _draggedSegment = null;
            }
            _isDraggingProgress = false;
            e.Pointer.Capture(null);
        }

        /// <summary>
        /// Briefly flashes a cyan snap-indicator line at <paramref name="positionSeconds"/>
        /// to give the user visual feedback that a cue was snapped to the beat grid.
        /// The indicator fades over ~500 ms.
        /// </summary>
        private void ShowSnapHighlight(double positionSeconds)
        {
            _snapHighlightSeconds = positionSeconds;
            _snapHighlightAlpha = 1.0f;
            _snapHighlightTimer?.Stop();
            _snapHighlightTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(33),
                DispatcherPriority.Render,
                (s, ev) =>
                {
                    _snapHighlightAlpha -= 0.065f; // ~500 ms fade (1.0 / 0.065 ≈ 15 ticks × 33 ms)
                    if (_snapHighlightAlpha <= 0f)
                    {
                        _snapHighlightAlpha = 0f;
                        _snapHighlightSeconds = -1.0;
                        _snapHighlightTimer?.Stop();
                        _snapHighlightTimer = null;
                    }
                    InvalidateVisual();
                });
            _snapHighlightTimer.Start();
            InvalidateVisual();
        }

        private double GetCueX(OrbitCue cue, WaveformAnalysisData data)
        {
            if (IsRolling)
            {
                double center = Bounds.Width / 2;
                double pixelsPerSec = Bounds.Width / 10.0; // 10s window
                return center + (cue.Timestamp - (Progress * data.DurationSeconds)) * pixelsPerSec;
            }
            return (cue.Timestamp / data.DurationSeconds) * Bounds.Width;
        }

        public override void Render(DrawingContext context)
        {
            var data = WaveformData;
            if (data == null || data.IsEmpty || data.PeakData == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                context.DrawLine(new Pen(Brushes.Gray, 1), new Point(0, Bounds.Height / 2), new Point(Bounds.Width, Bounds.Height / 2));
                return;
            }

            // Sprint 4: Frame throttling (60 FPS max)
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastRenderTime).TotalMilliseconds;
            if (elapsed < FrameThrottleMs && !_isDirty)
            {
                // Skip frame but schedule next render
                return;
            }
            _lastRenderTime = now;

            // Sprint 4: Bitmap reuse with size tolerance
            bool needsNewBitmap = _isDirty || _baseBitmap == null || _activeBitmap == null ||
                                  Math.Abs(_lastRenderSize.Width - Bounds.Width) > SizeTolerance ||
                                  Math.Abs(_lastRenderSize.Height - Bounds.Height) > SizeTolerance;

            if (needsNewBitmap)
            {
                UpdateBitmapCache(Bounds.Size);
                _isDirty = false;
                _lastRenderSize = Bounds.Size;
            }


            var width = Bounds.Width;
            var height = Bounds.Height;


            // 0. Draw Vocal Ghost Layer (Behind everything)
            var vocalCurve = VocalDensityCurve?.ToList();
            if (ShowVocalGhost && vocalCurve != null && vocalCurve.Count > 0)
            {
                context.Custom(new VocalGhostDrawOperation(new Rect(0, 0, width, height), vocalCurve, data.DurationSeconds, Progress, _ghostOpacity, IsRolling, ZoomLevel, ViewOffset));
            }

            // 1. Draw Phrase Segments (Background blocks)
            RenderPhraseSegments(context, width, height);

            if (IsRolling)
            {
                // For rolling, we just use the direct render for now as it needs continuous updating
                RenderRolling(context, data, width, height, height / 2);
            }
            else
            {
                // fast render using cached bitmaps
                if (_baseBitmap != null)
                    context.DrawImage(_baseBitmap, new Rect(0, 0, width, height));

                if (_activeBitmap != null)
                {
                    double playedWidth = Progress * width;
                    // Clip to played area
                    using (context.PushClip(new Rect(0, 0, playedWidth, height)))
                    {
                        context.DrawImage(_activeBitmap, new Rect(0, 0, width, height));
                    }
                }
            }

            // 2. Draw Curves (Energy, Vocals)
            RenderCurves(context, width, height);

            // Draw hover seek cursor (semi-transparent white line, only when not dragging)
            if (_hoverX >= 0 && !_isDraggingProgress && !_isDraggingCue)
            {
                var hoverPen = new Pen(new SolidColorBrush(Colors.White, 0.35), 1);
                context.DrawLine(hoverPen, new Point(_hoverX, 0), new Point(_hoverX, height));

                // Time tooltip: show estimated position as % or mm:ss if duration known
                if (WaveformData != null && WaveformData.DurationSeconds > 0)
                {
                    double hoverFraction = Math.Clamp(_hoverX / width, 0, 1);
                    double hoverSecs = hoverFraction * WaveformData.DurationSeconds;
                    int mm = (int)(hoverSecs / 60);
                    int ss = (int)(hoverSecs % 60);
                    string timeLabel = $"{mm}:{ss:D2}";

                    var tf = new FormattedText(
                        timeLabel,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        Typeface.Default,
                        10,
                        new SolidColorBrush(Colors.White, 0.75));

                    double labelX = (_hoverX + 4 + tf.Width > width) ? _hoverX - tf.Width - 4 : _hoverX + 4;
                    context.DrawText(tf, new Point(labelX, 4));
                }
            }

            // Draw Playhead Line
            double playheadX = IsRolling ? width / 2 : Progress * width;
            context.DrawLine(new Pen(PlayheadBrush ?? Brushes.White, 2), new Point(playheadX, 0), new Point(playheadX, height));

            RenderCues(context, width, height);

            // Snap indicator: brief cyan glow fades after magnetic snap
            if (_snapHighlightSeconds >= 0 && WaveformData != null &&
                WaveformData.DurationSeconds > 0 && _snapHighlightAlpha > 0)
            {
                double snapX = (_snapHighlightSeconds / WaveformData.DurationSeconds) * width;
                var glowBrush = new SolidColorBrush(Color.FromRgb(0, 207, 255), _snapHighlightAlpha * 0.25f);
                context.DrawRectangle(glowBrush, null, new Rect(snapX - 4, 0, 8, height));
                var linePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 207, 255), _snapHighlightAlpha), 2);
                context.DrawLine(linePen, new Point(snapX, 0), new Point(snapX, height));
            }
        }

        private void UpdateBitmapCache(Size size)
        {
            if (size.Width <= 0 || size.Height <= 0) return;

            // Dispose old bitmaps
            _baseBitmap?.Dispose();
            _activeBitmap?.Dispose();

            // Create new bitmaps
            // Note: Pixel size should match visual size * scaling, but for now 1:1 is likely fine or we get DPI
            var pixelSize = new PixelSize((int)size.Width, (int)size.Height);
            var dpi = new Vector(96, 96); // Standard DPI

            _baseBitmap = new RenderTargetBitmap(pixelSize, dpi);
            _activeBitmap = new RenderTargetBitmap(pixelSize, dpi);

            var data = WaveformData;
            var mid = size.Height / 2;
            var width = size.Width;

            // Render Unplayed (Base) State
            using (var ctx = _baseBitmap.CreateDrawingContext())
            {
                 RenderStaticToContext(ctx, data, width, size.Height, mid, false);
            }

            // Render Played (Active) State
            using (var ctx = _activeBitmap.CreateDrawingContext())
            {
                 RenderStaticToContext(ctx, data, width, size.Height, mid, true);
            }
        }

        private void RenderStaticToContext(DrawingContext context, WaveformAnalysisData data, double width, double height, double mid, bool isActive)
        {
            var samples = data.PeakData!.Length;
            double step = width / samples;
            var lowData  = LowBand  ?? data.LowData;
            var midData  = MidBand  ?? data.MidData;
            var highData = HighBand ?? data.HighData;
            bool hasRgb  = lowData != null && midData != null && highData != null && lowData.Length > 0;

            if (hasRgb)
            {
                RenderTrueRgb(context, data, width, height, mid, samples, step, lowData!, midData!, highData!, false, 0, isActive);
            }
            else if (FrequencyColorMode)
            {
                // Synthesize pseudo tri-band data from amplitude so freq colours still show
                // even when full-spectrum analysis hasn't run yet.
                var peak = data.PeakData!;
                var synLow  = new byte[samples];
                var synMid  = new byte[samples];
                var synHigh = new byte[samples];
                for (int i = 0; i < samples; i++)
                {
                    byte p = peak[i];
                    // Loud transients carry more low-end; quiet detail sits in highs.
                    synLow[i]  = (byte)(p > 160 ? p : 0);
                    synMid[i]  = (byte)(p is > 80 and < 220 ? p : 0);
                    synHigh[i] = (byte)(p < 110 ? (byte)(110 - p) : 0);
                }
                RenderTrueRgb(context, data, width, height, mid, samples, step, synLow, synMid, synHigh, false, 0, isActive);
            }
            else
            {
                RenderSingleBandCached(context, data, width, mid, samples, step, isActive);
            }
        }

        private static readonly Pen StaticBasePen = new Pen(Brushes.DimGray, 1);
        private static readonly Pen StaticPlayedPen = new Pen(Brushes.DeepSkyBlue, 1);
        
        // Cache for RGB pens to avoid allocations
        private static readonly Pen LowBasePen = new Pen(new SolidColorBrush(Color.FromRgb(100, 0, 0), 0.35f * 0.8f), 1);
        private static readonly Pen LowPlayedPen = new Pen(new SolidColorBrush(Colors.Red, 0.8f), 1);
        private static readonly Pen MidBasePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 100, 0), 0.35f * 0.7f), 1);
        private static readonly Pen MidPlayedPen = new Pen(new SolidColorBrush(Colors.Lime, 0.7f), 1);
        private static readonly Pen HighBasePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 80, 100), 0.35f), 1);
        private static readonly Pen HighPlayedPen = new Pen(new SolidColorBrush(Colors.DeepSkyBlue, 1.0f), 1);

        private void RenderSingleBandCached(DrawingContext context, WaveformAnalysisData data, double width, double mid, int samples, double step, bool isActive)
        {
            // Draw full waveform in one color
            int targetColumns = Math.Max(1, (int)width);
            int stride = Math.Max(1, samples / targetColumns);
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                for (int i = 0; i < samples; i += stride)
                {
                    double x = i * step;
                    double h = (data.PeakData![i] / 255.0) * mid;
                    if (h < 0.5) continue;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, isActive ? StaticPlayedPen : StaticBasePen, geom);
        }



        // Optimzied TrueRGB: Renders FULL waveform with specific opacity/brightness
        private void RenderTrueRgb(DrawingContext context, WaveformAnalysisData data, double width, double height, double mid, int samples, double step, byte[] low, byte[] midB, byte[] high, bool isRolling, double currentXOffset = 0, bool isActive = true)
        {
            var playedLimit = (int)(Progress * samples);
            var peak = data.PeakData!;
            int targetColumns = Math.Max(1, (int)width);
            int stride = Math.Max(1, samples / targetColumns);
            
            // Segmented Energy Tinting (Phase 25)
            var energyList = SegmentedEnergy?.ToList();
            var cuesList = Cues?.OrderBy(c => c.Timestamp).ToList();
            double duration = data.DurationSeconds > 0 ? data.DurationSeconds : samples / 100.0;

            // Professional Neon Palette
            var lowColor = Color.FromRgb(255, 40, 100);    // Hot Pink / Red
            var midColor = Color.FromRgb(0, 255, 120);    // Neon Green
            var highColor = Color.FromRgb(0, 200, 255);   // Cyan / Blue

            for (int i = 0; i < Math.Min(samples, low.Length); i += stride)
            {
                if (i >= peak.Length) break;
                
                double h = (peak[i] / 255.0) * mid;
                if (h < 0.5) continue;

                double x = (i * step) + currentXOffset;
                if (x < -step || x > width + step) continue;

                    // Resolve Energy Tint
                    float energyTint = 0.5f; // Neutral 5
                    if (energyList != null && cuesList != null)
                    {
                        double sec = (i / (double)samples) * duration;
                        int segmentIdx = 0;
                        for (int j = 0; j < cuesList.Count; j++)
                        {
                            if (sec >= cuesList[j].Timestamp) segmentIdx = j;
                            else break;
                        }
                        if (segmentIdx < energyList.Count) energyTint = energyList[segmentIdx] / 10.0f;
                    }

                    // Intensity-based blending
                    double l = low[i] / 255.0;
                    double m = midB[i] / 255.0;
                    double hf = high[i] / 255.0;
                    double total = l + m + hf;

                    if (total > 0)
                    {
                        byte r = (byte)Math.Clamp((l * lowColor.R + m * midColor.R + hf * highColor.R) / total, 0, 255);
                        byte g = (byte)Math.Clamp((l * lowColor.G + m * midColor.G + hf * highColor.G) / total, 0, 255);
                        byte b = (byte)Math.Clamp((l * lowColor.B + m * midColor.B + hf * highColor.B) / total, 0, 255);
                        
                        // Apply Energy Temperature (MIK Parity: Blue -> Green -> Yellow -> Red)
                        // This uses a spectral shift based on energyTint (0.0 - 1.0)
                        float t = energyTint;
                        byte targetR = (byte)(t < 0.5f ? 0 : Math.Clamp((t - 0.5f) * 2 * 255, 0, 255));
                        byte targetG = (byte)Math.Clamp((1.0f - Math.Abs(t - 0.5f) * 2) * 255, 0, 255);
                        byte targetB = (byte)(t > 0.5f ? 0 : Math.Clamp((0.5f - t) * 2 * 255, 0, 255));

                        // Blend the spectral tint into the frequency-based color
                        r = (byte)Math.Clamp((r * 0.7f) + (targetR * 0.3f), 0, 255);
                        g = (byte)Math.Clamp((g * 0.7f) + (targetG * 0.3f), 0, 255);
                        b = (byte)Math.Clamp((b * 0.7f) + (targetB * 0.3f), 0, 255);

                        bool isPlayed = isRolling ? (i <= playedLimit) : isActive;
                        float opacity = isPlayed ? 1.0f : 0.35f;
                    
                    var col = Color.FromArgb((byte)(opacity * 255), r, g, b);
                    context.DrawLine(new Pen(new SolidColorBrush(col), 1), new Point(x, mid - h), new Point(x, mid + h));
                }
            }
        }
        private void DrawBandBatch(DrawingContext context, byte[] data, int samples, double step, double mid, int playedLimit, Pen basePen, Pen playedPen)
        {
            var baseGeom = new StreamGeometry();
            using (var ctx = baseGeom.Open())
            {
                for (int i = playedLimit; i < Math.Min(samples, data.Length); i++)
                {
                    double h = (data[i] / 255.0) * mid;
                    if (h < 0.5) continue;
                    double x = i * step;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, basePen, baseGeom);

            var playedGeom = new StreamGeometry();
            using (var ctx = playedGeom.Open())
            {
                for (int i = 0; i < Math.Min(playedLimit, data.Length); i++)
                {
                    double h = (data[i] / 255.0) * mid;
                    if (h < 0.5) continue;
                    double x = i * step;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, playedPen, playedGeom);
        }

        private void RenderRolling(DrawingContext context, WaveformAnalysisData data, double width, double height, double mid)
        {
            double windowSec = 10.0;
            double pixelsPerSec = width / windowSec;
            double currentSec = Progress * data.DurationSeconds;
            double startSec = currentSec - (windowSec / 2);
            
            int samplesPerSec = (int)(data.PeakData!.Length / data.DurationSeconds);
            int startIdx = (int)(startSec * samplesPerSec);
            double startX = (width / 2) + ( (startSec - currentSec) * pixelsPerSec );

            var lowData = LowBand ?? data.LowData;
            var midData = MidBand ?? data.MidData;
            var highData = HighBand ?? data.HighData;
            bool hasRgb = lowData != null && midData != null && highData != null && lowData.Length > 0;

            if (hasRgb)
            {
                // step = pixels per sample. 
                // pixelsPerSec = width / 10.0
                // samplesPerSec = total_samples / duration
                // step = pixelsPerSec / samplesPerSec
                double step = pixelsPerSec / samplesPerSec;
                RenderTrueRgb(context, data, width, height, mid, data.PeakData.Length, step, lowData!, midData!, highData!, true, (width / 2) - (currentSec * pixelsPerSec), true);
            }
            else
            {
                // Fallback to static blue if no RGB
                int endIdx = startIdx + (int)(windowSec * samplesPerSec);
                int playedLimit = (int)(Progress * data.PeakData.Length);
                var playedGeom = new StreamGeometry();
                var baseGeom = new StreamGeometry();
                using (var pCtx = playedGeom.Open())
                using (var bCtx = baseGeom.Open())
                {
                    for (int i = startIdx; i <= endIdx; i++)
                    {
                        if (i < 0 || i >= data.PeakData.Length) continue;
                        double sampleSec = (double)i / samplesPerSec;
                        double x = (width / 2) + (sampleSec - currentSec) * pixelsPerSec;
                        double h = (data.PeakData[i] / 255.0) * mid;
                        var ctx = i <= playedLimit ? pCtx : bCtx;
                        ctx.BeginFigure(new Point(x, mid - h), false);
                        ctx.LineTo(new Point(x, mid + h));
                    }
                }
                context.DrawGeometry(null, StaticPlayedPen, playedGeom);
                context.DrawGeometry(null, StaticBasePen, baseGeom);
            }
        }

        private void RenderPhraseSegments(DrawingContext context, double width, double height)
        {
            var segments = PhraseSegments;
            var data = WaveformData;
            if (segments == null || data == null || data.DurationSeconds <= 0) return;

            var sorted = System.Linq.Enumerable.OrderBy(segments, s => s.Start).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                var s = sorted[i];
                double x = (s.Start / data.DurationSeconds) * width;
                double nextX = width;
                
                if (i < sorted.Count - 1)
                {
                    nextX = (sorted[i + 1].Start / data.DurationSeconds) * width;
                }

                if (x >= width || nextX <= 0) continue;

                var color = Color.Parse(s.Color ?? "#444444");
                var brush = new SolidColorBrush(color, 0.15f); // Semi-transparent block
                
                context.DrawRectangle(brush, null, new Rect(x, 0, Math.Max(0, nextX - x), height));
                
                // Draw Handles (Phase 2)
                if (IsEditing)
                {
                    var handleBrush = new SolidColorBrush(color, 0.8f);
                    var handlePen = new Pen(handleBrush, 2);
                    
                    // Start Handle
                    context.DrawRectangle(handleBrush, null, new Rect(x - HandleWidth/2, 0, HandleWidth, 15));
                    context.DrawLine(handlePen, new Point(x, 15), new Point(x, height));
                    
                    // End Handle
                    context.DrawRectangle(handleBrush, null, new Rect(nextX - HandleWidth/2, height - 15, HandleWidth, 15));
                    context.DrawLine(handlePen, new Point(nextX, 0), new Point(nextX, height - 15));
                }

                // Draw Label at the bottom
                var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
                var formattedText = new FormattedText(s.Label.ToUpper(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9, new SolidColorBrush(color, 0.6f));
                context.DrawText(formattedText, new Point(x + 4, height - formattedText.Height - 4));
            }
        }

        private void RenderCurves(DrawingContext context, double width, double height)
        {
            var energy = EnergyCurve;
            var vocals = VocalDensityCurve;
            
            if (energy == null && vocals == null) return;

            // Draw Energy Curve (Yellow glow)
            if (energy != null)
            {
                RenderCurve(context, energy, width, height, Color.FromRgb(255, 255, 0), 0.6f);
            }

            // Draw Vocal Curve (Purple glow)
            if (vocals != null)
            {
                RenderCurve(context, vocals, width, height, Color.FromRgb(189, 16, 224), 0.5f);
            }
        }

        private void RenderCurve(DrawingContext context, System.Collections.Generic.IEnumerable<float> points, double width, double height, Color color, float opacity)
        {
            var list = System.Linq.Enumerable.ToList(points);
            if (list.Count < 2) return;

            double step = width / (list.Count - 1);
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(0, height - (list[0] * height)), false);
                for (int i = 1; i < list.Count; i++)
                {
                    ctx.LineTo(new Point(i * step, height - (list[i] * height)));
                }
            }

            var brush = new SolidColorBrush(color, opacity);
            context.DrawGeometry(null, new Pen(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geom);
        }

        private void RenderCues(DrawingContext context, double width, double height)
        {
            var cues = Cues;
            var data = WaveformData;
            if (cues == null || data == null || data.DurationSeconds <= 0) return;

            foreach (var cue in cues)
            {
                double x = GetCueX(cue, data);
                if (x < 0 || x > width) continue;

                var color = Color.Parse(cue.Color ?? "#FFFFFF");
                context.DrawLine(new Pen(new SolidColorBrush(color, 0.8), 2), new Point(x, 0), new Point(x, height));
                
                // FormattedText is still a bit expensive but only for cues
                var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
                var formattedText = new FormattedText(cue.Name ?? cue.Role.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, new SolidColorBrush(color));
                context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.6), null, new Rect(x + 4, 2, formattedText.Width + 4, formattedText.Height));
                context.DrawText(formattedText, new Point(x + 6, 2));
            }
        }
    }

    public class VocalGhostDrawOperation : ICustomDrawOperation
    {
        public Rect Bounds { get; }
        private readonly System.Collections.Generic.List<float> _vocalData;
        private readonly double _duration;
        private readonly float _progress;
        private readonly float _opacity;
        private readonly bool _isRolling;
        private readonly double _zoom;
        private readonly double _offset;

        public VocalGhostDrawOperation(Rect bounds, System.Collections.Generic.List<float> vocalData, double duration, float progress, float opacity, bool isRolling, double zoom, double offset)
        {
            Bounds = bounds;
            _vocalData = vocalData;
            _duration = duration;
            _progress = progress;
            _opacity = opacity;
            _isRolling = isRolling;
            _zoom = zoom;
            _offset = offset;
        }

        public void Dispose() { }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLease>();
            if (lease == null) return;

            using var canvas = lease.SkCanvas;
            canvas.Save();
            
            var width = (float)Bounds.Width;
            var height = (float)Bounds.Height;
            var samples = _vocalData.Count;
            
            // Neon Purple (#B450FF) -> SKColor
            var baseColor = new SKColor(180, 80, 255, (byte)(_opacity * 255)); 

            using var paint = new SKPaint
            {
                Color = baseColor,
                BlendMode = SKBlendMode.Screen,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            
            // Create Gradient Shader (Vertical)
            var colors = new SKColor[] { SKColors.Transparent, baseColor, SKColors.Transparent };
            var pos = new float[] { 0.0f, 0.5f, 1.0f };
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                colors,
                pos,
                SKShaderTileMode.Clamp);
            
            paint.Shader = shader;

            double duration = _duration > 0 ? _duration : 1.0;
            
            // Visible Range
            double visibleDuration = duration / _zoom;
            double startTime = _offset * duration;
            double endTime = startTime + visibleDuration;
            
            int startIndex = (int)((startTime / duration) * samples);
            int endIndex = (int)((endTime / duration) * samples);
            
            startIndex = Math.Clamp(startIndex, 0, samples - 1);
            endIndex = Math.Clamp(endIndex, 0, samples - 1);
            
            int range = endIndex - startIndex;
            if (range <= 0) { canvas.Restore(); return; }

            // Step size for performance
            int step = Math.Max(1, range / (int)width); 

            float lastX = -1;
            
            for (int i = startIndex; i < endIndex; i += step)
            {
                if (i >= _vocalData.Count) break;
                
                float prob = _vocalData[i]; 
                
                // Logic: InstProb < 0.2 means High Vocal Presence (Vocal Pocket)
                if (prob < 0.2f) 
                {
                    // Map index to X
                    double sampleTime = (i / (double)samples) * duration;
                    double relTime = sampleTime - startTime;
                    double relPos = relTime / visibleDuration;
                    
                    float x = (float)(relPos * width);
                    float w = Math.Max(1.0f, (float)(width / range) * step);
                    
                    if (x > lastX)
                    {
                        canvas.DrawRect(x, 0, w, height, paint);
                        lastX = x + w;
                    }
                }
            }
            
            canvas.Restore();
        }
    }
}
