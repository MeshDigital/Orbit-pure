using System;
using System.Collections.ObjectModel;
using ReactiveUI;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.ViewModels;

// ─── StemEqViewModel ─────────────────────────────────────────────────────────

/// <summary>
/// Bindable EQ state for one stem.  Writes changes through to the
/// <see cref="NeuralMixEqSampleProvider"/> in the audio graph.
/// </summary>
public sealed class StemEqViewModel : ReactiveObject
{
    private readonly NeuralMixEqSampleProvider _eq;

    public StemType StemType    { get; }
    public string   DisplayName { get; }
    public string   AccentColor { get; }

    // ── Band gains ────────────────────────────────────────────────────────

    private float _lowDb;
    /// <summary>Low-shelf gain dB [-12, +12].</summary>
    public float LowGainDb
    {
        get => _lowDb;
        set
        {
            this.RaiseAndSetIfChanged(ref _lowDb, Math.Clamp(value, -12f, 12f));
            _eq.LowGainDb = _lowDb;
        }
    }

    private float _midDb;
    /// <summary>Mid peaking-EQ gain dB [-12, +12].</summary>
    public float MidGainDb
    {
        get => _midDb;
        set
        {
            this.RaiseAndSetIfChanged(ref _midDb, Math.Clamp(value, -12f, 12f));
            _eq.MidGainDb = _midDb;
        }
    }

    private float _highDb;
    /// <summary>High-shelf gain dB [-12, +12].</summary>
    public float HighGainDb
    {
        get => _highDb;
        set
        {
            this.RaiseAndSetIfChanged(ref _highDb, Math.Clamp(value, -12f, 12f));
            _eq.HighGainDb = _highDb;
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public System.Reactive.Unit ResetEq()
    {
        LowGainDb = 0f;
        MidGainDb = 0f;
        HighGainDb = 0f;
        return System.Reactive.Unit.Default;
    }

    public StemEqViewModel(StemType stemType, NeuralMixEqSampleProvider eq)
    {
        StemType = stemType;
        _eq      = eq;

        (DisplayName, AccentColor) = stemType switch
        {
            StemType.Vocals => ("Vocals", "#00CFFF"),
            StemType.Drums  => ("Drums",  "#FF8C00"),
            StemType.Bass   => ("Bass",   "#44FF88"),
            StemType.Other  => ("Other",  "#BB88FF"),
            _               => (stemType.ToString(), "#FFFFFF"),
        };
    }
}

// ─── NeuralMixEqViewModel ─────────────────────────────────────────────────────

/// <summary>
/// Aggregates four per-stem <see cref="StemEqViewModel"/>s, each wired to its own
/// <see cref="NeuralMixEqSampleProvider"/>.
///
/// The providers are exposed via <see cref="GetProvider(StemType)"/> so the audio
/// graph can insert them after each <see cref="StemMixerService"/> channel.
///
/// Register as <c>AddSingleton&lt;NeuralMixEqViewModel&gt;</c> in DI.
/// </summary>
public sealed class NeuralMixEqViewModel : ReactiveObject
{
    // Internal: NAudio sample providers keyed by stem type (inserted into audio chain)
    private readonly System.Collections.Generic.Dictionary<StemType, NeuralMixEqSampleProvider>
        _providers = new();

    public StemEqViewModel VocalsEq { get; }
    public StemEqViewModel DrumsEq  { get; }
    public StemEqViewModel BassEq   { get; }
    public StemEqViewModel OtherEq  { get; }

    public ReadOnlyCollection<StemEqViewModel> AllBands { get; }

    public NeuralMixEqViewModel()
    {
        // Placeholder source: flat silence until real stem WAV is loaded
        var silence = new SilenceSampleProvider();

        var vocalsEqProvider = new NeuralMixEqSampleProvider(silence);
        var drumsEqProvider  = new NeuralMixEqSampleProvider(silence);
        var bassEqProvider   = new NeuralMixEqSampleProvider(silence);
        var otherEqProvider  = new NeuralMixEqSampleProvider(silence);

        _providers[StemType.Vocals] = vocalsEqProvider;
        _providers[StemType.Drums]  = drumsEqProvider;
        _providers[StemType.Bass]   = bassEqProvider;
        _providers[StemType.Other]  = otherEqProvider;

        VocalsEq = new StemEqViewModel(StemType.Vocals, vocalsEqProvider);
        DrumsEq  = new StemEqViewModel(StemType.Drums,  drumsEqProvider);
        BassEq   = new StemEqViewModel(StemType.Bass,   bassEqProvider);
        OtherEq  = new StemEqViewModel(StemType.Other,  otherEqProvider);

        AllBands = new ReadOnlyCollection<StemEqViewModel>(
            new[] { VocalsEq, DrumsEq, BassEq, OtherEq });
    }

    /// <summary>Returns the <see cref="NeuralMixEqSampleProvider"/> for the given stem.</summary>
    public NeuralMixEqSampleProvider GetProvider(StemType st) => _providers[st];

    /// <summary>Resets all four stems to flat EQ (0 dB each band).</summary>
    public void ResetAll()
    {
        foreach (var band in AllBands) band.ResetEq();
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    /// <summary>Minimal ISampleProvider that produces digital silence at 44100/stereo.</summary>
    private sealed class SilenceSampleProvider : NAudio.Wave.ISampleProvider
    {
        public NAudio.Wave.WaveFormat WaveFormat { get; } =
            NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
