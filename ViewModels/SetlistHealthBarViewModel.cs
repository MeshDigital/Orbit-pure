using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.ViewModels
{
    /// <summary>
    /// ViewModel for the SetlistHealthBar control.
    /// Manages reactive state for each transition segment and overall health display.
    /// Coordinates with ForensicInspectorViewModel when user clicks segments.
    /// </summary>
    public class SetlistHealthBarViewModel : ReactiveObject
    {
        private StressDiagnosticReport? _report;
        private int _selectedSegmentIndex = -1;
        private bool _isLoading;
        private string _statusMessage = "Load a setlist to analyze...";

        /// <summary>
        /// Immutable snapshot of the latest stress-test report.
        /// </summary>
        public StressDiagnosticReport? Report
        {
            get => _report;
            set 
            {
                if (_report != value)
                {
                    _report = value;
                    this.RaisePropertyChanged(nameof(Report));
                    this.RaisePropertyChanged(nameof(OverallHealthPercent));
                }
            }
        }

        /// <summary>
        /// Observable collection of segment view models for the HealthBar.
        /// Each segment represents one transition (Track[i] → Track[i+1]).
        /// </summary>
        public ObservableCollection<HealthBarSegment> TransitionSegments { get; }
            = new ObservableCollection<HealthBarSegment>();

        /// <summary>
        /// Index of currently selected segment (-1 if none).
        /// Clicking a segment sets this property and triggers inspector update.
        /// </summary>
        public int SelectedSegmentIndex
        {
            get => _selectedSegmentIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedSegmentIndex, value);
        }

        /// <summary>
        /// True while stress-test is running in background.
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        /// <summary>
        /// Status message for UI display (loading, error, summary).
        /// </summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        /// <summary>
        /// Command to refresh the stress-test (re-run diagnostic on current setlist).
        /// </summary>
        public ReactiveCommand<Unit, StressDiagnosticReport> RefreshDiagnosticCommand { get; }

        /// <summary>
        /// Command triggered when user clicks a segment on the HealthBar.
        /// Publishes the selected stress point for detail display in Forensic Inspector.
        /// </summary>
        public ReactiveCommand<int, TransitionStressPoint?> SelectSegmentCommand { get; }

        /// <summary>
        /// Event fired when user selects a segment (for external subscribers like Forensic Inspector).
        /// </summary>
        public IObservable<TransitionStressPoint> SegmentSelected { get; }

        public SetlistHealthBarViewModel()
        {
            // RefreshCommand (placeholder - will be wired to actual service in DJCompanionViewModel)
            RefreshDiagnosticCommand = ReactiveCommand.CreateFromTask<Unit, StressDiagnosticReport>(
                async _ =>
                {
                    IsLoading = true;
                    StatusMessage = "Analyzing setlist...";
                    await Task.Delay(100); // Placeholder
                    IsLoading = false;
                    return Report ?? new StressDiagnosticReport();
                });

            // SelectSegmentCommand
            SelectSegmentCommand = ReactiveCommand.Create<int, TransitionStressPoint?>(idx =>
            {
                SelectedSegmentIndex = idx;
                if (Report?.StressPoints != null && idx >= 0 && idx < Report.StressPoints.Count)
                {
                    return Report.StressPoints[idx];
                }
                return null;
            });

            // SegmentSelected observable
            SegmentSelected = SelectSegmentCommand.Where(sp => sp != null).Select(sp => sp!);
        }

        /// <summary>
        /// Updates HealthBar with new stress-test report.
        /// Populates TransitionSegments collection from report's StressPoints.
        /// </summary>
        public void UpdateReport(StressDiagnosticReport report)
        {
            Report = report;

            TransitionSegments.Clear();

            if (report?.StressPoints == null || report.StressPoints.Count == 0)
            {
                StatusMessage = report?.QuickSummary ?? "No transitions to display.";
                return;
            }

            // Create segments
            for (int i = 0; i < report.StressPoints.Count; i++)
            {
                var stressPoint = report.StressPoints[i];
                var segment = new HealthBarSegment
                {
                    Index = i,
                    SeverityScore = stressPoint.SeverityScore,
                    SeverityLevel = stressPoint.SeverityLevel,
                    PrimaryProblem = stressPoint.PrimaryProblem,
                    SegmentWidth = 1.0 / report.StressPoints.Count, // Equal width distribution
                    ToolTipText = BuildSegmentTooltip(stressPoint)
                };

                TransitionSegments.Add(segment);
            }

            StatusMessage = report.QuickSummary;
            SelectedSegmentIndex = -1; // Clear selection on new report
        }

        /// <summary>
        /// Updates HealthBar with new report and animates affected segments.
        /// Used after ApplyRescueTrack to show visual feedback (Red → Green transition).
        /// </summary>
        public async Task UpdateReportWithAnimation(StressDiagnosticReport report, int affectedSegmentIndex)
        {
            // Update core report
            UpdateReport(report);

            // Animate the affected segment (pulse or color transition)
            if (affectedSegmentIndex >= 0 && affectedSegmentIndex < TransitionSegments.Count)
            {
                var segment = TransitionSegments[affectedSegmentIndex];

                // Visual feedback: Flash the segment briefly
                var originalBrush = segment.BackgroundColorBrush;

                // Quick flash sequence (200ms total)
                segment.SeverityScore = Math.Max(0, segment.SeverityScore - 20); // Lighter immediately
                await Task.Delay(100);
                segment.SeverityScore = Math.Min(100, segment.SeverityScore + 10); // Settle
                await Task.Delay(100);
            }

            StatusMessage = report.QuickSummary;
        }

        /// <summary>
        /// Builds tooltip text for a segment showing problem and impact.
        /// </summary>
        private string BuildSegmentTooltip(TransitionStressPoint stressPoint)
        {
            var lines = new System.Collections.Generic.List<string>
            {
                $"Transition {stressPoint.FromTrackIndex} → {stressPoint.ToTrackIndex}",
                "",
                $"Problem: {stressPoint.PrimaryProblem}",
                $"Severity: {stressPoint.SeverityScore}%"
            };

            if (stressPoint.RescueSuggestions?.Count > 0)
            {
                lines.Add("");
                lines.Add("💡 Rescue suggestion available (click to view)");
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Gets overall health percentage (inverse of average severity).
        /// </summary>
        public int OverallHealthPercent => Report?.OverallHealthScore ?? 0;

        /// <summary>
        /// Gets color for a severity level (for binding or direct access).
        /// </summary>
        public static string GetSeverityColor(StressSeverity severity)
        {
            return severity switch
            {
                StressSeverity.Healthy => "#22dd22",   // Green
                StressSeverity.Warning => "#ffcc00",   // Yellow
                StressSeverity.Critical => "#ff3333",  // Red
                _ => "#666666"                          // Gray
            };
        }
    }

    /// <summary>
    /// View model for a single segment on the HealthBar.
    /// Represents one transition with severity coloring and metadata.
    /// </summary>
    public class HealthBarSegment : ReactiveObject
    {
        private int _index;
        private int _severityScore;
        private StressSeverity _severityLevel;
        private string _primaryProblem = string.Empty;
        private double _segmentWidth;
        private string _toolTipText = string.Empty;

        public int Index
        {
            get => _index;
            set => this.RaiseAndSetIfChanged(ref _index, value);
        }

        public int SeverityScore
        {
            get => _severityScore;
            set => this.RaiseAndSetIfChanged(ref _severityScore, value);
        }

        public StressSeverity SeverityLevel
        {
            get => _severityLevel;
            set => this.RaiseAndSetIfChanged(ref _severityLevel, value);
        }

        public string PrimaryProblem
        {
            get => _primaryProblem;
            set => this.RaiseAndSetIfChanged(ref _primaryProblem, value);
        }

        /// <summary>
        /// Proportional width of segment (0-1.0).
        /// </summary>
        public double SegmentWidth
        {
            get => _segmentWidth;
            set => this.RaiseAndSetIfChanged(ref _segmentWidth, value);
        }

        public string ToolTipText
        {
            get => _toolTipText;
            set => this.RaiseAndSetIfChanged(ref _toolTipText, value);
        }

        /// <summary>
        /// Gets Avalonia color brush name for background based on severity.
        /// </summary>
        public string BackgroundColorBrush
        {
            get
            {
                return SeverityLevel switch
                {
                    StressSeverity.Healthy => "#22dd22",
                    StressSeverity.Warning => "#ffcc00",
                    StressSeverity.Critical => "#ff3333",
                    _ => "#999999"
                };
            }
        }
    }
}
