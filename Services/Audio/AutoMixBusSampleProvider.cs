using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Services.Audio;

/// <summary>Minimal 3-band EQ interface driven per audio block by <see cref="AutoMixBusSampleProvider"/>.</summary>
internal interface IThreeBandEq
{
    bool Active { get; set; }
    float LowGain { get; set; }
    float MidGain { get; set; }
    float HighGain { get; set; }
}

/// <summary>
/// Represents one deck's audio chain feeding the unified <see cref="AutoMixBusSampleProvider"/> (#600 MX1).
/// </summary>
internal sealed class MixBusDeck
{
    public required ISampleProvider Source { get; init; }
    public required VolumeSampleProvider Gain { get; init; }
    public IThreeBandEq? Eq { get; init; }
    public float LoudnessGain { get; set; } = 1f;
    /// <summary>Starting file offset in output frames (e.g., when preloaded at a mix-in cue &gt; 0 s).</summary>
    public long InitialFrameOffset { get; set; }
    public Action? PrepareBeforeStart { get; init; }
    public object? Tag { get; init; }
}

/// <summary>
/// Sample-accurate scheduled transition plan executed inside <see cref="AutoMixBusSampleProvider.Read"/> (#600 MX1).
/// </summary>
internal sealed class ScheduledMixTransition
{
    public required MixBusDeck Incoming { get; init; }
    /// <summary>Exact outgoing rendered frame at which the incoming deck's first sample enters the mix.</summary>
    public required long StartFrame { get; init; }
    /// <summary>Duration of the transition in output frames.</summary>
    public required long DurationFrames { get; init; }
    /// <summary>Optional preset-driven TransitionRegion; null uses equal-power cos/sin crossfade.</summary>
    public TransitionRegion? Region { get; init; }
    public string? PresetName { get; init; }
    public double DurationSeconds { get; init; }
}

/// <summary>
/// Unified single-output stereo float mixer for Library Auto-Mix (Epic #596 / MX1 #600).
/// <para>
/// Both outgoing and incoming decks feed this single <see cref="ISampleProvider"/>, which owns the
/// one audio output device. Incoming deck start is scheduled on the exact output sample clock
/// (splitting buffers at the exact <see cref="ScheduledMixTransition.StartFrame"/> so the first
/// audible incoming sample lands with 0-sample jitter), and gain/EQ automation from
/// <see cref="TransitionEngine.CalculateAutomation"/> is evaluated per sub-block (&lt;= 10 ms).
/// </para>
/// </summary>
internal sealed class AutoMixBusSampleProvider : ISampleProvider
{
    private const int DefaultSampleRate = 44100;
    private const int Channels = 2;
    /// <summary>Max automation sub-block size (~5.8 ms at 44.1 kHz, strictly &lt;= 10 ms per #600).</summary>
    internal const int MaxBlockFrames = 256;

    private readonly object _gate = new();
    private readonly TransitionEngine _transitionEngine = new();
    private readonly Limiter _limiter;

    private MixBusDeck? _active;
    private ScheduledMixTransition? _scheduled;
    private bool _transitionActive;
    private long _activeRenderedFrames;
    private long _transitionStartFrame;
    private float _masterVolume = 1f;
    private volatile bool _isPaused;
    private float[] _mixScratch = new float[MaxBlockFrames * Channels];

    /// <summary>Safety ceiling at the end of the live Auto-Mix bus (−1.0 dBFS).</summary>
    internal const float LimiterCeilingDb = -1.0f;

    public WaveFormat WaveFormat { get; }

