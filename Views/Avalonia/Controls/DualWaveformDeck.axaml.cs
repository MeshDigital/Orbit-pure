using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services.Audio;
using System;
using System.Linq;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public partial class DualWaveformDeck : UserControl
    {
        private WaveformControl? _macroWaveform;
        private WaveformControl? _microWaveform;
        private Border? _viewportRect;
        private TextBlock? _zoomLabel;
        
        // Define DPs to pass through to internal controls
        public static readonly StyledProperty<WaveformAnalysisData> WaveformDataProperty =
            AvaloniaProperty.Register<DualWaveformDeck, WaveformAnalysisData>(nameof(WaveformData));

        public WaveformAnalysisData WaveformData
        {
            get => GetValue(WaveformDataProperty);
            set => SetValue(WaveformDataProperty, value);
        }

        public static readonly StyledProperty<float> ProgressProperty =
            AvaloniaProperty.Register<DualWaveformDeck, float>(nameof(Progress));

        public float Progress
        {
            get => GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<OrbitCue>?> CuesProperty =
            AvaloniaProperty.Register<DualWaveformDeck, System.Collections.Generic.IEnumerable<OrbitCue>?>(nameof(Cues));

        public System.Collections.Generic.IEnumerable<OrbitCue>? Cues
        {
            get => GetValue(CuesProperty);
            set => SetValue(CuesProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<PhraseSegment>?> PhraseSegmentsProperty =
            AvaloniaProperty.Register<DualWaveformDeck, System.Collections.Generic.IEnumerable<PhraseSegment>?>(nameof(PhraseSegments));

        public System.Collections.Generic.IEnumerable<PhraseSegment>? PhraseSegments
        {
            get => GetValue(PhraseSegmentsProperty);
            set => SetValue(PhraseSegmentsProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<float>?> EnergyCurveProperty =
            AvaloniaProperty.Register<DualWaveformDeck, System.Collections.Generic.IEnumerable<float>?>(nameof(EnergyCurve));

        public System.Collections.Generic.IEnumerable<float>? EnergyCurve
        {
            get => GetValue(EnergyCurveProperty);
            set => SetValue(EnergyCurveProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<int>?> SegmentedEnergyProperty =
            AvaloniaProperty.Register<DualWaveformDeck, System.Collections.Generic.IEnumerable<int>?>(nameof(SegmentedEnergy));

        public System.Collections.Generic.IEnumerable<int>? SegmentedEnergy
        {
            get => GetValue(SegmentedEnergyProperty);
            set => SetValue(SegmentedEnergyProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<float>?> VocalDensityCurveProperty =
            AvaloniaProperty.Register<DualWaveformDeck, System.Collections.Generic.IEnumerable<float>?>(nameof(VocalDensityCurve));

        public System.Collections.Generic.IEnumerable<float>? VocalDensityCurve
        {
            get => GetValue(VocalDensityCurveProperty);
            set => SetValue(VocalDensityCurveProperty, value);
        }

        public static readonly StyledProperty<bool> ShowVocalGhostProperty =
            AvaloniaProperty.Register<DualWaveformDeck, bool>(nameof(ShowVocalGhost));
        public bool ShowVocalGhost { get => GetValue(ShowVocalGhostProperty); set => SetValue(ShowVocalGhostProperty, value); }

        // Commands
        public static readonly StyledProperty<System.Windows.Input.ICommand?> SeekCommandProperty =
            AvaloniaProperty.Register<DualWaveformDeck, System.Windows.Input.ICommand?>(nameof(SeekCommand));
        public System.Windows.Input.ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

        public static readonly StyledProperty<System.Windows.Input.ICommand?> CueClickedCommandProperty =
            AvaloniaProperty.Register<DualWaveformDeck, System.Windows.Input.ICommand?>(nameof(CueClickedCommand));
        public System.Windows.Input.ICommand? CueClickedCommand { get => GetValue(CueClickedCommandProperty); set => SetValue(CueClickedCommandProperty, value); }

        public static readonly StyledProperty<System.Windows.Input.ICommand?> CueUpdatedCommandProperty =
            AvaloniaProperty.Register<DualWaveformDeck, System.Windows.Input.ICommand?>(nameof(CueUpdatedCommand));
        public System.Windows.Input.ICommand? CueUpdatedCommand { get => GetValue(CueUpdatedCommandProperty); set => SetValue(CueUpdatedCommandProperty, value); }

        public static readonly StyledProperty<bool> IsRollingProperty =
            AvaloniaProperty.Register<DualWaveformDeck, bool>(nameof(IsRolling));
        public bool IsRolling { get => GetValue(IsRollingProperty); set => SetValue(IsRollingProperty, value); }
        
        public static readonly StyledProperty<float> BpmProperty =
            AvaloniaProperty.Register<DualWaveformDeck, float>(nameof(Bpm));
        public float Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }



        public DualWaveformDeck()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            
            _macroWaveform = this.FindControl<WaveformControl>("MacroWaveform");
            _microWaveform = this.FindControl<WaveformControl>("MicroWaveform");
            _viewportRect = this.FindControl<Border>("ViewportRect");
            _zoomLabel = this.FindControl<TextBlock>("ZoomLabel");
            
            // Sync logic
            if (_microWaveform != null)
            {
                _microWaveform.PropertyChanged += OnMicroWaveformPropertyChanged;
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            
            if (change.Property == WaveformDataProperty || 
                change.Property == ProgressProperty ||
                change.Property == CuesProperty || 
                change.Property == PhraseSegmentsProperty || 
                change.Property == EnergyCurveProperty ||
                change.Property == SegmentedEnergyProperty ||
                change.Property == VocalDensityCurveProperty)
            {
                // Pass data down to children manually if bindings fail or need complex logic
                // But XAML bindings should handle this.
                // We mainly need to trigger viewport updates if Zoom/Offset changes are internal
            }
        }

        private void OnMicroWaveformPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == WaveformControl.ZoomLevelProperty || 
                e.Property == WaveformControl.ViewOffsetProperty)
            {
                UpdateViewportOverlay();
            }
        }

        private void UpdateViewportOverlay()
        {
            if (_macroWaveform == null || _microWaveform == null || _viewportRect == null) return;
            
            double zoom = _microWaveform.ZoomLevel;
            double offset = _microWaveform.ViewOffset;
            
            // Viewport width is fraction of total width: 1.0 / Zoom
            double viewportFraction = 1.0 / zoom;
            
            // Calculate pixel dimensions relative to Macro View
            // Macro View is Width=Bounds.Width (Auto)
            double totalWidth = _macroWaveform.Bounds.Width;
            if (totalWidth <= 0) return; // Not laid out yet
            
            double rectWidth = totalWidth * viewportFraction;
            double rectX = totalWidth * offset;
            
            // Update UI
            _viewportRect.Width = rectWidth;
            Canvas.SetLeft(_viewportRect, rectX);
            
            if (_zoomLabel != null)
                _zoomLabel.Text = $"{zoom:F1}x";
        }

        // Handle Click on Macro View -> Jump Micro View
        private void OnMacroMapClicked(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
        {
            if (_macroWaveform == null || _microWaveform == null) return;
            
            var point = e.GetPosition(_macroWaveform);
            double clickFraction = point.X / _macroWaveform.Bounds.Width;
            
            // Center the Micro View on this point
            // New Offset = clickFraction - (viewportWindowWidth / 2)
            double zoom = _microWaveform.ZoomLevel;
            double windowSize = 1.0 / zoom;
            double targetedOffset = clickFraction - (windowSize / 2.0);
            
            // Clamp
            targetedOffset = Math.Clamp(targetedOffset, 0.0, Math.Max(0.0, 1.0 - windowSize));
            
            _microWaveform.ViewOffset = targetedOffset;
            UpdateViewportOverlay();
        }

        // Input Handling for Scroll-to-Zoom on Micro View is handled by WaveformControl internally,
        // but we receive the bubbling event.
        private void OnMicroWaveformScroll(object? sender, global::Avalonia.Input.PointerWheelEventArgs e)
        {
             // Handled in WaveformControl, but we might want to ensure viewport updates
             // The PropertyChanged handler should catch it.
        }

        // Layout Updated to ensure overlay is correct on resize
        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            Dispatcher.UIThread.Post(UpdateViewportOverlay);
        }
    }
}
