using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Timers;
using SLSKDONET.Configuration;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Services
{
    public class AudioPlayerService : IAudioPlayerService, IDisposable
    {
        private readonly AppConfig _config;

        /// <summary>
        /// One decoded/opened audio stream + its output device. Two decks let the engine have
        /// the next track already open and ready (preloaded) while the current one is still
        /// playing, which is what makes gapless transitions and crossfading possible.
        /// </summary>
        public string? LoadedFilePath
        {
            get { lock (_deckLock) return _current is { IsDisposed: false } d && !string.IsNullOrEmpty(d.FilePath) ? d.FilePath : null; }
        }

        private class Deck : IDisposable
        {
            public AudioFileReader? AudioFile;
            public IWavePlayer? Output;
            public MeteringSampleProvider? Metering;
            public MixBusDeck? BusDeck;
            public VariSpeedSampleProvider? VariSpeed;
            public ThreeBandGainProvider? Eq;
            /// <summary>Optional metronome click mixed in before the gain stage (Cue Forge audition).
            /// A pass-through when disabled, so it is always in the chain.</summary>
            public SLSKDONET.Services.Audio.MetronomeSampleProvider? Metronome;
            /// <summary>In-process gain stage for master volume/crossfade/loudness automation.
            /// Deliberately NOT <see cref="IWavePlayer.Volume"/> — on WASAPI that setter writes
            /// through to the OS-level per-app session volume (the same control behind the
            /// Windows Volume Mixer slider for this process), not an internal signal multiply.
            /// Driving that ~20x/sec from the crossfade timer was a real bug: it round-trips
            /// through the Windows Audio Session API on every tick, and any dropped/out-of-order
            /// update (process suspend, COM call failure, a deck disposed mid-write) leaves the
            /// OS-visible session sitting at whatever the last write was — reported as "audio
            /// gets muted in Windows". <see cref="Output"/>'s own Volume is pinned to 1.0 once at
            /// creation and never touched again; all real gain changes go through this instead.</summary>
            public NAudio.Wave.SampleProviders.VolumeSampleProvider? Gain;
            /// <summary>Linear gain applied on top of the master volume for loudness-normalized playback (see <see cref="AppConfig.LoudnessNormalizationEnabled"/>). 1.0 = no adjustment.</summary>
            public float LoudnessGain = 1f;

            /// <summary>Mix-saved transition to apply to the crossfade into this deck (set via
            /// PreloadNext), or null to fall back to the legacy fixed CrossfadeSeconds/curve.</summary>
            public SLSKDONET.Models.Timeline.TransitionModel? PendingTransition;
            public double PendingTransitionBpm = 128.0;

            /// <summary>Display name of the preset behind <see cref="PendingTransition"/> (e.g.
            /// "Wave") — carried purely for UI visibility (CrossfadeStartedEventArgs.PresetName),
            /// not consulted by the DSP itself.</summary>
            public string? PendingTransitionPresetName;

            /// <summary>Absolute position (seconds) into the OUTGOING (currently-playing) deck
            /// where the crossfade into this deck should begin — the analysis-suggested or saved
            /// mix-out point, not "duration minus crossfade length". Null falls back to legacy
            /// countdown-from-end behavior.</summary>
            public double? PendingSourceTriggerSeconds;

            /// <summary>Position (seconds) this deck's own file should be seeked to on load — the
            /// analysis-suggested or saved mix-in point (may skip a low-energy intro straight to
            /// the first drop). Null means start at 0 as usual.</summary>
            public double? PendingTargetTriggerSeconds;

            /// <summary>File BPM of the OUTGOING track for this pending transition (the incoming
            /// file BPM is <see cref="PendingTransitionBpm"/>). With both known, the incoming deck is
            /// tempo-matched when the mix starts and transition lengths follow the outgoing beat.</summary>
            public double? PendingSourceBpm;

            /// <summary>File this deck plays (for <see cref="SLSKDONET.Services.Audio.ExactSeek"/>).</summary>
            public string FilePath = string.Empty;

            /// <summary>Background exact seek in flight (Media Foundation formats decode forward to
            /// the target, ~70–300 ms). The deck must not start playing until it finishes.</summary>
            public System.Threading.Tasks.Task? SeekTask;

            /// <summary>Live-seek hand-off (see the Position setter): latest target not yet applied,
            /// and whether a worker is draining it. Guarded by <see cref="SeekLock"/>.</summary>
            public readonly object SeekLock = new();
            public double? RequestedSeekSeconds;
            public bool SeekWorkerRunning;

            /// <summary>Set by <see cref="Dispose"/> before the output is stopped. WASAPI raises
            /// <c>PlaybackStopped</c> synchronously from <c>Stop()</c> (verified on NAudio 2.2.1,
            /// shared + event-sync), i.e. while this deck may still be the service's <c>_current</c>.
            /// The output's PlaybackStopped handler must ignore a deck that is being torn down on
            /// purpose, or it mistakes a track change for a natural end-of-track and starts the
            /// preloaded deck (see AudioPlayerService.PromoteNextDeck).</summary>
            public volatile bool IsDisposed;

            /// <summary>Told the owning service to drop this deck from its live set.</summary>
            public Action<Deck>? OnDisposed;

            /// <summary>Blocks until a pending background seek is done (normally long finished —
            /// preload happens minutes before the crossfade). Bounded so playback can never hang.</summary>
            public void WaitForSeek()
            {
                try { SeekTask?.Wait(TimeSpan.FromSeconds(3)); } catch { /* seek failure: play from wherever it is */ }
            }

            /// <summary>Seeks this (not yet playing) deck exactly to <paramref name="seconds"/> in the background.</summary>
            public void SeekInBackground(double seconds)
            {
                var file = AudioFile;
                if (file == null) return;
                if (!SLSKDONET.Services.Audio.ExactSeek.NeedsDecodeForward(FilePath))
                {
                    WaitForSeek();
                    file.CurrentTime = TimeSpan.FromSeconds(seconds);
                    VariSpeed?.Reset();
                    Metronome?.Seek(seconds);
                    return;
                }
                var previous = SeekTask;
                SeekTask = System.Threading.Tasks.Task.Run(async () =>
                {
                    if (previous != null)
                    {
                        try { await previous; }
                        catch (Exception ex) { Serilog.Log.Warning(ex, "[AudioPlayerService] Previous seek task failed"); }
                    }
                    SLSKDONET.Services.Audio.ExactSeek.Seek(file, FilePath, seconds);
                    VariSpeed?.Reset();
                    Metronome?.Seek(seconds);
                });
            }

            public void Dispose()
            {
                if (IsDisposed) return;
                IsDisposed = true;
                try
                {
                    try { Output?.Stop(); } catch { /* already stopped/disposed */ }
                    AudioFile?.Dispose();
                    try { Output?.Dispose(); } catch { /* already disposed */ }
                    Output = null;
                    AudioFile = null;
                }
                finally
                {
                    OnDisposed?.Invoke(this);
                }
            }
        }

        /// <summary>
        /// Live per-channel 3-band gain stage (one-pole crossover split, same technique as
        /// <see cref="SLSKDONET.Services.Timeline.TransitionDsp"/>'s EqSwapProvider/FilterSweepProvider)
        /// inserted into each deck's chain so Mix presets that need audible EQ movement during a
        /// transition (Blend/Wave/Melt) — not just a plain volume crossfade — actually sound
        /// different during real queue playback. Gains default to 1.0 (a no-op fast path) outside
        /// a transition window.
        /// </summary>
        internal sealed class ThreeBandGainProvider : ISampleProvider, IThreeBandEq
        {
            private readonly ISampleProvider _source;
            private readonly float _lowCrossoverHz;
            private readonly float _highCrossoverHz;
            private float[] _lowState = Array.Empty<float>();
            private float[] _midHighSplitState = Array.Empty<float>();

            private volatile bool _active;
            public bool Active { get => _active; set => _active = value; }
            public float LowGain { get; set; } = 1f;
            public float MidGain { get; set; } = 1f;
            public float HighGain { get; set; } = 1f;

            public WaveFormat WaveFormat => _source.WaveFormat;

            public ThreeBandGainProvider(ISampleProvider source, float lowCrossoverHz = 250f, float highCrossoverHz = 4000f)
            {
                _source = source;
                _lowCrossoverHz = lowCrossoverHz;
                _highCrossoverHz = highCrossoverHz;
                int channels = Math.Max(1, source.WaveFormat.Channels);
                _lowState = new float[channels];
                _midHighSplitState = new float[channels];
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);
                if (!Active || (LowGain == 1f && MidGain == 1f && HighGain == 1f)) return read;

                int channels = WaveFormat.Channels;
                int sampleRate = WaveFormat.SampleRate;
                float dt = 1f / sampleRate;
                float alphaLow = dt / ((1f / (2f * MathF.PI * _lowCrossoverHz)) + dt);
                float alphaHigh = dt / ((1f / (2f * MathF.PI * _highCrossoverHz)) + dt);

                for (int i = 0; i < read; i++)
                {
                    int ch = i % channels;
                    float x = buffer[offset + i];

                    _lowState[ch] += alphaLow * (x - _lowState[ch]);
                    float highPassAtLow = x - _lowState[ch];
                    _midHighSplitState[ch] += alphaHigh * (highPassAtLow - _midHighSplitState[ch]);
                    float high = highPassAtLow - _midHighSplitState[ch];
                    float mid = _midHighSplitState[ch];

                    buffer[offset + i] = (_lowState[ch] * LowGain) + (mid * MidGain) + (high * HighGain);
                }

                return read;
            }
        }

        private Deck? _current;
        private Deck? _next;
        private string? _nextFilePath;
        private bool _isCrossfading;
        private double _crossfadeProgress;
        private float _masterVolumeFraction = 1f;

        /// <summary>MX1 (#600): Unified single-output stereo mix graph owning the one audio device.</summary>
        private readonly AutoMixBusSampleProvider _mixBus = new();
        private IWavePlayer? _sharedOutput;

        /// <summary>Guards <see cref="_current"/>, <see cref="_next"/>, <see cref="_nextFilePath"/>,
        /// <see cref="_stopping"/> and <see cref="_liveDecks"/>: the deck fields are touched by the
        /// UI thread (play/pause/stop), the crossfade timer thread and each output's
        /// PlaybackStopped thread (which WASAPI raises synchronously from inside Stop()).</summary>
        private readonly object _deckLock = new();

        /// <summary>True while a deliberate Stop() is tearing the decks down. Set before the
        /// outputs are stopped, so a PlaybackStopped raised by that Stop() can never be mistaken
        /// for an end-of-track that should hand over to the preloaded deck.</summary>
        private bool _stopping;

        /// <summary>Every deck this service created and has not disposed yet. Exists so Stop() can
        /// guarantee "nothing is still making sound": a deck that lost its <see cref="_current"/>/
        /// <see cref="_next"/> reference would otherwise keep playing with no way to reach it.</summary>
        private readonly HashSet<Deck> _liveDecks = new();

        /// <summary>Test seam: replaces the real output device. Production leaves this null.</summary>
        internal Func<ISampleProvider, IWavePlayer>? OutputFactory { get; set; }

        private bool _isInitialized;
        private System.Timers.Timer? _timer;

        public event EventHandler<long>? TimeChanged;
        public event EventHandler<float>? PositionChanged;
        public event EventHandler<long>? LengthChanged;
        public event EventHandler<AudioLevelsEventArgs>? AudioLevelsChanged;
        public event EventHandler<float[]>? SpectrumChanged;
        /// <summary>Mono time-domain block (2048 samples) of what is playing, while a visualizer is attached.</summary>
        public event EventHandler<float[]>? WaveformChanged;
        public event EventHandler? EndReached;
        public event EventHandler? PausableChanged;

        /// <summary>True while an active crossfade is in progress. Previously computed
        /// entirely internally (the <c>_isCrossfading</c> field) with no way for any ViewModel
        /// to know a mix was even happening, let alone how far through it was or which preset.</summary>
        public bool IsCrossfading => _isCrossfading;

        /// <summary>0.0-1.0 progress through the active crossfade; 0 when none is active.</summary>
        public double CrossfadeProgress => _crossfadeProgress;

        public event EventHandler<CrossfadeStartedEventArgs>? CrossfadeStarted;
        public event EventHandler<double>? CrossfadeProgressChanged;
        public event EventHandler? CrossfadeEnded;
        public event EventHandler<OutputFallbackEventArgs>? OutputFellBack;

        /// <summary>Fired when the engine autonomously advances to a preloaded track (gapless
        /// swap or crossfade completion), so listeners can sync "now playing" state without
        /// re-triggering Play() themselves.</summary>
        public event EventHandler? TrackAdvanced;

        private int _vuMeterSkipCounter = 0;
        private const int VU_METER_SKIP_FRAMES = 5; // Only update VU meter every 5th buffer
        private const double TimerIntervalSeconds = 0.05; // 50ms tick, matches _timer below

        private double _pitch = 1.0;
        /// <summary>Turntable-style pitch: 1.0 = normal, &gt;1.0 = faster/higher, &lt;1.0 =
        /// slower/lower. Speed and pitch move together, matching a real turntable/CDJ pitch
        /// fader rather than tempo-only time-stretching.</summary>
        public double Pitch
        {
            get => _pitch;
            set
            {
                _pitch = value;
                if (_current?.VariSpeed != null) _current.VariSpeed.Speed = value;
                if (_next?.VariSpeed != null) _next.VariSpeed.Speed = value;
                RefreshScheduledBusTransition();
            }
        }

        private bool _crossfadeEnabled;
        /// <summary>When enabled, the preloaded next track fades in while the current track
        /// fades out over <see cref="CrossfadeSeconds"/>, instead of a hard gapless cut.</summary>
        public bool CrossfadeEnabled
        {
            get => _crossfadeEnabled;
            set
            {
                _crossfadeEnabled = value;
                RefreshScheduledBusTransition();
            }
        }

        private double _crossfadeSeconds = 3.0;
        /// <summary>Length of the crossfade overlap, in seconds. Only used when
        /// <see cref="CrossfadeEnabled"/> is true.</summary>
        public double CrossfadeSeconds
        {
            get => _crossfadeSeconds;
            set
            {
                _crossfadeSeconds = value;
                RefreshScheduledBusTransition();
            }
        }

        public AudioPlayerService(AppConfig config)
        {
            _config = config;
            _isInitialized = true;
            _mixBus.TransitionStarted += OnBusTransitionStarted;
            _mixBus.TransitionCompleted += OnBusTransitionCompleted;
            _mixBus.ActiveDeckEnded += OnBusActiveDeckEnded;
            _timer = new System.Timers.Timer(TimerIntervalSeconds * 1000);
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
        }

        private void OnBusTransitionStarted(ScheduledMixTransition plan)
        {
            lock (_deckLock)
            {
                _isCrossfading = true;
                _crossfadeProgress = 0;
            }
            System.Threading.Tasks.Task.Run(() => CrossfadeStarted?.Invoke(this, new CrossfadeStartedEventArgs
            {
                PresetName = plan.PresetName,
                DurationSeconds = plan.DurationSeconds,
            }));
        }

        private void OnBusTransitionCompleted(MixBusDeck outgoingBus, MixBusDeck incomingBus)
        {
            var outgoing = outgoingBus.Tag as Deck;
            var incoming = incomingBus.Tag as Deck;
            if (outgoing == null || incoming == null) return;

            lock (_deckLock)
            {
                if (!ReferenceEquals(_current, outgoing)) return;
                _isCrossfading = false;
                _crossfadeProgress = 0;
                _current = incoming;
                if (ReferenceEquals(_next, incoming))
                {
                    _next = null;
                    _nextFilePath = null;
                }
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                LengthChanged?.Invoke(this, (long)(incoming.AudioFile?.TotalTime.TotalMilliseconds ?? 0));
                try { outgoing.Dispose(); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "[AudioPlayerService] Deferred disposal of finished deck failed"); }
                CrossfadeEnded?.Invoke(this, EventArgs.Empty);
                TrackAdvanced?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnBusActiveDeckEnded(MixBusDeck endedBus)
        {
            var ended = endedBus.Tag as Deck;
            if (ended == null) return;

            Deck? nextToPromote = null;
            lock (_deckLock)
            {
                if (_stopping || !ReferenceEquals(_current, ended) || ended.IsDisposed) return;
                if (_next?.BusDeck != null)
                {
                    nextToPromote = _next;
                    _next = null;
                    _nextFilePath = null;
                    _current = nextToPromote;
                }
            }

            if (nextToPromote != null)
            {
                nextToPromote.WaitForSeek();
                double speed = Math.Max(0.01, nextToPromote.VariSpeed?.Speed ?? 1.0);
                long offsetFrames = (long)Math.Round(((nextToPromote.AudioFile?.CurrentTime.TotalSeconds ?? 0) / speed) * _mixBus.WaveFormat.SampleRate);
                _mixBus.SetActiveDeck(nextToPromote.BusDeck, offsetFrames);
                System.Threading.Tasks.Task.Run(() =>
                {
                    LengthChanged?.Invoke(this, (long)(nextToPromote.AudioFile?.TotalTime.TotalMilliseconds ?? 0));
                    try { ended.Dispose(); }
                    catch (Exception ex) { Serilog.Log.Warning(ex, "[AudioPlayerService] Deferred disposal of ended deck failed"); }
                    TrackAdvanced?.Invoke(this, EventArgs.Empty);
                });
            }
            else
            {
                System.Threading.Tasks.Task.Run(() => EndReached?.Invoke(this, EventArgs.Empty));
            }
        }

        private void RefreshScheduledBusTransition()
        {
            Deck? current, next;
            lock (_deckLock)
            {
                current = _current;
                next = _next;
                if (_isCrossfading) return;
            }

            if (current?.AudioFile == null || next?.BusDeck == null)
            {
                _mixBus.CancelScheduledTransition();
                return;
            }

            var pendingTransition = next.PendingTransition;
            if (!CrossfadeEnabled && pendingTransition == null)
            {
                _mixBus.CancelScheduledTransition();
                return;
            }

            double effectiveSeconds = TransitionSeconds(current, next);
            double totalSeconds = current.AudioFile.TotalTime.TotalSeconds;
            double plannedStartSeconds = next.PendingSourceTriggerSeconds is double sourceTrigger
                ? LatestMixStart(sourceTrigger, totalSeconds, effectiveSeconds)
                : Math.Max(0, totalSeconds - effectiveSeconds);

            double speed = Math.Max(0.01, current.VariSpeed?.Speed ?? 1.0);
            long startFrame = (long)Math.Round((plannedStartSeconds / speed) * _mixBus.WaveFormat.SampleRate);
            long durationFrames = Math.Max(1, (long)Math.Round(effectiveSeconds * _mixBus.WaveFormat.SampleRate));

            var region = pendingTransition != null
                ? AutoMixBusSampleProvider.BuildRegion(pendingTransition, durationFrames)
                : null;

            _mixBus.ScheduleTransition(new ScheduledMixTransition
            {
                Incoming = next.BusDeck,
                StartFrame = startFrame,
                DurationFrames = durationFrames,
                Region = region,
                PresetName = next.PendingTransitionPresetName,
                DurationSeconds = effectiveSeconds,
            });
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            Deck? current;
            lock (_deckLock)
            {
                current = _current;
            }
            if (current?.AudioFile == null) return;

            // Unified mix-bus mode (#600 MX1): _mixBus.Read() owns sample-exact transition
            // scheduling and per-block (<= 5.8 ms) gain/EQ automation. The timer only emits
            // UI progress ticks.
            if (current.BusDeck != null)
            {
                if (_sharedOutput?.PlaybackState != PlaybackState.Playing || _mixBus.IsPaused)
                    return;

                TimeChanged?.Invoke(this, (long)current.AudioFile.CurrentTime.TotalMilliseconds);
                PositionChanged?.Invoke(this, Position);

                if (_isCrossfading)
                {
                    var progress = _mixBus.TransitionProgress;
                    _crossfadeProgress = progress;
                    CrossfadeProgressChanged?.Invoke(this, progress);
                }
                return;
            }

            // Legacy per-deck fallback (retained only for reflection-based unit tests that
            // construct a Deck directly with a mock IWavePlayer).
            if (_isCrossfading && current.Output?.PlaybackState == PlaybackState.Stopped)
            {
                CompleteCrossfade(current);
                return;
            }

            if (current.Output?.PlaybackState != PlaybackState.Playing)
                return;

            TimeChanged?.Invoke(this, (long)current.AudioFile.CurrentTime.TotalMilliseconds);
            PositionChanged?.Invoke(this, Position);
        }

        /// <summary>Ends the crossfade (legacy reflection-test path): incoming deck at full level with its EQ reset, then promoted.</summary>
        private void CompleteCrossfade(Deck current)
        {
            Deck? next;
            lock (_deckLock)
            {
                if (!ReferenceEquals(_current, current)) return;
                _isCrossfading = false;
                _crossfadeProgress = 0;
                next = _next;
            }
            if (current.Eq != null) current.Eq.Active = false;
            if (next != null)
            {
                if (next.Eq != null) { next.Eq.Active = false; next.Eq.LowGain = next.Eq.MidGain = next.Eq.HighGain = 1f; }
                if (next.Gain != null) next.Gain.Volume = _masterVolumeFraction * next.LoudnessGain;
            }
            PromoteNextDeck(current);
            CrossfadeEnded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// When the crossfade out of a track must begin: at the mix-out point, but no later than
        /// "track end minus transition length" so the whole transition fits. Public for tests.
        /// </summary>
        public static double LatestMixStart(double mixOutSeconds, double trackSeconds, double transitionSeconds)
        {
            if (trackSeconds <= 0 || transitionSeconds <= 0) return mixOutSeconds;
            var latest = Math.Max(0, trackSeconds - transitionSeconds);
            return Math.Min(mixOutSeconds, latest);
        }

        /// <summary>Largest tempo change applied to match an incoming track (6 % ≈ one semitone).</summary>
        private const double MaxTempoSyncRatio = 0.06;

        /// <summary>
        /// Real-time length of the pending transition. Its bars follow the OUTGOING track's beat as
        /// it is actually playing (file BPM × deck speed) when that is known — the incoming track is
        /// tempo-matched to it — otherwise the incoming BPM as before.
        /// </summary>
        private double TransitionSeconds(Deck current, Deck? next)
        {
            var transition = next?.PendingTransition;
            if (transition == null) return CrossfadeSeconds;
            double bpm = next!.PendingSourceBpm is double outBpm
                ? outBpm * (current.VariSpeed?.Speed ?? 1.0)
                : next.PendingTransitionBpm;
            return transition.DurationBeats * 60.0 / Math.Max(1, bpm);
        }

        /// <summary>
        /// Matches the incoming deck's tempo to the outgoing one (the DJ pitch fader) when both file
        /// BPMs are known and within <see cref="MaxTempoSyncRatio"/>; the incoming track keeps that
        /// tempo after the mix. Without this, two tracks 2 BPM apart drift a quarter-second out of
        /// time over 16 bars and the kicks flam. Returns incoming/outgoing file-time rate ratio.
        /// Public for tests.
        /// </summary>
        public static double TempoSyncRatio(double? outgoingBpm, double incomingBpm)
        {
            if (outgoingBpm is not > 0 || incomingBpm <= 0) return 1.0;
            // Half/double-time readings (87 vs 174) are the same tempo for mixing.
            double ratio = SLSKDONET.Engine.Transitions.TempoFold.Ratio(outgoingBpm.Value, incomingBpm);
            return Math.Abs(ratio - 1) <= MaxTempoSyncRatio ? ratio : 1.0;
        }

        private double ApplyTempoSync(Deck current, Deck next)
        {
            double currentSpeed = current.VariSpeed?.Speed ?? 1.0;
            double ratio = TempoSyncRatio(next.PendingSourceBpm, next.PendingTransitionBpm);
            if (next.VariSpeed != null && ratio != 1.0)
            {
                next.VariSpeed.Speed = currentSpeed * ratio;
                Serilog.Log.Information("[Mix] Tempo-matched incoming track: ×{Ratio:0.000} ({Out:0.0} → {In:0.0} BPM file tempo)",
                    ratio, next.PendingSourceBpm, next.PendingTransitionBpm);
            }
            else if (next.VariSpeed != null)
            {
                next.VariSpeed.Speed = currentSpeed;
            }
            // Incoming file seconds per outgoing file second.
            return (next.VariSpeed?.Speed ?? 1.0) / Math.Max(0.01, currentSpeed);
        }

        public bool IsInitialized => _isInitialized;
        public bool IsPlaying =>
            (_current != null && _sharedOutput?.PlaybackState == PlaybackState.Playing && !_mixBus.IsPaused)
            || (_current?.Output?.PlaybackState == PlaybackState.Playing);
        public long Length => (long)(_current?.AudioFile?.TotalTime.TotalMilliseconds ?? 0);
        public double Duration => _current?.AudioFile?.TotalTime.TotalSeconds ?? 0;
        public long Time => (long)(_current?.AudioFile?.CurrentTime.TotalMilliseconds ?? 0);

        public float Position
        {
            // Clamped: Media Foundation rounds FLAC lengths down to the second, so the raw ratio
            // can run a little past 1 at the very end of a track.
            get => (float)(_current?.AudioFile is { Length: > 0 } file ? Math.Clamp(file.Position / (double)file.Length, 0, 1) : 0);
            set
            {
                var deck = _current;
                if (deck?.AudioFile == null) return;

                if (!SLSKDONET.Services.Audio.ExactSeek.NeedsDecodeForward(deck.FilePath))
                {
                    deck.AudioFile.Position = (long)(value * deck.AudioFile.Length);
                    deck.VariSpeed?.Reset(); // discard stale buffered samples from before the seek
                    deck.Metronome?.Seek(deck.AudioFile.CurrentTime.TotalSeconds);
                    SyncBusFramesToDeck(deck);
                    RefreshScheduledBusTransition();
                    return;
                }

                // FLAC/M4A: Media Foundation lands up to ~0.9 s off target, so seek exactly by
                // decoding forward — off the UI thread, with the bus paused meanwhile. Rapid
                // seeks (dragging a seek bar) collapse into the latest target.
                lock (deck.SeekLock)
                {
                    deck.RequestedSeekSeconds = Math.Clamp(value, 0f, 1f) * deck.AudioFile.TotalTime.TotalSeconds;
                    if (deck.SeekWorkerRunning) return; // the running worker picks up the new target
                    deck.SeekWorkerRunning = true;
                }
                bool wasPlaying = IsPlaying;
                deck.SeekTask = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (wasPlaying) _mixBus.IsPaused = true;
                        while (true)
                        {
                            double seconds;
                            lock (deck.SeekLock)
                            {
                                if (deck.RequestedSeekSeconds is not double next || deck.AudioFile == null)
                                {
                                    deck.SeekWorkerRunning = false;
                                    break;
                                }
                                seconds = next;
                                deck.RequestedSeekSeconds = null;
                            }
                            try
                            {
                                SLSKDONET.Services.Audio.ExactSeek.Seek(deck.AudioFile, deck.FilePath, seconds);
                                deck.VariSpeed?.Reset();
                                deck.Metronome?.Seek(seconds);
                                SyncBusFramesToDeck(deck);
                                RefreshScheduledBusTransition();
                            }
                            catch (Exception ex)
                            {
                                Serilog.Log.Warning(ex, "[AudioPlayerService] Exact seek failed for {File}", deck.FilePath);
                            }
                        }
                    }
                    finally
                    {
                        lock (deck.SeekLock) deck.SeekWorkerRunning = false;
                        if (wasPlaying && ReferenceEquals(_current, deck)) _mixBus.IsPaused = false;
                    }
                });
            }
        }

        private void SyncBusFramesToDeck(Deck deck)
        {
            if (deck.AudioFile == null) return;
            double speed = Math.Max(0.01, deck.VariSpeed?.Speed ?? 1.0);
            long frames = (long)Math.Round((deck.AudioFile.CurrentTime.TotalSeconds / speed) * _mixBus.WaveFormat.SampleRate);
            _mixBus.SetRenderedFrames(frames);
        }

        public int Volume
        {
            get => (int)(_masterVolumeFraction * 100);
            set
            {
                _masterVolumeFraction = Math.Clamp(value / 100f, 0f, 1f);
                _mixBus.MasterVolume = _masterVolumeFraction;
                if (_current?.Gain != null && !_isCrossfading)
                {
                    _current.Gain.Volume = _masterVolumeFraction * _current.LoudnessGain;
                }
            }
        }

        /// <summary>
        /// Enables/disables the Cue Forge metronome click on the current deck, scheduled from the
        /// beat grid (issue #222). A pass-through when disabled.
        /// </summary>
        public void SetMetronome(IReadOnlyList<double>? beatTimes, double downbeatSeconds, bool enabled)
        {
            Deck? current;
            lock (_deckLock) { current = _current; }
            if (current?.Metronome == null) return;
            if (beatTimes is { Count: > 0 }) current.Metronome.SetGrid(beatTimes, downbeatSeconds);
            current.Metronome.Enabled = enabled;
        }

        public bool IsVisualizerActive { get; private set; }

        private int _visualizerAttachCount;

        /// <summary>
        /// Was never called from anywhere, which is why IsVisualizerActive stayed permanently
        /// false — the FFT/spectrum computation it gates ran unconditionally for every playing
        /// track regardless of whether a visualizer control was ever going to render it. Wired
        /// from VibeVisualizer/OrbitVisualizerCanvas's OnAttachedToVisualTree. Reference-counted
        /// so two simultaneously-visible visualizer instances don't have one's detach turn the
        /// flag off while the other is still showing.
        /// </summary>
        public void NotifyVisualizerAttached()
        {
            IsVisualizerActive = System.Threading.Interlocked.Increment(ref _visualizerAttachCount) > 0;
        }

        public void NotifyVisualizerDetached()
        {
            var count = System.Threading.Interlocked.Decrement(ref _visualizerAttachCount);
            if (count < 0)
            {
                // Defensive: an unmatched Detached call (shouldn't happen if every control pairs
                // Attached/Detached correctly) — clamp so a stray extra call can't push future
                // legitimate attach/detach pairs permanently out of sync.
                System.Threading.Interlocked.Exchange(ref _visualizerAttachCount, 0);
                count = 0;
            }
            IsVisualizerActive = count > 0;
        }

        public void Play(string filePath, double? trackLoudnessLufs = null) => OpenDevice(filePath, autoPlay: true, trackLoudnessLufs);

        /// <summary>
        /// Opens the file and initializes the output device without starting playback.
        /// Callers that just need a track "hot and ready" (e.g. Cue Forge loading a track
        /// before the user presses play) should use this instead of Play() — a
        /// Play()-then-immediately-Pause() sequence still lets a moment of real audio through
        /// the WASAPI buffer before the pause takes effect, which is audible as a brief blip.
        /// </summary>
        public void LoadWithoutPlaying(string filePath, double? trackLoudnessLufs = null) => OpenDevice(filePath, autoPlay: false, trackLoudnessLufs);

        /// <summary>
        /// Opens and initializes the output device for the track that will play next, without
        /// making any sound, so the transition when the current track ends can be a near-instant
        /// swap (gapless) or a timed overlap (crossfade) instead of a cold file-open that causes
        /// an audible gap. Call this as soon as the next track is known (e.g. right after the
        /// current one starts), well before playback is expected to reach it.
        /// </summary>
        public void PreloadNext(string filePath, double? trackLoudnessLufs = null, SLSKDONET.Models.Timeline.TransitionModel? transition = null, double? transitionBpm = null,
            double? sourceTriggerSeconds = null, double? targetTriggerSeconds = null, string? presetName = null)
        {
            lock (_deckLock)
            {
                if (_current == null) return;
                if (_nextFilePath == filePath && _next != null) return; // already preloaded
            }

            CancelPreload();

            try
            {
                var deck = CreateDeck(filePath, trackLoudnessLufs);
                deck.Gain!.Volume = 0f;
                deck.PendingTransition = transition;
                deck.PendingTransitionBpm = transitionBpm is > 0 ? transitionBpm.Value : 128.0;
                deck.PendingTransitionPresetName = presetName;
                deck.PendingSourceTriggerSeconds = sourceTriggerSeconds;
                deck.PendingTargetTriggerSeconds = targetTriggerSeconds;
                if (targetTriggerSeconds is > 0 && deck.AudioFile != null)
                {
                    // Not CurrentTime directly: on FLAC/M4A a seek on an unread file is dropped and
                    // the incoming track started at 0:00 instead of its mix-in point (ExactSeek).
                    deck.SeekInBackground(targetTriggerSeconds.Value);
                }
                lock (_deckLock)
                {
                    // A Stop()/Play() may have landed while the file was opening; never park a deck
                    // the service no longer has a current track for (it would play on promote).
                    if (_current == null || _next != null)
                    {
                        deck.Dispose();
                        return;
                    }
                    _next = deck;
                    _nextFilePath = filePath;
                }
                RefreshScheduledBusTransition();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[AudioPlayerService] Preload failed for {File}", filePath);
                lock (_deckLock)
                {
                    _next = null;
                    _nextFilePath = null;
                }
            }
        }

        /// <summary>
        /// Attaches (or clears) a Mix transition — including its analysis-suggested/saved
        /// trigger points — on the already-preloaded next deck, without reopening the file. Used
        /// when the file was already hot before the async saved-transition lookup
        /// (<see cref="ViewModels.PlayerViewModel.SchedulePreloadNext"/>) resolves — avoids a
        /// second PreloadNext call re-triggering CreateDeck's early-return guard for a filename
        /// that's already preloaded.
        /// </summary>
        public void SetPendingTransitionForNext(string filePath, SLSKDONET.Models.Timeline.TransitionModel? transition, double? transitionBpm,
            double? sourceTriggerSeconds = null, double? targetTriggerSeconds = null, string? presetName = null, double? outgoingBpm = null)
        {
            Deck? next;
            bool crossfading;
            lock (_deckLock)
            {
                if (_next == null || _nextFilePath != filePath) return;
                next = _next;
                crossfading = _isCrossfading;
            }

            next.PendingSourceBpm = outgoingBpm is > 0 ? outgoingBpm : null;
            next.PendingTransition = transition;
            next.PendingTransitionBpm = transitionBpm is > 0 ? transitionBpm.Value : 128.0;
            next.PendingTransitionPresetName = presetName;
            next.PendingSourceTriggerSeconds = sourceTriggerSeconds;
            next.PendingTargetTriggerSeconds = targetTriggerSeconds;

            // Guarded against re-seeking a deck that's already actively playing: this method is
            // called once the async saved/suggested-transition lookup resolves (see
            // PlayerViewModel.SchedulePreloadNext), which can land well after the crossfade has
            // already started for this exact deck if that lookup takes long enough (a real risk
            // now that the Auto-suggestion path does its own DB round-trips — see
            // PlayerViewModel.AttachSavedTransitionAsync). Seeking AudioFile.CurrentTime on a
            // NAudio device that's mid-playback doesn't just jump the position cleanly — it can
            // corrupt/stall the live output entirely, which showed up as "the incoming track sits
            // there and stops" right around when the crossfade finished. Once _isCrossfading is
            // true for this deck, it's too late to safely reposition it — better to let it keep
            // playing from wherever it already is than risk killing it.
            if (!crossfading && targetTriggerSeconds is > 0 && next.AudioFile != null)
            {
                var total = next.AudioFile.TotalTime.TotalSeconds;
                if (targetTriggerSeconds.Value < total)
                {
                    next.SeekInBackground(targetTriggerSeconds.Value);
                }
            }
            RefreshScheduledBusTransition();
        }

        /// <summary>Discards any preloaded next track (e.g. the queue changed before it was needed).</summary>
        public void CancelPreload()
        {
            Deck? next;
            lock (_deckLock)
            {
                next = _next;
                _next = null;
                _nextFilePath = null;
                _isCrossfading = false;
            }
            _mixBus.CancelScheduledTransition();
            next?.Dispose();
        }

        private void OpenDevice(string filePath, bool autoPlay, double? trackLoudnessLufs = null)
        {
            Stop();

            try
            {
                var deck = CreateDeck(filePath, trackLoudnessLufs);
                deck.Gain!.Volume = _masterVolumeFraction * deck.LoudnessGain;
                _mixBus.MasterVolume = _masterVolumeFraction;
                _mixBus.SetActiveDeck(deck.BusDeck, 0);
                _mixBus.IsPaused = !autoPlay;
                lock (_deckLock)
                {
                    _current = deck;
                }
                var output = EnsureSharedOutput();
                if (autoPlay) output.Play();
                LengthChanged?.Invoke(this, (long)deck.AudioFile!.TotalTime.TotalMilliseconds);
                PausableChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[AudioPlayerService] Playback error");
                throw;
            }
        }

        /// <summary>
        /// Builds an output device honoring the user's configured audio-output mode/device
        /// (Settings → Advanced → Audio Output — WaveOut/WASAPI-Shared/WASAPI-Exclusive/ASIO),
        /// falling back to the previous hardcoded WASAPI-Shared behavior if the configured
        /// device/driver fails to open (e.g. an ASIO driver that's since been uninstalled).
        /// </summary>
        private IWavePlayer CreateConfiguredOutputDevice(ISampleProvider source)
        {
            var mode = Enum.TryParse<AudioOutputMode>(_config.AudioOutputMode, out var parsed)
                ? parsed
                : AudioOutputMode.WasapiShared;
            var attempts = BuildOutputAttempts(mode, _config.AudioOutputDeviceName, AudioOutputBackend.Current);
            var output = OpenFirstAvailable(attempts, source, out var fallback);
            if (fallback != null)
            {
                Serilog.Log.Warning(fallback.Reason, "[AudioPlayerService] Configured output {Configured} failed; playing on {Fallback}",
                    fallback.Configured, fallback.Actual);
                OutputFellBack?.Invoke(this, fallback);
            }
            return output;
        }

        /// <summary>
        /// The outputs to try, in order, for the configured mode and device: the configured one, then
        /// the same device in WASAPI shared mode, then the OS default. Construction rarely fails —
        /// Init is where a busy/unsupported device throws, so <see cref="OpenFirstAvailable"/> includes
        /// Init in each attempt.
        /// <para>The shared-mode retry only applies to WASAPI device names. In ASIO mode the stored
        /// name is an ASIO <em>driver</em> name, which never matches a WASAPI endpoint, so that retry
        /// used to land silently on the Windows default device under a misleading label. An ASIO
        /// driver allows one stream, so this is exactly what happens to a mix's incoming deck.</para>
        /// </summary>
        internal static List<(string Label, Func<IWavePlayer> Create)> BuildOutputAttempts(
            AudioOutputMode mode, string? deviceName, IAudioOutputBackend backend)
        {
            var attempts = new List<(string Label, Func<IWavePlayer> Create)>
            {
                ($"{mode}/{deviceName ?? "default"}", () => backend.CreateDevice(mode, deviceName)),
            };
            if (mode is not (AudioOutputMode.WasapiShared or AudioOutputMode.Asio) && deviceName != null)
                attempts.Add(($"WasapiShared/{deviceName}", () => backend.CreateSharedDevice(deviceName, 50)));
            attempts.Add(("System default output", () => backend.CreateDefaultFallbackDevice(100)));
            return attempts;
        }

        /// <summary>Opens the first attempt whose device constructs and initialises. <paramref name="fallback"/>
        /// is set when that wasn't the first (configured) attempt. Throws when none opens.</summary>
        internal static IWavePlayer OpenFirstAvailable(
            IReadOnlyList<(string Label, Func<IWavePlayer> Create)> attempts, ISampleProvider source,
            out OutputFallbackEventArgs? fallback)
        {
            fallback = null;
            Exception? last = null;
            for (int i = 0; i < attempts.Count; i++)
            {
                var (label, create) = attempts[i];
                IWavePlayer? output = null;
                try
                {
                    output = create();
                    output.Init(source);
                    if (i > 0)
                    {
                        fallback = new OutputFallbackEventArgs
                        {
                            Configured = attempts[0].Label,
                            Actual = label,
                            ConfiguredIsAsio = attempts[0].Label.StartsWith(nameof(AudioOutputMode.Asio) + "/", StringComparison.Ordinal),
                            Reason = last,
                        };
                    }
                    return output;
                }
                catch (Exception ex)
                {
                    output?.Dispose();
                    last = ex;
                }
            }
            throw new InvalidOperationException("No audio output device could be opened.", last);
        }

        /// <summary>Opens the single shared audio output device feeding <see cref="_mixBus"/> (#600 MX1).</summary>
        private IWavePlayer EnsureSharedOutput()
        {
            if (_sharedOutput != null) return _sharedOutput;
            // Never touch output.Volume. On NAudio's WasapiOut that property is the DEVICE's Windows
            // master volume (AudioEndpointVolume), not a per-stream level. All real gain — master
            // volume, crossfades, loudness — goes through deck.Gain / _mixBus.MasterVolume.
            _sharedOutput = OutputFactory?.Invoke(_mixBus) ?? CreateConfiguredOutputDevice(_mixBus);
            return _sharedOutput;
        }

        /// <summary>
        /// Moves the single shared mix-bus output onto the output device now selected in Settings,
        /// keeping position and play/pause state.
        /// </summary>
        public void ApplyOutputSettings()
        {
            var old = _sharedOutput;
            if (old == null) return;
            try
            {
                bool wasPlaying = old.PlaybackState == PlaybackState.Playing && !_mixBus.IsPaused;
                var fresh = OutputFactory?.Invoke(_mixBus) ?? CreateConfiguredOutputDevice(_mixBus);
                _sharedOutput = fresh;
                try { old.Stop(); } catch { /* already stopped */ }
                old.Dispose();
                if (wasPlaying) fresh.Play();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[AudioPlayerService] Switching output device failed");
            }
        }

        /// <summary>
        /// Loudness normalization à la ReplayGain, using each track's already-analyzed integrated
        /// loudness (or a quick non-blocking stream estimate when LUFS hasn't been analysed yet).
        /// Deliberately attenuation-only (never boosts a quiet track above unity gain) — boosting
        /// risks clipping since there's no limiter in this signal chain, and every other player
        /// with this feature defaults the same way.
        /// </summary>
        internal static float ComputeLoudnessGainForTarget(bool enabled, double targetLufs, double? trackLoudnessLufs)
        {
            // AsLufs rejects legacy values (Essentia's 0–1 average_loudness was stored as "LUFS"
            // before re-analysis; treating 0.8 as LUFS cut every such track by ~15 dB).
            if (!enabled
                || SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(trackLoudnessLufs) is not double lufs)
                return 1f;

            var gainDb = Math.Min(0.0, targetLufs - lufs);
            return (float)Math.Pow(10.0, gainDb / 20.0);
        }

        private float ComputeLoudnessGain(double? trackLoudnessLufs) =>
            ComputeLoudnessGainForTarget(_config.LoudnessNormalizationEnabled, _config.LoudnessNormalizationTargetLufs, trackLoudnessLufs);

        private Deck CreateDeck(string filePath, double? trackLoudnessLufs = null)
        {
            // PlayableAudio: files Windows can't decode (some FLAC/Opus/Ogg) play from an
            // ffmpeg-decoded WAV instead of failing to load. FilePath is what is actually read,
            // so ExactSeek knows whether a decode-forward seek is needed.
            var reader = SLSKDONET.Services.Audio.PlayableAudio.Open(filePath, out var playablePath);
            double? effectiveLufs = trackLoudnessLufs;
            if (_config.LoudnessNormalizationEnabled
                && SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(effectiveLufs) is null)
            {
                try
                {
                    effectiveLufs = SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter
                        .EstimateQuickLufsFromStream(reader);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[AudioPlayerService] Quick loudness estimate failed for {File}; using unity gain.", filePath);
                }
            }

            var deck = new Deck
            {
                FilePath = playablePath,
                AudioFile = reader,
                LoudnessGain = ComputeLoudnessGain(effectiveLufs)
            };

            // Set up channel and resampler for pitch (turntable style)
            var sampleChannel = new SampleChannel(deck.AudioFile, true);

            // 0. Vari-speed for pitch control: reads the channel at a variable rate via linear
            // interpolation, so speeding up/slowing down raises/lowers pitch just like a real
            // turntable/CDJ pitch fader (as opposed to tempo-only time-stretching).
            deck.VariSpeed = new VariSpeedSampleProvider(sampleChannel) { Speed = _pitch };

            // 0.5. Live per-band gain stage — inert (Active=false) outside a Mix transition
            // window; AdvanceCrossfade turns it on and drives its gains for presets that need
            // more than a plain volume crossfade (Blend/Wave/Melt).
            deck.Eq = new ThreeBandGainProvider(deck.VariSpeed);

            // 0.6. Metronome click (Cue Forge audition) — always in the chain, pass-through until enabled.
            deck.Metronome = new SLSKDONET.Services.Audio.MetronomeSampleProvider(deck.Eq, Array.Empty<double>(), 0);

            // 0.75. In-process gain stage — see the Deck.Gain field doc for why this, and not
            // Output.Volume, is what master volume/crossfade/loudness-normalization drive.
            deck.Gain = new NAudio.Wave.SampleProviders.VolumeSampleProvider(deck.Metronome) { Volume = 1f };

            // 1. Intercept for FFT (Spectrum). Only forwarded upstream while this deck is the
            // active one, so a preloaded/promoted deck seamlessly takes over the visualizer.
            var fftProvider = new FftSampleProvider(deck.Gain, 2048, magnitudes =>
            {
                if (ReferenceEquals(_current, deck)) SpectrumChanged?.Invoke(this, magnitudes);
            }, isActive: () => IsVisualizerActive,
            onWaveform: samples =>
            {
                if (ReferenceEquals(_current, deck)) WaveformChanged?.Invoke(this, samples);
            });

            // 2. Wrap in Metering for VU
            deck.Metering = new MeteringSampleProvider(fftProvider);
            deck.Metering.StreamVolume += (s, e) =>
            {
                if (!ReferenceEquals(_current, deck)) return;

                // Throttle VU meter updates to reduce event marshalling overhead
                _vuMeterSkipCounter++;
                if (_vuMeterSkipCounter >= VU_METER_SKIP_FRAMES)
                {
                    _vuMeterSkipCounter = 0;
                    AudioLevelsChanged?.Invoke(this, new AudioLevelsEventArgs
                    {
                        Left = e.MaxSampleValues[0],
                        Right = e.MaxSampleValues.Length > 1 ? e.MaxSampleValues[1] : e.MaxSampleValues[0]
                    });
                }
            };

            deck.BusDeck = new MixBusDeck
            {
                Source = _mixBus.AdaptToBusFormat(deck.Metering),
                Gain = deck.Gain,
                Eq = deck.Eq,
                LoudnessGain = deck.LoudnessGain,
                PrepareBeforeStart = () =>
                {
                    deck.WaitForSeek();
                    Deck? cur;
                    lock (_deckLock) cur = _current;
                    if (cur != null && !ReferenceEquals(cur, deck))
                        ApplyTempoSync(cur, deck);
                    double inSpeed = Math.Max(0.01, deck.VariSpeed?.Speed ?? 1.0);
                    double inSec = deck.AudioFile?.CurrentTime.TotalSeconds ?? 0;
                    deck.BusDeck!.InitialFrameOffset = (long)Math.Round((inSec / inSpeed) * _mixBus.WaveFormat.SampleRate);
                },
                Tag = deck,
            };
            deck.OnDisposed = ForgetDeck;
            lock (_deckLock)
            {
                _liveDecks.Add(deck);
            }
            return deck;
        }

        /// <summary>Makes the preloaded deck the active one and disposes the deck that just finished.</summary>
        private void PromoteNextDeck(Deck finishedDeck)
        {
            Deck? promoted;
            lock (_deckLock)
            {
                // Only the deck that is actually current may hand over. A PlaybackStopped for an
                // already-replaced deck must never touch _next: it belongs to whatever plays now.
                if (!ReferenceEquals(_current, finishedDeck)) return;
                if (finishedDeck.IsDisposed) return;

                promoted = _next;
                _next = null;
                _nextFilePath = null;
                if (promoted?.Output == null) return;

                _current = promoted;
            }

            if (promoted.Gain != null) promoted.Gain.Volume = _masterVolumeFraction * promoted.LoudnessGain;
            if (promoted.Output.PlaybackState != PlaybackState.Playing)
            {
                promoted.WaitForSeek();
                promoted.Output.Play();
            }

            LengthChanged?.Invoke(this, (long)(promoted.AudioFile?.TotalTime.TotalMilliseconds ?? 0));

            // Disposing the finished deck's WasapiOut synchronously, here, on the timer thread —
            // in the same call stack that just started/confirmed the newly-promoted deck's own
            // WasapiOut is playing — has been observed to silently kill the PROMOTED deck's
            // playback moments later: its PlaybackState keeps reporting Playing and its volume
            // stays at 1, but AudioFile.CurrentTime simply stops advancing forever (confirmed via
            // live logging — position frozen at the exact promotion timestamp, five-plus minutes
            // later, with no further PlaybackStopped/EndReached ever firing). Deck.Dispose() calls
            // Output.Stop() first, which blocks waiting for that WasapiOut's dedicated event-sync
            // thread to exit — doing that immediately adjacent to another WasapiOut instance
            // starting up on the timer thread is exactly the kind of ordering NAudio/WASAPI is
            // fragile about. Deferring disposal to a background thread, decoupled from this call
            // stack, avoids whatever race that ordering was hitting.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    finishedDeck.Dispose();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[AudioPlayerService] Deferred disposal of finished deck failed");
                }
            });

            TrackAdvanced?.Invoke(this, EventArgs.Empty);
        }

        // Custom FFT Provider (Inline for simplicity or could be moved)
        /// <summary>
        /// Taps the playing signal for the visualizers: a mono downmix, windowed and FFT'd off the
        /// audio thread. The downmix matters — this used to FFT the raw interleaved stereo stream
        /// (L,R,L,R…), which folds the two channels into one sequence and scrambles the frequency
        /// axis every visualizer reads. Also hands out the time-domain block (oscilloscope preset).
        /// </summary>
        private class FftSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _fftSize;
            private readonly int _channels;
            private readonly Action<float[]> _onFftCalculated;
            private readonly Action<float[]>? _onWaveform;
            private readonly float[] _buffer;
            private readonly float[] _processingBuffer;
            private readonly System.Numerics.Complex[] _complexBuffer;
            private readonly float[] _window;
            private int _pos;
            private int _fftBusy;
            private float _frameSum;
            private int _frameChannel;
            private readonly Func<bool> _isActive;

            public WaveFormat WaveFormat => _source.WaveFormat;

            public FftSampleProvider(ISampleProvider source, int fftSize, Action<float[]> onFftCalculated, Func<bool>? isActive = null, Action<float[]>? onWaveform = null)
            {
                _source = source;
                _fftSize = fftSize;
                _channels = Math.Max(1, source.WaveFormat.Channels);
                _onFftCalculated = onFftCalculated;
                _onWaveform = onWaveform;
                _buffer = new float[fftSize];
                _processingBuffer = new float[fftSize];
                _complexBuffer = new System.Numerics.Complex[fftSize];
                _window = new float[fftSize];
                for (int i = 0; i < fftSize; i++)
                    _window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (fftSize - 1)))); // Hann
                _isActive = isActive ?? (() => true);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);

                // No visualizer attached — skip the work, and restart from a clean window later.
                if (!_isActive())
                {
                    _pos = 0;
                    _frameSum = 0;
                    _frameChannel = 0;
                    return read;
                }

                for (int i = 0; i < read; i++)
                {
                    // Frames can straddle Read calls, so the channel position carries over.
                    _frameSum += buffer[offset + i];
                    if (++_frameChannel < _channels) continue;

                    _buffer[_pos++] = _frameSum / _channels;
                    _frameSum = 0;
                    _frameChannel = 0;

                    if (_pos >= _fftSize)
                    {
                        // Skip this block if the previous FFT is still running.
                        if (System.Threading.Interlocked.CompareExchange(ref _fftBusy, 1, 0) == 0)
                        {
                            Array.Copy(_buffer, _processingBuffer, _fftSize);
                            _ = System.Threading.Tasks.Task.Run(PerformFft);
                        }
                        // 50% overlap: keep the second half as the start of the next window, so a
                        // new spectrum arrives every ~23 ms instead of every ~46 ms.
                        int half = _fftSize / 2;
                        Array.Copy(_buffer, half, _buffer, 0, half);
                        _pos = half;
                    }
                }

                return read;
            }

            private void PerformFft()
            {
                try
                {
                    _onWaveform?.Invoke((float[])_processingBuffer.Clone());

                    for (int i = 0; i < _fftSize; i++)
                        _complexBuffer[i] = new System.Numerics.Complex(_processingBuffer[i] * _window[i], 0);

                    MathNet.Numerics.IntegralTransforms.Fourier.Forward(_complexBuffer, MathNet.Numerics.IntegralTransforms.FourierOptions.NoScaling);

                    var magnitude = new float[_fftSize / 2];
                    for (int i = 0; i < magnitude.Length; i++)
                        magnitude[i] = (float)_complexBuffer[i].Magnitude;

                    _onFftCalculated(magnitude);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "Visualizer FFT block failed");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _fftBusy, 0);
                }
            }
        }

        /// <summary>
        /// Reads its source at a variable rate using linear interpolation between samples,
        /// producing a turntable/CDJ-style varispeed effect: <see cref="Speed"/> above 1.0 plays
        /// faster and higher-pitched, below 1.0 slower and lower-pitched — speed and pitch always
        /// move together, exactly like physically speeding up or slowing down a record. Speed can
        /// be changed at any time (e.g. from a live slider) with no audible discontinuity.
        /// </summary>
        internal class VariSpeedSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _channels;
            private float[] _sourceBuffer = Array.Empty<float>();
            private int _sourceFrameCount; // valid frames currently held in _sourceBuffer
            private double _readPosition;  // fractional frame index into _sourceBuffer
            private bool _sourceExhausted;

            /// <summary>1.0 = normal speed/pitch. Typical DJ pitch-fader range is ~0.92–1.08.</summary>
            public double Speed { get; set; } = 1.0;

            public WaveFormat WaveFormat => _source.WaveFormat;

            public VariSpeedSampleProvider(ISampleProvider source)
            {
                _source = source;
                _channels = source.WaveFormat.Channels;
            }

            /// <summary>Discards buffered samples so the next Read() starts fresh from wherever
            /// the underlying source now is. Must be called after seeking the source directly
            /// (e.g. AudioFileReader.Position), otherwise stale buffered samples from the old
            /// position would play briefly before catching up.</summary>
            public void Reset()
            {
                _sourceFrameCount = 0;
                _readPosition = 0;
                _sourceExhausted = false;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                var speed = Speed <= 0 ? 1.0 : Speed;
                int framesRequested = count / _channels;
                int framesWritten = 0;

                while (framesWritten < framesRequested)
                {
                    int baseFrame = (int)_readPosition;
                    if (baseFrame + 1 >= _sourceFrameCount && !RefillBuffer(speed, framesRequested - framesWritten))
                    {
                        break; // source exhausted — return what we've produced so far
                    }

                    baseFrame = (int)_readPosition;
                    double frac = _readPosition - baseFrame;

                    for (int ch = 0; ch < _channels; ch++)
                    {
                        float s0 = _sourceBuffer[baseFrame * _channels + ch];
                        float s1 = _sourceBuffer[(baseFrame + 1) * _channels + ch];
                        buffer[offset + framesWritten * _channels + ch] = (float)(s0 + (s1 - s0) * frac);
                    }

                    framesWritten++;
                    _readPosition += speed;
                }

                return framesWritten * _channels;
            }

            /// <summary>Drops already-consumed frames, then tops up the buffer from the source.
            /// Returns false once the source has no more data and the buffer can't satisfy
            /// another interpolated frame.</summary>
            private bool RefillBuffer(double speed, int framesStillNeeded)
            {
                int consumedWholeFrames = Math.Min((int)_readPosition, _sourceFrameCount);
                if (consumedWholeFrames > 0)
                {
                    int keepFrames = _sourceFrameCount - consumedWholeFrames;
                    if (keepFrames > 0)
                    {
                        Array.Copy(_sourceBuffer, consumedWholeFrames * _channels, _sourceBuffer, 0, keepFrames * _channels);
                    }
                    _sourceFrameCount = keepFrames;
                    _readPosition -= consumedWholeFrames;
                }

                if (_sourceExhausted)
                {
                    return (int)_readPosition + 1 < _sourceFrameCount;
                }

                int framesToRead = Math.Max(256, (int)Math.Ceiling(framesStillNeeded * speed) + 8);
                int requiredCapacityFrames = _sourceFrameCount + framesToRead;
                if (_sourceBuffer.Length < requiredCapacityFrames * _channels)
                {
                    Array.Resize(ref _sourceBuffer, requiredCapacityFrames * _channels);
                }

                int samplesRead = _source.Read(_sourceBuffer, _sourceFrameCount * _channels, framesToRead * _channels);
                int framesRead = samplesRead / _channels;
                _sourceFrameCount += framesRead;
                if (framesRead == 0) _sourceExhausted = true;

                return (int)_readPosition + 1 < _sourceFrameCount;
            }
        }

        public void Pause()
        {
            Deck? current, next;
            lock (_deckLock)
            {
                current = _current;
                next = _next;
            }
            if (current == null) return;

            if (_sharedOutput != null && current.BusDeck != null)
            {
                bool wasPlaying = _sharedOutput.PlaybackState == PlaybackState.Playing && !_mixBus.IsPaused;
                if (wasPlaying)
                {
                    _mixBus.IsPaused = true;
                    _sharedOutput.Pause();
                }
                else
                {
                    current.WaitForSeek();
                    _mixBus.IsPaused = false;
                    _sharedOutput.Play();
                }
                PausableChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (current.Output == null) return;

            bool legacyPlaying = current.Output.PlaybackState == PlaybackState.Playing;
            if (legacyPlaying)
            {
                current.Output.Pause();
            }
            else
            {
                current.WaitForSeek();
                current.Output.Play();
            }

            if (next?.Output != null)
            {
                if (legacyPlaying && next.Output.PlaybackState == PlaybackState.Playing) next.Output.Pause();
                else if (!legacyPlaying && next.Output.PlaybackState == PlaybackState.Paused) next.Output.Play();
            }

            PausableChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Stops playback and releases every deck. Detaches the active/scheduled decks from the
        /// unified mix bus first so no further samples are pulled while disposing readers.
        /// </summary>
        public void Stop()
        {
            Deck? current, next;
            List<Deck> strays;
            lock (_deckLock)
            {
                _stopping = true;
                current = _current;
                next = _next;
                _current = null;
                _next = null;
                _nextFilePath = null;
                _isCrossfading = false;
                _crossfadeProgress = 0;

                // Safety net for the whole bug class: anything this service created and hasn't
                // disposed yet is stopped too, even if some code path lost its reference.
                strays = _liveDecks.Where(d => !ReferenceEquals(d, current) && !ReferenceEquals(d, next)).ToList();
            }

            _mixBus.IsPaused = true;
            _mixBus.CancelScheduledTransition();
            _mixBus.SetActiveDeck(null, 0);
            try { _sharedOutput?.Stop(); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[AudioPlayerService] Shared output stop during Stop()"); }

            try
            {
                current?.Dispose();
                next?.Dispose();
                foreach (var stray in strays)
                {
                    stray.Dispose();
                }
            }
            finally
            {
                lock (_deckLock) { _stopping = false; }
            }

            PausableChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Drops a disposed deck from the live set (called from Deck.Dispose via the service).</summary>
        private void ForgetDeck(Deck deck)
        {
            lock (_deckLock)
            {
                _liveDecks.Remove(deck);
            }
        }

        public void Dispose()
        {
            Stop();
            try { _sharedOutput?.Dispose(); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[AudioPlayerService] Shared output dispose during Dispose()"); }
            _sharedOutput = null;
            _timer?.Stop();
            _timer?.Dispose();
        }
    }
}