    public AutoMixBusSampleProvider(int sampleRate = DefaultSampleRate)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels);
        _limiter = new Limiter(thresholdDb: LimiterCeilingDb, lookAheadMs: 0f, sampleRate: sampleRate, releaseMs: 60f);
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => _isPaused = value;
    }

    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Clamp(value, 0f, 1f);
            lock (_gate)
            {
                if (_active != null && !_transitionActive)
                    _active.Gain.Volume = _masterVolume * _active.LoudnessGain;
            }
        }
    }

    public long ActiveRenderedFrames
    {
        get { lock (_gate) return _activeRenderedFrames; }
    }

    public long TransitionStartFrame
    {
        get { lock (_gate) return _transitionStartFrame; }
    }

    public bool IsTransitionActive
    {
        get { lock (_gate) return _transitionActive; }
    }

    public double TransitionProgress
    {
        get
        {
            lock (_gate)
            {
                if (!_transitionActive || _scheduled == null || _scheduled.DurationFrames <= 0)
                    return 0;
                return Math.Clamp((_activeRenderedFrames - _transitionStartFrame) / (double)_scheduled.DurationFrames, 0.0, 1.0);
            }
        }
    }

    public event Action<ScheduledMixTransition>? TransitionStarted;
    public event Action<MixBusDeck, MixBusDeck>? TransitionCompleted;
    public event Action<MixBusDeck>? ActiveDeckEnded;

    /// <summary>Adapts any input provider to the bus's stereo float format so one output device handles all tracks.</summary>
    public ISampleProvider AdaptToBusFormat(ISampleProvider source)
    {
        ISampleProvider adapted = source;
        if (adapted.WaveFormat.Channels == 1)
            adapted = new MonoToStereoSampleProvider(adapted);
        if (adapted.WaveFormat.SampleRate != WaveFormat.SampleRate)
            adapted = new WdlResamplingSampleProvider(adapted, WaveFormat.SampleRate);
        return adapted;
    }

    public void SetActiveDeck(MixBusDeck? deck, long initialRenderedFrames = 0)
    {
        lock (_gate)
        {
            _active = deck;
            _scheduled = null;
            _transitionActive = false;
            _activeRenderedFrames = Math.Max(0, initialRenderedFrames);
            if (_active == null)
            {
                _limiter.Reset();
            }
            else
            {
                _active.Gain.Volume = _masterVolume * _active.LoudnessGain;
                if (_active.Eq != null)
                {
                    _active.Eq.Active = false;
                    _active.Eq.LowGain = _active.Eq.MidGain = _active.Eq.HighGain = 1f;
                }
            }
        }
    }

    public void SetRenderedFrames(long frames)
    {
        lock (_gate)
        {
            _activeRenderedFrames = Math.Max(0, frames);
        }
    }

    public void ScheduleTransition(ScheduledMixTransition? plan)
    {
        lock (_gate)
        {
            if (_transitionActive) return; // don't replace a transition already in flight
            _scheduled = plan;
        }
    }

    public void CancelScheduledTransition()
    {
        lock (_gate)
        {
            _scheduled = null;
            _transitionActive = false;
            if (_active == null)
            {
                _limiter.Reset();
            }
            else
            {
                _active.Gain.Volume = _masterVolume * _active.LoudnessGain;
                if (_active.Eq != null) _active.Eq.Active = false;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int totalFramesRequested = count / Channels;
        if (totalFramesRequested <= 0) return 0;

        if (_isPaused)
        {
            Array.Clear(buffer, offset, totalFramesRequested * Channels);
            return totalFramesRequested * Channels;
        }

        int framesWritten = 0;

        while (framesWritten < totalFramesRequested)
        {
            ScheduledMixTransition? startedEvent = null;
            (MixBusDeck Outgoing, MixBusDeck Incoming)? completedEvent = null;
            MixBusDeck? endedEvent = null;

            int remainingFrames = totalFramesRequested - framesWritten;
            int blockFrames = Math.Min(remainingFrames, MaxBlockFrames);
            int writeOffset = offset + (framesWritten * Channels);

            lock (_gate)
            {
                if (_active == null)
                {
                    Array.Clear(buffer, writeOffset, blockFrames * Channels);
                    framesWritten += blockFrames;
                    continue;
                }

                // Sample-accurate split: if a transition is scheduled inside this block, render ONLY
                // up to StartFrame first so the incoming deck starts on the EXACT scheduled sample.
                if (!_transitionActive && _scheduled != null)
                {
                    if (_activeRenderedFrames < _scheduled.StartFrame)
                    {
                        long framesUntilStart = _scheduled.StartFrame - _activeRenderedFrames;
                        if (framesUntilStart < blockFrames)
                            blockFrames = (int)framesUntilStart;
                    }
                    else
                    {
                        // Exact StartFrame reached: activate transition now.
                        _transitionActive = true;
                        _transitionStartFrame = _activeRenderedFrames;
                        _scheduled.Incoming.PrepareBeforeStart?.Invoke();
                        bool useEq = _scheduled.Region != null;
                        if (_active.Eq != null) _active.Eq.Active = useEq;
                        if (_scheduled.Incoming.Eq != null) _scheduled.Incoming.Eq.Active = useEq;
                        startedEvent = _scheduled;
                    }
                }

                if (_transitionActive && _scheduled != null)
                {
                    long elapsedFrames = _activeRenderedFrames - _transitionStartFrame;
                    long remainingTransitionFrames = _scheduled.DurationFrames - elapsedFrames;
                    if (remainingTransitionFrames <= 0)
                    {
                        completedEvent = FinishTransitionLocked();
                        // Continue loop without advancing framesWritten so incoming fills the remaining buffer.
                    }
                    else
                    {
                        if (remainingTransitionFrames < blockFrames)
                            blockFrames = (int)remainingTransitionFrames;

                        double progress = _scheduled.DurationFrames > 0
                            ? Math.Clamp(elapsedFrames / (double)_scheduled.DurationFrames, 0.0, 1.0)
                            : 1.0;

                        ApplyBlockAutomation(_active, _scheduled.Incoming, _scheduled.Region, progress);

                        int samplesToRead = blockFrames * Channels;
                        EnsureScratch(samplesToRead);

                        int outRead = _active.Source.Read(buffer, writeOffset, samplesToRead);
                        if (outRead < samplesToRead)
                            Array.Clear(buffer, writeOffset + outRead, samplesToRead - outRead);

                        int inRead = _scheduled.Incoming.Source.Read(_mixScratch, 0, samplesToRead);
                        for (int i = 0; i < inRead; i++)
                            buffer[writeOffset + i] += _mixScratch[i];

                        int framesProduced = Math.Max(outRead, inRead) / Channels;
                        if (framesProduced <= 0)
                        {
                            completedEvent = FinishTransitionLocked();
                        }
                        else
                        {
                            _activeRenderedFrames += framesProduced;
                            framesWritten += framesProduced;

                            // Outgoing ran out mid-transition OR transition reached its end sample.
                            if (outRead < samplesToRead || (_activeRenderedFrames - _transitionStartFrame) >= _scheduled.DurationFrames)
                            {
                                completedEvent = FinishTransitionLocked();
                            }
                        }
                    }
                }
                else
                {
                    int samplesToRead = blockFrames * Channels;
                    int read = _active.Source.Read(buffer, writeOffset, samplesToRead);
                    if (read < samplesToRead)
                    {
                        Array.Clear(buffer, writeOffset + read, samplesToRead - read);
                        _activeRenderedFrames += read / Channels;
                        framesWritten += blockFrames;
                        endedEvent = _active;
                        _active = null;
                    }
                    else
                    {
                        _activeRenderedFrames += blockFrames;
                        framesWritten += blockFrames;
                    }
                }
            }

            // Fire notifications outside the audio lock to avoid deadlocks or lock-order inversions.
            if (startedEvent != null) TransitionStarted?.Invoke(startedEvent);
            if (completedEvent is { } comp) TransitionCompleted?.Invoke(comp.Outgoing, comp.Incoming);
            if (endedEvent != null) ActiveDeckEnded?.Invoke(endedEvent);
        }

        int samplesWritten = framesWritten * Channels;
        if (samplesWritten > 0)
            _limiter.Process(buffer, offset, samplesWritten);
        return samplesWritten;
    }

    private (MixBusDeck Outgoing, MixBusDeck Incoming) FinishTransitionLocked()
    {
        var outgoing = _active!;
        var incoming = _scheduled!.Incoming;
        long incomingFramesRendered = Math.Max(0, _activeRenderedFrames - _transitionStartFrame);

        if (outgoing.Eq != null) outgoing.Eq.Active = false;
        if (incoming.Eq != null)
        {
            incoming.Eq.Active = false;
            incoming.Eq.LowGain = incoming.Eq.MidGain = incoming.Eq.HighGain = 1f;
        }
        incoming.Gain.Volume = _masterVolume * incoming.LoudnessGain;

        _active = incoming;
        _scheduled = null;
        _transitionActive = false;
        _activeRenderedFrames = Math.Max(0, incoming.InitialFrameOffset) + incomingFramesRendered;

        return (outgoing, incoming);
    }

    private void ApplyBlockAutomation(MixBusDeck outgoing, MixBusDeck incoming, TransitionRegion? region, double t)
    {
        if (region != null)
        {
            long samplePos = region.StartSample + (long)Math.Round(t * (region.EndSample - region.StartSample));
            var auto = _transitionEngine.CalculateAutomation(region, samplePos);

            outgoing.Gain.Volume = _masterVolume * outgoing.LoudnessGain * auto.OutgoingGain;
            incoming.Gain.Volume = _masterVolume * incoming.LoudnessGain * auto.IncomingGain;

            if (outgoing.Eq != null)
            {
                outgoing.Eq.LowGain = auto.OutgoingLowGain;
                outgoing.Eq.MidGain = auto.OutgoingMidGain;
                outgoing.Eq.HighGain = auto.OutgoingHighGain;
            }
            if (incoming.Eq != null)
            {
                incoming.Eq.LowGain = auto.IncomingLowGain;
                incoming.Eq.MidGain = auto.IncomingMidGain;
                incoming.Eq.HighGain = auto.IncomingHighGain;
            }
        }
        else
        {
            // Constant-sum linear crossfade (matching TransitionEngine.CalculateCrossfade) so two
            // overlapping full-scale tracks stay at unity sum instead of peaking at +3.01 dB.
            outgoing.Gain.Volume = (float)(_masterVolume * outgoing.LoudnessGain * (1.0 - t));
            incoming.Gain.Volume = (float)(_masterVolume * incoming.LoudnessGain * t);
        }
    }

    private void EnsureScratch(int samples)
    {
        if (_mixScratch.Length < samples)
            _mixScratch = new float[samples];
    }

    /// <summary>Builds a <see cref="TransitionRegion"/> from a saved/planned <see cref="TransitionModel"/>.</summary>
    public static TransitionRegion BuildRegion(TransitionModel model, long durationFrames) => new()
    {
        StartSample = 0,
        EndSample = Math.Max(1, durationFrames),
        Type = model.Type.ToAutomationType(),
        Curve = TransitionCurve.SCurve,
        WaveDuckDepth = model.WaveDuckDepth,
        EchoDecayFactor = model.EchoDecayFactor,
        EqConfig = new EqBandSwapConfig
        {
            SwapLow = model.EqSwapLow,
            SwapMid = model.EqSwapMid,
            SwapHigh = model.EqSwapHigh,
            LowCrossover = model.EqLowCrossoverHz,
            HighCrossover = model.EqHighCrossoverHz,
            HardLowSwap = model.EqHardLowSwap,
            HardLowSwapAtEnd = model.EqHardLowSwapAtEnd,
        },
    };
}
