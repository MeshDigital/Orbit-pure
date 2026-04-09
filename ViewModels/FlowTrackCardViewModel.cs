using System;
using System.Collections.Generic;
using System.Reactive;
using Avalonia.Media;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Transition bridge data shown between two adjacent cards on the Flow Builder timeline.
/// </summary>
public sealed class FlowBridgeInfo
{
    /// <summary>e.g. "+3.0 BPM" or "−2.5 BPM"</summary>
    public string BpmDeltaDisplay { get; init; } = string.Empty;
    /// <summary>Colour-coded bar brush: green (compatible) → yellow → red (clash).</summary>
    public IBrush ScoreBrush { get; init; } = Brushes.Gray;
    /// <summary>Compatibility label, e.g. "5A → 6A  ✓ Compatible"</summary>
    public string KeyLabel { get; init; } = string.Empty;
    public string Tooltip  { get; init; } = string.Empty;
}

/// <summary>
/// ViewModel for a single track card in the Flow Builder horizontal timeline.
/// Exposes all properties bound by the DataTemplate in <c>FlowBuilderView.axaml</c>.
/// </summary>
public sealed class FlowTrackCardViewModel : ReactiveObject
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string Artist          { get; }
    public string Title           { get; }
    public string BpmDisplay      { get; }
    public string KeyDisplay      { get; }
    public string DurationDisplay { get; }

    /// <summary>The file path, used to load this track onto a deck.</summary>
    public string FilePath        { get; }

    /// <summary>Unique track hash — used for DB lookups (analysis, cues).</summary>
    public string TrackHash       { get; }

    // ── Energy sparkline ──────────────────────────────────────────────────────

    /// <summary>
    /// Normalised energy points (0..1) for the SparklineControl.
    /// Derived from <see cref="AudioFeaturesEntity.SegmentedEnergyJson"/> (8 points) or
    /// a flat line at the track's EnergyScore if segmented data is unavailable.
    /// </summary>
    public IReadOnlyList<double> EnergyCurvePoints { get; }

    // ── Transition bridge ─────────────────────────────────────────────────────

    /// <summary>True when there is a next track and the bridge data has been computed.</summary>
    public bool HasBridge => Bridge != null;

    private FlowBridgeInfo? _bridge;
    public FlowBridgeInfo? Bridge
    {
        get => _bridge;
        set
        {
            this.RaiseAndSetIfChanged(ref _bridge, value);
            this.RaisePropertyChanged(nameof(HasBridge));
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> MoveLeftCommand  { get; }
    public ReactiveCommand<Unit, Unit> MoveRightCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand    { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public FlowTrackCardViewModel(
        PlaylistTrack track,
        Action onMoveLeft,
        Action onMoveRight,
        Action onRemove)
    {
        Artist          = track.Artist  ?? "Unknown Artist";
        Title           = track.Title   ?? "Unknown Title";
        FilePath        = track.ResolvedFilePath ?? string.Empty;
        TrackHash       = track.TrackUniqueHash  ?? string.Empty;

        BpmDisplay      = track.BPM.HasValue
            ? track.BPM.Value.ToString("F1")
            : "—";
        // Key may be in Camelot notation (e.g. "8A") or musical (e.g. "Am")
        KeyDisplay      = string.IsNullOrEmpty(track.Key) ? "—" : track.Key;
        DurationDisplay = track.CanonicalDuration.HasValue
            ? FormatDuration(track.CanonicalDuration.Value)
            : "—";

        EnergyCurvePoints = BuildEnergyCurve(track);

        MoveLeftCommand  = ReactiveCommand.Create(onMoveLeft);
        MoveRightCommand = ReactiveCommand.Create(onMoveRight);
        RemoveCommand    = ReactiveCommand.Create(onRemove);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

        // Fall back to flat line.
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

    // ── Bridge scoring ────────────────────────────────────────────────────────

    /// <summary>
    /// Computes and sets the transition bridge to the <paramref name="next"/> card.
    /// Uses Camelot wheel distance + BPM delta for colour scoring.
    /// </summary>
    public void SetBridgeTo(FlowTrackCardViewModel? next)
    {
        if (next == null) { Bridge = null; return; }

        // BPM delta display
        float myBpm   = float.TryParse(BpmDisplay, out var b1)  ? b1  : 0f;
        float nxtBpm  = float.TryParse(next.BpmDisplay, out var b2) ? b2 : 0f;
        float bpmDiff = myBpm > 0 && nxtBpm > 0 ? nxtBpm - myBpm : 0f;
        string bpmDeltaDisplay = bpmDiff == 0f ? "—"
            : bpmDiff > 0 ? $"+{bpmDiff:F1} BPM"
            : $"{bpmDiff:F1} BPM";

        // Camelot distance → colour
        double camelotDist = CamelotDistance(KeyDisplay, next.KeyDisplay);
        IBrush scoreBrush = camelotDist switch
        {
            <= 1 => new SolidColorBrush(Color.Parse("#4EC9B0")),   // compatible – teal
            <= 2 => new SolidColorBrush(Color.Parse("#FFD700")),   // energy shift – yellow
            <= 4 => new SolidColorBrush(Color.Parse("#FF8C00")),   // stretch – orange
            _    => new SolidColorBrush(Color.Parse("#E74C3C")),   // clash – red
        };

        string compatLabel = camelotDist switch
        {
            <= 1 => "✓ Compatible",
            <= 2 => "~ Energy shift",
            _    => "⚠ Key clash",
        };
        string keyLabel = $"{KeyDisplay} → {next.KeyDisplay}  {compatLabel}";
        string tooltip  = $"BPM: {BpmDisplay} → {next.BpmDisplay}  |  Key: {keyLabel}";

        Bridge = new FlowBridgeInfo
        {
            BpmDeltaDisplay = bpmDeltaDisplay,
            ScoreBrush      = scoreBrush,
            KeyLabel        = keyLabel,
            Tooltip         = tooltip,
        };
    }

    /// <summary>Camelot wheel distance (0 = perfect match, 6 = worst clash).</summary>
    private static double CamelotDistance(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == "—" || b == "—")
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
