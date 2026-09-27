using System;
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
        private class Deck : IDisposable
        {
            public AudioFileReader? AudioFile;
            public IWavePlayer? Output;
            public MeteringSampleProvider? Metering;
            public VariSpeedSampleProvider? VariSpeed;
            public ThreeBandGainProvider? Eq;
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

            public void Dispose()
            {
                try { Output?.Stop(); } catch { /* already stopped/disposed */ }
                AudioFile?.Dispose();
                Output?.Dispose();
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
        private sealed class ThreeBandGainProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly float _lowCrossoverHz;
            private readonly float _highCrossoverHz;
            private float[] _lowState = Array.Empty<float>();
            private float[] _midHighSplitState = Array.Empty<float>();

            public volatile bool Active;
            public float LowGain = 1f, MidGain = 1f, HighGain = 1f;

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
        private double _crossfadeElapsedSeconds;
        private double _crossfadeProgress;
        private float _masterVolumeFraction = 1f;

        private bool _isInitialized;
        private System.Timers.Timer? _timer;

        public event EventHandler<long>? TimeChanged;
        public event EventHandler<float>? PositionChanged;
        public event EventHandler<long>? LengthChanged;
        public event EventHandler<AudioLevelsEventArgs>? AudioLevelsChanged;
        public event EventHandler<float[]>? SpectrumChanged;
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
            }
        }

        /// <summary>When enabled, the preloaded next track fades in while the current track
        /// fades out over <see cref="CrossfadeSeconds"/>, instead of a hard gapless cut.</summary>
        public bool CrossfadeEnabled { get; set; } = false;

        /// <summary>Length of the crossfade overlap, in seconds. Only used when
        /// <see cref="CrossfadeEnabled"/> is true.</summary>
        public double CrossfadeSeconds { get; set; } = 3.0;

        public AudioPlayerService(AppConfig config)
        {
            _config = config;
            _isInitialized = true;
            _timer = new System.Timers.Timer(TimerIntervalSeconds * 1000);
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            var current = _current;
            if (current?.AudioFile == null || current.Output?.PlaybackState != PlaybackState.Playing)
                return;

            TimeChanged?.Invoke(this, (long)current.AudioFile.CurrentTime.TotalMilliseconds);
            PositionChanged?.Invoke(this, (float)(current.AudioFile.Position / (double)current.AudioFile.Length));

            if (_isCrossfading)
            {
                AdvanceCrossfade(current);
                return;
            }

            // A saved Mix transition (see PlayerViewModel.SchedulePreloadNext) overrides the
            // legacy fixed-duration global crossfade — it applies regardless of the
            // CrossfadeEnabled toggle, since choosing a transition for this specific pair is a
            // more specific instruction than the app-wide default.
            var pendingTransition = _next?.PendingTransition;
            var effectiveCrossfadeSeconds = pendingTransition != null
                ? pendingTransition.DurationBeats * (60.0 / _next!.PendingTransitionBpm)
                : CrossfadeSeconds;

            if ((CrossfadeEnabled || pendingTransition != null) && _next?.Output != null)
            {
                bool shouldStart;
                if (_next.PendingSourceTriggerSeconds is double sourceTrigger)
                {
                    // Analysis-suggested/saved mix-out point: an absolute position in THIS track,
                    // not "however many seconds are left" — a mix-out near a phrase boundary well
                    // before the literal end of a track with a long fade-out tail, for instance.
                    shouldStart = current.AudioFile.CurrentTime.TotalSeconds >= sourceTrigger;
                }
                else
                {
                    var remaining = current.AudioFile.TotalTime - current.AudioFile.CurrentTime;
                    shouldStart = remaining.TotalSeconds <= effectiveCrossfadeSeconds;
                }

                if (shouldStart)
                {
                    _isCrossfading = true;
                    _crossfadeElapsedSeconds = 0;
                    _crossfadeProgress = 0;
                    if (_next.Gain != null) _next.Gain.Volume = 0f;
                    if (_next.Eq != null) _next.Eq.Active = pendingTransition != null;
                    if (current.Eq != null) current.Eq.Active = pendingTransition != null;
                    _next.Output.Play();

                    CrossfadeStarted?.Invoke(this, new CrossfadeStartedEventArgs
                    {
                        PresetName = _next.PendingTransitionPresetName,
                        DurationSeconds = effectiveCrossfadeSeconds,
                    });
                }
            }
        }

        private static readonly TransitionEngine _liveTransitionEngine = new();

        private void AdvanceCrossfade(Deck current)
        {
            if (_next?.Output == null)
            {
                var wasCrossfading = _isCrossfading;
                _isCrossfading = false;
                _crossfadeProgress = 0;
                if (wasCrossfading) CrossfadeEnded?.Invoke(this, EventArgs.Empty);
                return;
            }

            var pendingTransition = _next.PendingTransition;
            var durationSeconds = pendingTransition != null
                ? pendingTransition.DurationBeats * (60.0 / _next.PendingTransitionBpm)
                : CrossfadeSeconds;

            _crossfadeElapsedSeconds += TimerIntervalSeconds;
            var t = durationSeconds > 0 ? Math.Clamp(_crossfadeElapsedSeconds / durationSeconds, 0.0, 1.0) : 1.0;

            float currentGain, nextGain;

            if (pendingTransition != null)
            {
                // Preset-accurate automation — the same TransitionEngine math the Mix editor's
                // waveform overlay curves are sampled from, so what plays matches what was previewed.
                const int samplePoints = 1000;
                var region = new SLSKDONET.Services.Audio.TransitionRegion
                {
                    StartSample = 0,
                    EndSample = samplePoints,
                    Type = pendingTransition.Type.ToAutomationType(),
                    Curve = SLSKDONET.Services.Audio.TransitionCurve.SCurve,
                    WaveDuckDepth = pendingTransition.WaveDuckDepth,
                    EchoDecayFactor = pendingTransition.EchoDecayFactor,
                    EqConfig = new SLSKDONET.Services.Audio.EqBandSwapConfig
                    {
                        SwapLow = pendingTransition.EqSwapLow,
                        SwapMid = pendingTransition.EqSwapMid,
                        SwapHigh = pendingTransition.EqSwapHigh,
                        LowCrossover = pendingTransition.EqLowCrossoverHz,
                        HighCrossover = pendingTransition.EqHighCrossoverHz,
                    },
                };
                var automation = _liveTransitionEngine.CalculateAutomation(region, (long)(t * samplePoints));

                currentGain = _masterVolumeFraction * current.LoudnessGain * automation.OutgoingGain;
                nextGain = _masterVolumeFraction * _next.LoudnessGain * automation.IncomingGain;

                if (current.Eq != null) { current.Eq.LowGain = automation.OutgoingLowGain; current.Eq.MidGain = automation.OutgoingMidGain; current.Eq.HighGain = automation.OutgoingHighGain; }
                if (_next.Eq != null) { _next.Eq.LowGain = automation.IncomingLowGain; _next.Eq.MidGain = automation.IncomingMidGain; _next.Eq.HighGain = automation.IncomingHighGain; }
            }
            else
            {
                // Legacy fixed equal-power crossfade curve (constant perceived loudness through
                // the overlap, unlike a linear fade which dips in the middle) — unchanged
                // behavior for playlists that haven't saved a Mix transition.
                currentGain = (float)(_masterVolumeFraction * current.LoudnessGain * Math.Cos(t * Math.PI / 2));
                nextGain = (float)(_masterVolumeFraction * _next.LoudnessGain * Math.Sin(t * Math.PI / 2));
            }

            if (current.Gain != null) current.Gain.Volume = currentGain;
            if (_next.Gain != null) _next.Gain.Volume = nextGain;

            _crossfadeProgress = t;
            CrossfadeProgressChanged?.Invoke(this, t);

            if (t >= 1.0)
            {
                _isCrossfading = false;
                _crossfadeElapsedSeconds = 0;
                _crossfadeProgress = 0;
                if (current.Eq != null) current.Eq.Active = false;
                if (_next.Eq != null) { _next.Eq.Active = false; _next.Eq.LowGain = _next.Eq.MidGain = _next.Eq.HighGain = 1f; }
                if (_current != null) PromoteNextDeck(_current);
                CrossfadeEnded?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsInitialized => _isInitialized;
        public bool IsPlaying => _current?.Output?.PlaybackState == PlaybackState.Playing;
        public long Length => (long)(_current?.AudioFile?.TotalTime.TotalMilliseconds ?? 0);
        public double Duration => _current?.AudioFile?.TotalTime.TotalSeconds ?? 0;
        public long Time => (long)(_current?.AudioFile?.CurrentTime.TotalMilliseconds ?? 0);

        public float Position
        {
            get => (float)(_current?.AudioFile != null ? _current.AudioFile.Position / (double)_current.AudioFile.Length : 0);
            set
            {
                if (_current?.AudioFile != null)
                {
                    _current.AudioFile.Position = (long)(value * _current.AudioFile.Length);
                    _current.VariSpeed?.Reset(); // discard stale buffered samples from before the seek
                }
            }
        }

        public int Volume
        {
            get => (int)(_masterVolumeFraction * 100);
            set
            {
                _masterVolumeFraction = Math.Clamp(value / 100f, 0f, 1f);
                // While crossfading, the fade envelope owns each deck's volume; the new
                // master level takes effect once the crossfade finishes (see AdvanceCrossfade).
                if (_current?.Gain != null && !_isCrossfading)
                {
                    _current.Gain.Volume = _masterVolumeFraction * _current.LoudnessGain;
                }
            }
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
            if (_current == null) return;
            if (_nextFilePath == filePath && _next != null) return; // already preloaded

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
                    deck.AudioFile.CurrentTime = TimeSpan.FromSeconds(targetTriggerSeconds.Value);
                    deck.VariSpeed?.Reset();
                }
                _next = deck;
                _nextFilePath = filePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioPlayerService] Preload failed for {filePath}: {ex.Message}");
                _next = null;
                _nextFilePath = null;
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
            double? sourceTriggerSeconds = null, double? targetTriggerSeconds = null, string? presetName = null)
        {
            if (_next == null || _nextFilePath != filePath) return;
            _next.PendingTransition = transition;
            _next.PendingTransitionBpm = transitionBpm is > 0 ? transitionBpm.Value : 128.0;
            _next.PendingTransitionPresetName = presetName;
            _next.PendingSourceTriggerSeconds = sourceTriggerSeconds;
            _next.PendingTargetTriggerSeconds = targetTriggerSeconds;

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
            if (!_isCrossfading && targetTriggerSeconds is > 0 && _next.AudioFile != null)
            {
                var total = _next.AudioFile.TotalTime.TotalSeconds;
                if (targetTriggerSeconds.Value < total)
                {
                    _next.AudioFile.CurrentTime = TimeSpan.FromSeconds(targetTriggerSeconds.Value);
                    _next.VariSpeed?.Reset();
                }
            }
        }

        /// <summary>Discards any preloaded next track (e.g. the queue changed before it was needed).</summary>
        public void CancelPreload()
        {
            _next?.Dispose();
            _next = null;
            _nextFilePath = null;
            _isCrossfading = false;
            _crossfadeElapsedSeconds = 0;
        }

        private void OpenDevice(string filePath, bool autoPlay, double? trackLoudnessLufs = null)
        {
            Stop();

            try
            {
                _current = CreateDeck(filePath, trackLoudnessLufs);
                _current.Gain!.Volume = _masterVolumeFraction * _current.LoudnessGain;
                if (autoPlay) _current.Output.Play();
                LengthChanged?.Invoke(this, (long)_current.AudioFile!.TotalTime.TotalMilliseconds);
                PausableChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioPlayerService] Playback error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Builds an output device honoring the user's configured audio-output mode/device
        /// (Settings → Advanced → Audio Output — WaveOut/WASAPI-Shared/WASAPI-Exclusive/ASIO),
        /// falling back to the previous hardcoded WASAPI-Shared behavior if the configured
        /// device/driver fails to open (e.g. an ASIO driver that's since been uninstalled).
        /// </summary>
        private IWavePlayer CreateConfiguredOutputDevice()
        {
            var mode = Enum.TryParse<AudioOutputMode>(_config.AudioOutputMode, out var parsed)
                ? parsed
                : AudioOutputMode.WasapiShared;

            try
            {
                return AudioOutputProvider.CreateDevice(mode, _config.AudioOutputDeviceName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioPlayerService] Failed to open configured output device ({mode}/{_config.AudioOutputDeviceName ?? "default"}): {ex.Message}. Falling back to WASAPI Shared.");
                return new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
            }
        }

        /// <summary>
        /// Loudness normalization à la ReplayGain, using each track's already-analyzed integrated
        /// loudness instead of a separate scan pass. Deliberately attenuation-only (never boosts
        /// a quiet track above unity gain) — boosting risks clipping since there's no limiter in
        /// this signal chain, and every other player with this feature defaults the same way.
        /// </summary>
        private float ComputeLoudnessGain(double? trackLoudnessLufs)
        {
            if (!_config.LoudnessNormalizationEnabled || trackLoudnessLufs is not double lufs)
                return 1f;

            var gainDb = Math.Min(0.0, _config.LoudnessNormalizationTargetLufs - lufs);
            return (float)Math.Pow(10.0, gainDb / 20.0);
        }

        private Deck CreateDeck(string filePath, double? trackLoudnessLufs = null)
        {
            var deck = new Deck
            {
                AudioFile = new AudioFileReader(filePath),
                LoudnessGain = ComputeLoudnessGain(trackLoudnessLufs)
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

            // 0.75. In-process gain stage — see the Deck.Gain field doc for why this, and not
            // Output.Volume, is what master volume/crossfade/loudness-normalization drive.
            deck.Gain = new NAudio.Wave.SampleProviders.VolumeSampleProvider(deck.Eq) { Volume = 1f };

            // 1. Intercept for FFT (Spectrum). Only forwarded upstream while this deck is the
            // active one, so a preloaded/promoted deck seamlessly takes over the visualizer.
            var fftProvider = new FftSampleProvider(deck.Gain, 2048, magnitudes =>
            {
                if (ReferenceEquals(_current, deck)) SpectrumChanged?.Invoke(this, magnitudes);
            }, isActive: () => IsVisualizerActive);

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

            deck.Output = CreateConfiguredOutputDevice();
            deck.Output.Init(deck.Metering);
            // Pinned forever — see Deck.Gain field doc. All real gain automation goes through
            // deck.Gain (an in-process sample multiply), never through the OS session volume.
            deck.Output.Volume = 1f;
            deck.Output.PlaybackStopped += (s, e) =>
            {
                if (!ReferenceEquals(_current, deck)) return; // stale event from a deck we've already advanced past
                if (_isCrossfading) return; // the crossfade timer owns this transition

                if (_next != null)
                {
                    PromoteNextDeck(deck);
                }
                else
                {
                    EndReached?.Invoke(this, EventArgs.Empty);
                }
            };

            return deck;
        }

        /// <summary>Makes the preloaded deck the active one and disposes the deck that just finished.</summary>
        private void PromoteNextDeck(Deck finishedDeck)
        {
            var promoted = _next;
            _next = null;
            _nextFilePath = null;
            if (promoted?.Output == null)
            {
                return;
            }

            _current = promoted;
            if (promoted.Gain != null) promoted.Gain.Volume = _masterVolumeFraction * promoted.LoudnessGain;
            if (promoted.Output.PlaybackState != PlaybackState.Playing)
            {
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
                    Console.WriteLine($"[AudioPlayerService] Deferred disposal of finished deck failed: {ex.Message}");
                }
            });

            TrackAdvanced?.Invoke(this, EventArgs.Empty);
        }

        // Custom FFT Provider (Inline for simplicity or could be moved)
        private class FftSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _fftSize;
            private readonly Action<float[]> _onFftCalculated;
            private readonly float[] _buffer;
            private float[] _processingBuffer;
            private int _pos;
            private readonly System.Numerics.Complex[] _complexBuffer;
            private int _fftBusy = 0;
            private readonly Func<bool> _isActive;

            public WaveFormat WaveFormat => _source.WaveFormat;

            public FftSampleProvider(ISampleProvider source, int fftSize, Action<float[]> onFftCalculated, Func<bool>? isActive = null)
            {
                _source = source;
                _fftSize = fftSize;
                _onFftCalculated = onFftCalculated;
                _buffer = new float[fftSize];
                _processingBuffer = new float[fftSize];
                _complexBuffer = new System.Numerics.Complex[fftSize];
                _isActive = isActive ?? (() => true);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);

                // No visualizer control is attached/visible right now — skip the windowing +
                // MathNet FFT dispatch entirely (this ran unconditionally for every playing
                // track before, whether or not anything was ever going to render it). Keep
                // _pos at 0 while inactive so re-activating starts a clean window instead of
                // bursting out a stale, partially-filled buffer from whenever it was last active.
                if (!_isActive())
                {
                    _pos = 0;
                    return read;
                }

                for (int i = 0; i < read; i++)
                {
                    _buffer[_pos] = buffer[offset + i];
                    _pos++;

                    if (_pos >= _fftSize)
                    {
                        // Use lock-free check: only start FFT if not already running
                        if (System.Threading.Interlocked.CompareExchange(ref _fftBusy, 1, 0) == 0)
                        {
                            // Copy buffer data (audio thread writes to _buffer, FFT reads from _processingBuffer)
                            Array.Copy(_buffer, _processingBuffer, _fftSize);

                            // Fire-and-forget: run FFT on background thread
                            _ = System.Threading.Tasks.Task.Run(() => PerformFftAsync());
                        }
                        // else: skip this FFT cycle if previous one still processing

                        _pos = 0;
                    }
                }

                return read;
            }

            private void PerformFftAsync()
            {
                try
                {
                    // Work on _processingBuffer (swapped with _buffer)
                    for (int i = 0; i < _fftSize; i++)
                    {
                        // Apply Hanning Window to reduce leakage
                        float window = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (_fftSize - 1))));
                        _complexBuffer[i] = new System.Numerics.Complex(_processingBuffer[i] * window, 0);
                    }

                    // Use MathNet.Numerics for FFT
                    MathNet.Numerics.IntegralTransforms.Fourier.Forward(_complexBuffer, MathNet.Numerics.IntegralTransforms.FourierOptions.NoScaling);

                    var magnitude = new float[_fftSize / 2];
                    for (int i = 0; i < magnitude.Length; i++)
                    {
                        magnitude[i] = (float)_complexBuffer[i].Magnitude;
                    }

                    _onFftCalculated(magnitude);
                }
                finally
                {
                    // Release the busy flag
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
            if (_current?.Output == null) return;

            bool wasPlaying = _current.Output.PlaybackState == PlaybackState.Playing;
            if (wasPlaying) _current.Output.Pause();
            else if (_current.Output.PlaybackState == PlaybackState.Paused) _current.Output.Play();

            // Keep a preloaded/crossfading deck in lockstep so it doesn't keep playing silently
            // (or fail to resume) independently of the current one.
            if (_next?.Output != null)
            {
                if (wasPlaying && _next.Output.PlaybackState == PlaybackState.Playing) _next.Output.Pause();
                else if (!wasPlaying && _next.Output.PlaybackState == PlaybackState.Paused) _next.Output.Play();
            }

            PausableChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            _current?.Dispose();
            _current = null;
            CancelPreload();
            PausableChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Stop();
            _timer?.Stop();
            _timer?.Dispose();
        }
    }
}
