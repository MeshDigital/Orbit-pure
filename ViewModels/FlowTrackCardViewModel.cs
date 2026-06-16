using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using Avalonia.Media;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Transition bridge data shown between two adjacent cards on the Flow Builder timeline.
/// </summary>
public sealed class FlowBridgeInfo
{
    /// <summary>e.g. "+3.0 BPM" or "-2.5 BPM"</summary>
    public string BpmDeltaDisplay { get; init; } = string.Empty;
    /// <summary>Colour-coded bar brush: green (compatible) -> yellow -> red (clash).</summary>
    public IBrush ScoreBrush { get; init; } = Brushes.Gray;
    public string QualityLabel { get; init; } = string.Empty;
    /// <summary>Compatibility label, e.g. "5A -> 6A  Compatible"</summary>
    public string KeyLabel { get; init; } = string.Empty;
    public string BreakdownDisplay { get; init; } = string.Empty;
    public string ReasonSummary { get; init; } = string.Empty;
    public TransitionStyle? TransitionStyle { get; init; }
    public string TransitionStyleLabel { get; init; } = string.Empty;
    public string TransitionStyleReason { get; init; } = string.Empty;
    public string Tooltip  { get; init; } = string.Empty;
}

/// <summary>
/// ViewModel for a single track card in the Flow Builder horizontal timeline.
/// Exposes all properties bound by the DataTemplate in <c>FlowBuilderView.axaml</c>.
/// </summary>
public sealed class FlowTrackCardViewModel : ReactiveObject
{
    // -- Identity --------------------------------------------------------------

    public string  Artist          { get; }
    public string  Title           { get; }
    public string  BpmDisplay      { get; }
    public string  KeyDisplay      { get; }
    public string  DurationDisplay { get; }
    public string? AlbumArtUrl     { get; }
    public IBrush  KeyColorBrush   { get; }

    /// <summary>The file path, used to load this track onto a deck.</summary>
    public string FilePath  { get; }

    /// <summary>Unique track hash - used for DB lookups (analysis, cues).</summary>
    public string TrackHash { get; }
    public PlaylistTrack Model { get; }

    // -- Energy sparkline ------------------------------------------------------

    /// <summary>
    /// Normalised energy points (0..1) for the SparklineControl.
    /// Derived from <see cref="AudioFeaturesEntity.SegmentedEnergyJson"/> (8 points) or
    /// a flat line at the track's EnergyScore if segmented data is unavailable.
    /// </summary>
    public IReadOnlyList<double> EnergyCurvePoints { get; }

    // -- Transition bridge -----------------------------------------------------

    /// <summary>True when there is a next track, bridge data exists, and the current style filter keeps it visible.</summary>
    public bool HasBridge => Bridge != null && IsBridgeVisibleByFilter;

