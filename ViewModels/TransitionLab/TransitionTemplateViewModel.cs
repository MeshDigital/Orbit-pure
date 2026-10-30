using System;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Timeline;

namespace SLSKDONET.ViewModels.TransitionLab;

/// <summary>
/// Slice K6: binds the DSP transition parameters (cue anchors, mix-in timing, EQ/filter/echo, loop)
/// and maps them to the existing <see cref="TransitionModel"/> and the persisted
/// <see cref="TransitionTemplateEntity"/>.
/// </summary>
public sealed class TransitionTemplateViewModel : ReactiveObject
{
    private TransitionType _type = TransitionType.Crossfade;
    public TransitionType Type { get => _type; set => this.RaiseAndSetIfChanged(ref _type, value); }

    private int? _sourceCueRole;
    public int? SourceCueRole { get => _sourceCueRole; set => this.RaiseAndSetIfChanged(ref _sourceCueRole, value); }

    private int? _targetCueRole;
    public int? TargetCueRole { get => _targetCueRole; set => this.RaiseAndSetIfChanged(ref _targetCueRole, value); }

    private int _barsBeforeMix = 16;
    public int BarsBeforeMix { get => _barsBeforeMix; set => this.RaiseAndSetIfChanged(ref _barsBeforeMix, value); }

    private float _echoDecayFactor = 0.55f;
    public float EchoDecayFactor { get => _echoDecayFactor; set => this.RaiseAndSetIfChanged(ref _echoDecayFactor, value); }

    private float _filterStartFrequency = 20_000f;
    public float FilterStartFrequency { get => _filterStartFrequency; set => this.RaiseAndSetIfChanged(ref _filterStartFrequency, value); }

    private float _filterEndFrequency = 200f;
    public float FilterEndFrequency { get => _filterEndFrequency; set => this.RaiseAndSetIfChanged(ref _filterEndFrequency, value); }

    private bool _filterSweepRising;
    public bool FilterSweepRising { get => _filterSweepRising; set => this.RaiseAndSetIfChanged(ref _filterSweepRising, value); }

    private float _waveDuckDepth = 0.5f;
    public float WaveDuckDepth { get => _waveDuckDepth; set => this.RaiseAndSetIfChanged(ref _waveDuckDepth, value); }

    private bool _eqSwapLow = true;
    public bool EqSwapLow { get => _eqSwapLow; set => this.RaiseAndSetIfChanged(ref _eqSwapLow, value); }

    private bool _eqSwapMid;
    public bool EqSwapMid { get => _eqSwapMid; set => this.RaiseAndSetIfChanged(ref _eqSwapMid, value); }

    private bool _eqSwapHigh;
    public bool EqSwapHigh { get => _eqSwapHigh; set => this.RaiseAndSetIfChanged(ref _eqSwapHigh, value); }

    private float _eqLowCrossoverHz = 250f;
    public float EqLowCrossoverHz { get => _eqLowCrossoverHz; set => this.RaiseAndSetIfChanged(ref _eqLowCrossoverHz, value); }

    private float _eqHighCrossoverHz = 4000f;
    public float EqHighCrossoverHz { get => _eqHighCrossoverHz; set => this.RaiseAndSetIfChanged(ref _eqHighCrossoverHz, value); }

    private int _loopBars = 8;
    public int LoopBars { get => _loopBars; set => this.RaiseAndSetIfChanged(ref _loopBars, value); }

    private int _loopRepeats = 1;
    public int LoopRepeats { get => _loopRepeats; set => this.RaiseAndSetIfChanged(ref _loopRepeats, value); }

    private string? _notes;
    public string? Notes { get => _notes; set => this.RaiseAndSetIfChanged(ref _notes, value); }

    /// <summary>Builds the DSP-facing model (bars → beats for the overlap window).</summary>
    public TransitionModel ToTransitionModel() => new()
    {
        Type = Type,
        DurationBeats = BarsBeforeMix * 4.0,
        EchoDecayFactor = EchoDecayFactor,
        FilterStartFrequency = FilterStartFrequency,
        FilterEndFrequency = FilterEndFrequency,
        FilterSweepRising = FilterSweepRising,
        WaveDuckDepth = WaveDuckDepth,
        EqSwapLow = EqSwapLow,
        EqSwapMid = EqSwapMid,
        EqSwapHigh = EqSwapHigh,
        EqLowCrossoverHz = EqLowCrossoverHz,
        EqHighCrossoverHz = EqHighCrossoverHz,
        LoopBars = LoopBars,
        LoopRepeats = LoopRepeats,
    };

    /// <summary>Builds the persisted template entity (loop/effect settings serialized as JSON).</summary>
    public TransitionTemplateEntity ToEntity(string name) => new()
    {
        Name = name,
        SourceCueRole = SourceCueRole,
        TargetCueRole = TargetCueRole,
        BarsBeforeMix = BarsBeforeMix,
        LoopSettingsJson = System.Text.Json.JsonSerializer.Serialize(new { Bars = LoopBars, Repeats = LoopRepeats }),
        EffectsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            EchoDecayFactor,
            FilterStartFrequency,
            FilterEndFrequency,
            FilterSweepRising,
            WaveDuckDepth,
            EqSwapLow,
            EqSwapMid,
            EqSwapHigh,
            EqLowCrossoverHz,
            EqHighCrossoverHz,
        }),
        Notes = Notes,
    };
}
