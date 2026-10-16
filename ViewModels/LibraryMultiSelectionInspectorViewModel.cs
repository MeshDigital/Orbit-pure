using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Inspector content for 3+ selected library tracks: a summary of the selection and the bulk
/// actions that already exist on <see cref="LibraryViewModel"/>. Before this, selecting a range
/// left the inspector showing whichever single track had been clicked first, as if it still
/// applied. Snapshot-based: <see cref="Update"/> is called from the debounced selection refresh.
/// </summary>
public sealed class LibraryMultiSelectionInspectorViewModel : INotifyPropertyChanged
{
    private readonly LibraryViewModel? _library;

    public LibraryMultiSelectionInspectorViewModel(LibraryViewModel? library)
    {
        _library = library;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count { get; private set; }
    public string Title => $"{Count} tracks selected";
    public string DurationDisplay { get; private set; } = "—";
    public string BpmRangeDisplay { get; private set; } = "—";
    public string KeysDisplay { get; private set; } = "—";
    public string AnalysedDisplay { get; private set; } = "—";
    public int MissingCount { get; private set; }
    public bool HasMissing => MissingCount > 0;
    public string MissingDisplay => MissingCount == 1 ? "1 file missing" : $"{MissingCount} files missing";
    public bool HasUnanalysed { get; private set; }

    public ICommand? AnalyseAllCommand => _library?.BatchQueueAnalysisCommand;
    public ICommand? EditTagsCommand => _library?.BatchTagEditCommand;
    public ICommand? AddToPlaylistCommand => _library?.BatchAddToPlaylistCommand;
    public ICommand? AddToQueueCommand => _library?.Operations.AddSelectedToQueueCommand;
    public ICommand? ClearSelectionCommand => _library?.BatchClearSelectionCommand;

    /// <summary>Recomputes the summary from a settled selection.</summary>
    public void Update(IReadOnlyList<PlaylistTrackViewModel> selection)
    {
        var tracks = selection.Where(t => !t.IsPlaceholder).ToList();
        Count = tracks.Count;

        var totalMs = tracks.Sum(t => (long)(t.Model.CanonicalDuration ?? 0));
        DurationDisplay = totalMs > 0 ? FormatDuration(TimeSpan.FromMilliseconds(totalMs)) : "—";

        var bpms = tracks.Where(t => t.BPM > 0).Select(t => t.BPM).ToList();
        BpmRangeDisplay = bpms.Count == 0 ? "—"
            : Math.Abs(bpms.Max() - bpms.Min()) < 0.5 ? $"{bpms.Min():0}"
            : $"{bpms.Min():0}–{bpms.Max():0}";

        var keys = tracks.Select(t => t.CamelotDisplay).Where(k => !string.IsNullOrEmpty(k) && k != "—").Distinct().Count();
        KeysDisplay = keys == 0 ? "—" : keys == 1 ? "1 key" : $"{keys} keys";

        var analysed = tracks.Count(t => t.HasAnalysisData);
        AnalysedDisplay = $"{analysed} / {Count}";
        HasUnanalysed = analysed < Count;
        MissingCount = tracks.Count(t => t.IsGhost);

        foreach (var name in new[]
                 {
                     nameof(Count), nameof(Title), nameof(DurationDisplay), nameof(BpmRangeDisplay), nameof(KeysDisplay),
                     nameof(AnalysedDisplay), nameof(HasUnanalysed), nameof(MissingCount), nameof(HasMissing), nameof(MissingDisplay),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
}
