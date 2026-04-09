using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel for a single stem waveform row (Vocals / Drums / Bass / Other) in
/// <see cref="StemWaveformViewModel"/>.  Binds directly to the per-row
/// <c>WaveformControl</c> instances inside <c>StemWaveformView.axaml</c>.
/// </summary>
public sealed class StemWaveformRowViewModel : ReactiveObject
{
    // ── Waveform data ─────────────────────────────────────────────────────────

    private WaveformAnalysisData? _waveformData;
    public WaveformAnalysisData? WaveformData
    {
        get => _waveformData;
        set => this.RaiseAndSetIfChanged(ref _waveformData, value);
    }

    // ── Playback progress (0..1) ──────────────────────────────────────────────

    private float _progress;
    public float Progress
    {
        get => _progress;
        set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    // ── Loading indicator ─────────────────────────────────────────────────────

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Clears waveform data (e.g. when a new track is loaded on the deck).</summary>
    public void Clear()
    {
        WaveformData = null;
        Progress     = 0f;
        IsLoading    = false;
    }
}