    private bool _isBridgeVisibleByFilter = true;
    public bool IsBridgeVisibleByFilter
    {
        get => _isBridgeVisibleByFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBridgeVisibleByFilter, value);
            this.RaisePropertyChanged(nameof(HasBridge));
        }
    }

    private FlowBridgeInfo? _bridge;
    public FlowBridgeInfo? Bridge
    {
        get => _bridge;
        set
        {
            this.RaiseAndSetIfChanged(ref _bridge, value);
            this.RaisePropertyChanged(nameof(HasBridge));
            this.RaisePropertyChanged(nameof(PrimaryTransitionReason));
            this.RaisePropertyChanged(nameof(HasPrimaryTransitionReason));
        }
    }

    public string PrimaryTransitionReason => Bridge?.ReasonSummary ?? string.Empty;
    public bool HasPrimaryTransitionReason => !string.IsNullOrWhiteSpace(PrimaryTransitionReason);

    // -- Commands --------------------------------------------------------------

    public ReactiveCommand<Unit, Unit> MoveLeftCommand  { get; }
    public ReactiveCommand<Unit, Unit> MoveRightCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand    { get; }
    public ReactiveCommand<Unit, Unit> FindBridgeToNextCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectTransitionInspectorCommand { get; }

    // -- Constructor -----------------------------------------------------------

    public FlowTrackCardViewModel(
        PlaylistTrack track,
        Action onMoveLeft,
        Action onMoveRight,
        Action onRemove,
        Action<string>? onFindBridgeToNext = null,
        Action<string>? onSelectTransitionInspector = null)
    {
        Model           = track;
        Artist          = track.Artist  ?? "Unknown Artist";
        Title           = track.Title   ?? "Unknown Title";
        FilePath        = track.ResolvedFilePath ?? string.Empty;
        TrackHash       = track.TrackUniqueHash  ?? string.Empty;
        AlbumArtUrl     = track.AlbumArtUrl;

        BpmDisplay      = track.BPM.HasValue
            ? track.BPM.Value.ToString("F1")
            : "-";
        // Key may be in Camelot notation (e.g. "8A") or musical (e.g. "Am")
        KeyDisplay      = string.IsNullOrEmpty(track.Key) ? "-" : track.Key;
        KeyColorBrush   = GetKeyColorBrush(KeyDisplay);
        DurationDisplay = track.CanonicalDuration.HasValue
            ? FormatDuration(track.CanonicalDuration.Value)
            : "-";

        EnergyCurvePoints = BuildEnergyCurve(track);

        MoveLeftCommand  = ReactiveCommand.Create(onMoveLeft);
        MoveRightCommand = ReactiveCommand.Create(onMoveRight);
        RemoveCommand    = ReactiveCommand.Create(onRemove);
        FindBridgeToNextCommand = ReactiveCommand.Create(() => onFindBridgeToNext?.Invoke(TrackHash));
        SelectTransitionInspectorCommand = ReactiveCommand.Create(() => onSelectTransitionInspector?.Invoke(TrackHash));
    }

    // -- Helpers ---------------------------------------------------------------

    private static IReadOnlyList<double> BuildEnergyCurve(PlaylistTrack track)
    {
        // Use WaveformData (raw byte RMS) if available to produce 8 energy points.
        if (track.WaveformData is { Length: > 0 })
        {
            byte[] src = track.WaveformData;
            int step = Math.Max(1, src.Length / 8);
            var pts = new double[8];
            for (int i = 0; i < 8; i++)
            {
                int start = i * step;
                int end   = Math.Min(start + step, src.Length);
                double sum = 0;
                for (int j = start; j < end; j++) sum += src[j];
                pts[i] = (end > start) ? sum / ((end - start) * 255.0) : 0;
            }
            return pts;
        }

        const double flat = 0.5;
        return new[] { flat, flat, flat, flat, flat, flat, flat, flat };
    }

    private static string FormatDuration(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    // -- Bridge scoring --------------------------------------------------------

    /// <summary>
    /// Computes and sets the transition bridge to the <paramref name="next"/> card.
    /// Uses harmonic distance, tempo drift, and local outro->intro energy alignment.
    /// </summary>
    public void SetBridgeTo(
        FlowTrackCardViewModel? next,
        double? sectionBlendScore = null,
        double? doubleDropScore = null,
        PlaylistRecommendation? recommendation = null,
        TransitionStyleResult? transitionStyle = null)
    {
        if (next == null) { Bridge = null; return; }

        float myBpm   = float.TryParse(BpmDisplay, out var b1) ? b1 : 0f;
        float nxtBpm  = float.TryParse(next.BpmDisplay, out var b2) ? b2 : 0f;
        float bpmDiff = myBpm > 0 && nxtBpm > 0 ? nxtBpm - myBpm : 0f;
        string bpmDeltaDisplay = bpmDiff == 0f ? "-"
            : bpmDiff > 0 ? $"+{bpmDiff:F1} BPM"
            : $"{bpmDiff:F1} BPM";

        double camelotDist = CamelotDistance(KeyDisplay, next.KeyDisplay);
        double harmonicScore = Math.Clamp(1.0 - (camelotDist / 6.0), 0.0, 1.0);
        double tempoScore = myBpm > 0 && nxtBpm > 0
            ? Math.Clamp(Math.Exp(-Math.Abs(bpmDiff) / 4.0), 0.0, 1.0)
            : 0.5;

        double outroEnergy = EnergyCurvePoints.Count > 0
            ? EnergyCurvePoints.Skip(Math.Max(0, EnergyCurvePoints.Count - 2)).Average()
            : 0.5;
        double introEnergy = next.EnergyCurvePoints.Count > 0
            ? next.EnergyCurvePoints.Take(2).Average()
            : 0.5;
        double energyMatch = Math.Clamp(1.0 - Math.Abs(outroEnergy - introEnergy), 0.0, 1.0);

        double sectionMatch = sectionBlendScore ?? energyMatch;

        double transitionScore = recommendation?.Score ?? Math.Clamp(
            (harmonicScore * 0.35) +
            (tempoScore * 0.15) +
            (energyMatch * 0.20) +
            (sectionMatch * 0.30),
            0.0, 1.0);

        IBrush scoreBrush = transitionScore switch
        {
            >= 0.82 => new SolidColorBrush(Color.Parse("#4EC9B0")),
            >= 0.64 => new SolidColorBrush(Color.Parse("#B8E986")),
            >= 0.45 => new SolidColorBrush(Color.Parse("#FFB347")),
            _       => new SolidColorBrush(Color.Parse("#E74C3C")),
        };

        string compatLabel = doubleDropScore is >= 0.78
            ? "Double-drop friendly"
            : transitionScore switch
            {
                >= 0.82 => "Smooth blend",
                >= 0.64 => "Good bridge",
                >= 0.45 => "Usable shift",
                _       => "Risky transition",
            };

        string reasonSummary = recommendation?.ReasonTags.FirstOrDefault() switch
        {
            { Length: > 26 } firstReason => firstReason[..26] + "...",
            { Length: > 0 } firstReason => firstReason,
            _ => compatLabel,
        };

        string breakdownDisplay = recommendation != null
            ? $"S {(recommendation.SimilarityScore * 100):F0}% · H {(recommendation.HarmonicScore * 100):F0}% · E {(recommendation.EnergyFitScore * 100):F0}%"
            : $"Section {(sectionMatch * 100):F0}% · Energy {(energyMatch * 100):F0}%";

        string keyLabel = recommendation != null
            ? $"{compatLabel} · {(transitionScore * 100):F0}%"
            : $"{KeyDisplay} -> {next.KeyDisplay} · {(transitionScore * 100):F0}%";
        string tooltip =
            $"Flow: {(transitionScore * 100):F0}% ({compatLabel})\n" +
            (transitionStyle != null ? $"Style: {transitionStyle.Label}\nReason: {transitionStyle.Reason}\n" : string.Empty) +
            $"Key: {KeyDisplay} -> {next.KeyDisplay}\n" +
            $"BPM: {BpmDisplay} -> {next.BpmDisplay} ({bpmDeltaDisplay})\n" +
            $"Outro->Intro section match: {(sectionMatch * 100):F0}%\n" +
            $"Energy contour match: {(energyMatch * 100):F0}%" +
            (doubleDropScore.HasValue ? $"\nDrop-to-drop compatibility: {(doubleDropScore.Value * 100):F0}%" : string.Empty) +
            (recommendation != null
                ? $"\nA10 similarity: {(recommendation.SimilarityScore * 100):F0}%\nA10 harmonic: {(recommendation.HarmonicScore * 100):F0}%\nA10 transition: {(recommendation.TransitionScore * 100):F0}%\nA10 energy fit: {(recommendation.EnergyFitScore * 100):F0}%\nReasons: {string.Join(", ", recommendation.ReasonTags)}"
                : string.Empty);

        Bridge = new FlowBridgeInfo
        {
            BpmDeltaDisplay = bpmDeltaDisplay,
            ScoreBrush = scoreBrush,
            QualityLabel = compatLabel,
            KeyLabel = keyLabel,
            BreakdownDisplay = breakdownDisplay,
            ReasonSummary = reasonSummary,
            TransitionStyle = transitionStyle?.Style,
            TransitionStyleLabel = transitionStyle?.Label ?? string.Empty,
            TransitionStyleReason = transitionStyle?.Reason ?? string.Empty,
            Tooltip = tooltip,
        };
    }

    private static IBrush GetKeyColorBrush(string key)
    {
        if (!TryParseCamelot(key, out int n, out bool isMinor))
            return new SolidColorBrush(Color.Parse("#4EC9B0"), 0.55);

        string hex = (isMinor, n) switch
        {
            (true,  1)  => "#008080", (true,  2)  => "#4682B4", (true,  3)  => "#4169E1",
            (true,  4)  => "#4B0082", (true,  5)  => "#9400D3", (true,  6)  => "#C71585",
            (true,  7)  => "#DC143C", (true,  8)  => "#FF8C00", (true,  9)  => "#FFD700",
            (true,  10) => "#9ACD32", (true,  11) => "#3CB371", (true,  12) => "#008B8B",
            (false, 1)  => "#7FFFD4", (false, 2)  => "#87CEEB", (false, 3)  => "#1E90FF",
            (false, 4)  => "#6A5ACD", (false, 5)  => "#DDA0DD", (false, 6)  => "#FF69B4",
            (false, 7)  => "#F08080", (false, 8)  => "#FFA500", (false, 9)  => "#F0E68C",
            (false, 10) => "#98FB98", (false, 11) => "#00FA9A", (false, 12) => "#40E0D0",
            _ => "#4EC9B0",
        };
        return new SolidColorBrush(Color.Parse(hex), 0.70);
    }

    /// <summary>Camelot wheel distance (0 = perfect match, 6 = worst clash).</summary>
    private static double CamelotDistance(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == "-" || b == "-")
            return 3.0;

        if (!TryParseCamelot(a, out int na, out bool minA) ||
            !TryParseCamelot(b, out int nb, out bool minB))
            return 3.0;

        int raw  = Math.Abs(na - nb);
        int dist = Math.Min(raw, 12 - raw);
        return dist + (minA == minB ? 0 : 1);
    }

    private static bool TryParseCamelot(string key, out int number, out bool isMinor)
    {
        number  = 0;
        isMinor = false;
        if (key.Length < 2) return false;
        char suffix = char.ToUpperInvariant(key[^1]);
        if (suffix != 'A' && suffix != 'B') return false;
        isMinor = suffix == 'A';
        return int.TryParse(key[..^1], out number) && number >= 1 && number <= 12;
    }
}
