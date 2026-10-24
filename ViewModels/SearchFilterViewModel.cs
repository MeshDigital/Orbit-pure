using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Client-side filters for the ad-hoc Search page. Every filter is applied reactively to the
/// cached result set (no new network search), so the one filter strip on the page can be
/// toggled freely. One evaluation routine (<see cref="Evaluate"/>) backs the predicate, the
/// "why is this hidden" reason and <see cref="IsMatch"/> so they can never disagree.
/// </summary>
public class SearchFilterViewModel : ReactiveObject
{
    public const int DefaultMinBitrate = 320;
    private const int ShortQueueLimit = 50;

    /// <summary>Choices offered by the "Min bitrate" dropdown (0 = any).</summary>
    public static IReadOnlyList<int> MinBitrateOptions { get; } = new[] { 0, 128, 192, 256, 320 };

    private int _minBitrate = DefaultMinBitrate;
    /// <summary>Minimum bitrate in kbps; 0 means any. Used for the network request as well.</summary>
    public int MinBitrate
    {
        get => _minBitrate;
        set => this.RaiseAndSetIfChanged(ref _minBitrate, Math.Max(0, value));
    }

    // Formats
    /// <summary>Format chips shown by default: everything ORBIT imports from Soulseek's common formats.</summary>
    private static readonly string[] DefaultFormats = { "MP3", "FLAC", "WAV", "AIFF" };

    public ObservableCollection<string> SelectedFormats { get; } = new(DefaultFormats);

    private bool _useHighReliability;
    /// <summary>"Short queue" chip: hide peers with more than 50 files queued.</summary>
    public bool UseHighReliability
    {
        get => _useHighReliability;
        set => this.RaiseAndSetIfChanged(ref _useHighReliability, value);
    }

    private bool _hideSuspects = true;
    /// <summary>"Hide suspects" chip: hide flagged files and files whose size doesn't fit their bitrate.</summary>
    public bool HideSuspects
    {
        get => _hideSuspects;
        set => this.RaiseAndSetIfChanged(ref _hideSuspects, value);
    }

