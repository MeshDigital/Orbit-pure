using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Collections.Specialized;
using ReactiveUI;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Phase 13 AI Layer – Mood-Based Library Filter ViewModel.
///
/// Filters the library's <see cref="PlaylistTrackViewModel"/> collection using the
/// AI-detected mood probabilities produced by the Essentia TensorFlow models
/// (mood_happy, mood_aggressive, mood_sad, mood_relaxed, mood_party, mood_electronic).
///
/// Follows the Glass Box philosophy: filter state is fully observable and the
/// predicate is emitted reactively so the UI can update without polling.
/// </summary>
public class MoodLibraryFilterViewModel : ReactiveObject
{
    // -----------------------------------------------------------------------
    // Available mood labels (mirrors the Phase 13 Essentia model set)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The complete set of moods detectable by the Phase 13 AI layer.
    /// Corresponds to the 'mood_*' TensorFlow models in Tools/Essentia/models/.
    /// </summary>
    public static readonly IReadOnlyList<string> AvailableMoods = new[]
    {
        "Happy",
        "Aggressive",
        "Sad",
        "Relaxed",
        "Party",
        "Electronic",
    };

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    /// <summary>
    /// Moods that must be included in the filtered view.
    /// An empty collection means <b>all moods pass</b> (no mood filter active).
    /// </summary>
    public ObservableCollection<string> SelectedMoods { get; } = new();

    private float _minMoodProbability = 0.5f;

    /// <summary>
    /// Minimum confidence threshold (0.0 – 1.0) for the primary mood tag.
    /// Tracks whose <c>MoodConfidence</c> falls below this value are excluded
    /// when mood filtering is active.
    /// </summary>
    public float MinMoodProbability
    {
        get => _minMoodProbability;
        set => this.RaiseAndSetIfChanged(ref _minMoodProbability,
            Math.Clamp(value, 0f, 1f));
    }

    private bool _includeUnclassified = true;

    /// <summary>
    /// When <c>true</c>, tracks that have no mood data (empty <c>MoodTag</c>) are
    /// still shown. When <c>false</c>, only analysed tracks with a known mood pass.
    /// </summary>
    public bool IncludeUnclassified
    {
        get => _includeUnclassified;
        set => this.RaiseAndSetIfChanged(ref _includeUnclassified, value);
    }

    // -----------------------------------------------------------------------
    // Reactive filter stream
    // -----------------------------------------------------------------------

    /// <summary>
    /// Observable predicate that fires whenever any filter parameter changes.
    /// Consumers (e.g. <c>TrackListViewModel</c>) should subscribe and replace
    /// their active filter whenever a new value arrives.
    /// </summary>
    public IObservable<Func<PlaylistTrackViewModel, bool>> FilterChanged =>
        this.WhenAnyValue(
                x => x.MinMoodProbability,
                x => x.IncludeUnclassified)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Merge(
                Observable
                    .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                        h => SelectedMoods.CollectionChanged += h,
                        h => SelectedMoods.CollectionChanged -= h)
                    .Select(_ => (MinMoodProbability, IncludeUnclassified)))
            .Select(_ => GetFilterPredicate());

    // -----------------------------------------------------------------------
    // Predicate factory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a snapshot predicate based on the current filter state.
    /// Safe to call synchronously; captures current values to avoid closure races.
    /// </summary>
    public Func<PlaylistTrackViewModel, bool> GetFilterPredicate()
    {
        // Capture immutable snapshot so the closure is thread-safe.
        var selectedMoods = SelectedMoods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var minProbability = MinMoodProbability;
        var includeUnclassified = IncludeUnclassified;

        return track =>
        {
            var moodTag = track.MoodTag;
            bool hasClassification = !string.IsNullOrEmpty(moodTag) && moodTag != "Neutral";

            // Tracks with no mood classification
            if (!hasClassification)
                return includeUnclassified;

            // If no mood filter is active, only the probability threshold applies.
            if (selectedMoods.Count > 0 && !selectedMoods.Contains(moodTag!))
                return false;

            // Reject low-confidence predictions so the UI shows only reliable data.
            return track.MoodConfidence >= minProbability;
        };
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Returns <c>true</c> if any mood or threshold filter is currently active.</summary>
    public bool IsFilterActive =>
        SelectedMoods.Count > 0 || !_includeUnclassified || _minMoodProbability > 0.5f;

    /// <summary>Resets all filter state to defaults (no filtering).</summary>
    public void Reset()
    {
        SelectedMoods.Clear();
        MinMoodProbability = 0.5f;
        IncludeUnclassified = true;
    }

    /// <summary>Convenience: toggle a single mood in/out of the selection.</summary>
    public void ToggleMood(string mood)
    {
        if (SelectedMoods.Contains(mood, StringComparer.OrdinalIgnoreCase))
            SelectedMoods.Remove(SelectedMoods.First(m =>
                string.Equals(m, mood, StringComparison.OrdinalIgnoreCase)));
        else
            SelectedMoods.Add(mood);
    }
}