    private BouncerMode _bouncerMode = BouncerMode.Standard;
    /// <summary>Only <see cref="BouncerMode.Strict"/> changes filtering (320 kbps+ when known).
    /// Standard and Relaxed are equivalent here, so the UI exposes it as one "Strict" chip.</summary>
    public BouncerMode BouncerMode
    {
        get => _bouncerMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _bouncerMode, value);
            this.RaisePropertyChanged(nameof(IsStrict));
        }
    }

    public bool IsStrict
    {
        get => BouncerMode == BouncerMode.Strict;
        set => BouncerMode = value ? BouncerMode.Strict : BouncerMode.Standard;
    }

    /// <summary>Runs a batch of filter changes (e.g. from query-token parsing).</summary>
    public void SetFromQueryParsing(Action action) => action();

    public bool FilterMp3
    {
        get => SelectedFormats.Contains("MP3");
        set => ToggleFormat("MP3", value);
    }

    public bool FilterFlac
    {
        get => SelectedFormats.Contains("FLAC");
        set => ToggleFormat("FLAC", value);
    }

    public bool FilterWav
    {
        get => SelectedFormats.Contains("WAV");
        set => ToggleFormat("WAV", value);
    }

    /// <summary>AIFF chip: .aif, .aiff and .aifc results (were hidden as "Format filtered out (AIF)").</summary>
    public bool FilterAiff
    {
        get => SelectedFormats.Contains("AIFF");
        set => ToggleFormat("AIFF", value);
    }

    /// <summary>Number of filters that differ from the defaults (drives the conditional Clear).</summary>
    public int ActiveFilterCount
    {
        get
        {
            var count = 0;
            if (SelectedFormats.Count != DefaultFormats.Length) count++;
            if (MinBitrate != DefaultMinBitrate) count++;
            if (IsStrict) count++;
            if (UseHighReliability) count++;
            if (!HideSuspects) count++;
            return count;
        }
    }

    public bool HasActiveFilters => ActiveFilterCount > 0;

    public SearchFilterViewModel()
    {
        SelectedFormats.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(FilterMp3));
            this.RaisePropertyChanged(nameof(FilterFlac));
            this.RaisePropertyChanged(nameof(FilterWav));
            this.RaisePropertyChanged(nameof(FilterAiff));
            RaiseActiveFilterChanged();
        };

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MinBitrate) or nameof(UseHighReliability)
                or nameof(HideSuspects) or nameof(BouncerMode))
            {
                RaiseActiveFilterChanged();
            }
        };
    }

    private void RaiseActiveFilterChanged()
    {
        this.RaisePropertyChanged(nameof(ActiveFilterCount));
        this.RaisePropertyChanged(nameof(HasActiveFilters));
    }

    private void ToggleFormat(string format, bool isSelected)
    {
        if (isSelected && !SelectedFormats.Contains(format))
            SelectedFormats.Add(format);
        else if (!isSelected && SelectedFormats.Contains(format))
            SelectedFormats.Remove(format);
    }

    public IObservable<Func<SearchResult, bool>> FilterChanged =>
        this.WhenAnyValue(
            x => x.MinBitrate,
            x => x.UseHighReliability,
            x => x.HideSuspects,
            x => x.BouncerMode)
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
            .Select(_ => System.Reactive.Unit.Default)
            .Merge(
                Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => SelectedFormats.CollectionChanged += h,
                    h => SelectedFormats.CollectionChanged -= h)
                .Select(_ => System.Reactive.Unit.Default))
            .Select(_ => GetFilterPredicate());

    private sealed record Snapshot(int MinBitrate, HashSet<string> Formats, bool HighReliability, bool HideSuspects, BouncerMode Bouncer);

    private Snapshot TakeSnapshot() => new(
        MinBitrate,
        SelectedFormats.Select(f => f.ToUpperInvariant()).ToHashSet(),
        UseHighReliability,
        HideSuspects,
        BouncerMode);

    /// <summary>VBR buckets: asking for 320 lets V0 (~240+) through, 256 allows V1, 192 allows V2.</summary>
    internal static int EffectiveMinBitrate(int minBitrate) => minBitrate switch
    {
        >= 320 => 240,
        >= 256 => 220,
        >= 192 => 180,
        _ => minBitrate
    };

    /// <summary>Returns null when the result passes, else a human-readable reason.</summary>
    private static string? Evaluate(SearchResult result, Snapshot s)
    {
        if (result.Model == null)
            return "Invalid result";

        // Bitrate == 0 means "not reported" — most Soulseek results; never hide those on bitrate.
        if (s.Bouncer == BouncerMode.Strict && result.Bitrate > 0 && result.Bitrate < 320)
            return $"Strict mode requires 320 kbps+, got {result.Bitrate} kbps";

        var effectiveMin = EffectiveMinBitrate(s.MinBitrate);
        if (result.Bitrate > 0 && result.Bitrate < effectiveMin)
            return $"Bitrate below filter floor ({effectiveMin}kbps effective minimum)";

        // Prefer the filename extension, fall back to the model format; unknown formats pass.
        var ext = System.IO.Path.GetExtension(result.Model.Filename)?.TrimStart('.');
        var formatToken = ChipFormat(!string.IsNullOrWhiteSpace(ext) ? ext : result.Model.Format);
        if (!string.IsNullOrWhiteSpace(formatToken) && !s.Formats.Contains(formatToken))
            return $"Format filtered out ({formatToken})";

        if (s.HighReliability && result.QueueLength > ShortQueueLimit)
            return $"Peer queue too long ({result.QueueLength} files)";

        if (s.HideSuspects)
        {
            if (result.IntegrityStatus == "Suspect")
                return "Hidden by safety filter (suspect or upscaled)";

            // Size gate: ±25% around bitrate × length.
            if (result.Size > 0 && result.Length > 0 && result.Bitrate > 0)
            {
                double expectedBytes = (result.Bitrate * 1000.0 / 8.0) * result.Length!.Value;
                if (result.Size < expectedBytes * 0.75 || result.Size > expectedBytes * 1.25)
                    return "Hidden by forensic size gate";
            }
        }

        return null;
    }

    public Func<SearchResult, bool> GetFilterPredicate()
    {
        var snapshot = TakeSnapshot();
        return result => Evaluate(result, snapshot) == null;
    }

    public string? GetHiddenReason(SearchResult result) => Evaluate(result, TakeSnapshot());

    public bool IsMatch(SearchResult result) => Evaluate(result, TakeSnapshot()) == null;

    /// <summary>Resets bitrate, formats and queue filter (query-token parsing baseline).</summary>
    public void Reset()
    {
        MinBitrate = DefaultMinBitrate;
        UseHighReliability = false;
        ResetFormats();
    }

    /// <summary>Restores every filter on the strip to its default (the "Clear" action).</summary>
    public void ResetToDefaults()
    {
        Reset();
        HideSuspects = true;
        BouncerMode = BouncerMode.Standard;
    }

    /// <summary>The chip a file format belongs to: AIF/AIFC count as AIFF, WAVE as WAV (from the format table).</summary>
    internal static string ChipFormat(string? extensionOrFormat)
    {
        var info = SLSKDONET.Services.Audio.AudioFormats.Find(extensionOrFormat);
        if (info != null) return info.DisplayName.ToUpperInvariant();
        return (extensionOrFormat ?? string.Empty).Trim().TrimStart('.').ToUpperInvariant();
    }

    private void ResetFormats()
    {
        if (SelectedFormats.Count == DefaultFormats.Length && DefaultFormats.All(SelectedFormats.Contains)) return;
        SelectedFormats.Clear();
        foreach (var format in DefaultFormats) SelectedFormats.Add(format);
    }
}
