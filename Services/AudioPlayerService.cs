using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Timers;

namespace SLSKDONET.Services
{
    public class AudioPlayerService : IAudioPlayerService, IDisposable
    {
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

            public void Dispose()
            {
                try { Output?.Stop(); } catch { /* already stopped/disposed */ }
                AudioFile?.Dispose();
                Output?.Dispose();
            }
        }

        private Deck? _current;
        private Deck? _next;
        private string? _nextFilePath;
        private bool _isCrossfading;
        private double _crossfadeElapsedSeconds;
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

        public AudioPlayerService()
        {
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

            if (CrossfadeEnabled && _next?.Output != null)
            {
                var remaining = current.AudioFile.TotalTime - current.AudioFile.CurrentTime;
                if (remaining.TotalSeconds <= CrossfadeSeconds)
                {
                    _isCrossfading = true;
                    _crossfadeElapsedSeconds = 0;
                    _next.Output.Volume = 0f;
                    _next.Output.Play();
                }
            }
        }

        private void AdvanceCrossfade(Deck current)
        {
            if (_next?.Output == null)
            {
                _isCrossfading = false;
                return;
            }

            _crossfadeElapsedSeconds += TimerIntervalSeconds;
            var t = CrossfadeSeconds > 0 ? Math.Clamp(_crossfadeElapsedSeconds / CrossfadeSeconds, 0.0, 1.0) : 1.0;

            // Equal-power crossfade curve (constant perceived loudness through the overlap,
            // unlike a linear fade which dips in the middle).
            var currentGain = (float)(_masterVolumeFraction * Math.Cos(t * Math.PI / 2));
            var nextGain = (float)(_masterVolumeFraction * Math.Sin(t * Math.PI / 2));

            if (current.Output != null) current.Output.Volume = currentGain;
            _next.Output.Volume = nextGain;

            if (t >= 1.0)
            {
                _isCrossfading = false;
                _crossfadeElapsedSeconds = 0;
                if (_current != null) PromoteNextDeck(_current);
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
                if (_current?.Output != null && !_isCrossfading)
                {
                    _current.Output.Volume = _masterVolumeFraction;
                }
            }
        }

        public bool IsVisualizerActive { get; set; }

        public void Play(string filePath) => OpenDevice(filePath, autoPlay: true);

        /// <summary>
        /// Opens the file and initializes the output device without starting playback.
        /// Callers that just need a track "hot and ready" (e.g. Cue Forge loading a track
        /// before the user presses play) should use this instead of Play() — a
        /// Play()-then-immediately-Pause() sequence still lets a moment of real audio through
        /// the WASAPI buffer before the pause takes effect, which is audible as a brief blip.
        /// </summary>
        public void LoadWithoutPlaying(string filePath) => OpenDevice(filePath, autoPlay: false);

        /// <summary>
        /// Opens and initializes the output device for the track that will play next, without
        /// making any sound, so the transition when the current track ends can be a near-instant
        /// swap (gapless) or a timed overlap (crossfade) instead of a cold file-open that causes
        /// an audible gap. Call this as soon as the next track is known (e.g. right after the
        /// current one starts), well before playback is expected to reach it.
        /// </summary>
        public void PreloadNext(string filePath)
        {
            if (_current == null) return;
            if (_nextFilePath == filePath && _next != null) return; // already preloaded

            CancelPreload();

            try
            {
                var deck = CreateDeck(filePath);
                deck.Output!.Volume = 0f;
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

        /// <summary>Discards any preloaded next track (e.g. the queue changed before it was needed).</summary>
        public void CancelPreload()
        {
            _next?.Dispose();
            _next = null;
            _nextFilePath = null;
            _isCrossfading = false;
            _crossfadeElapsedSeconds = 0;
        }

        private void OpenDevice(string filePath, bool autoPlay)
        {
            Stop();

            try
            {
                _current = CreateDeck(filePath);
                _current.Output!.Volume = _masterVolumeFraction;
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

        private Deck CreateDeck(string filePath)
        {
            var deck = new Deck
            {
                AudioFile = new AudioFileReader(filePath)
            };

            // Set up channel and resampler for pitch (turntable style)
            var sampleChannel = new SampleChannel(deck.AudioFile, true);

            // 0. Vari-speed for pitch control: reads the channel at a variable rate via linear
            // interpolation, so speeding up/slowing down raises/lowers pitch just like a real
            // turntable/CDJ pitch fader (as opposed to tempo-only time-stretching).
            deck.VariSpeed = new VariSpeedSampleProvider(sampleChannel) { Speed = _pitch };

            // 1. Intercept for FFT (Spectrum). Only forwarded upstream while this deck is the
            // active one, so a preloaded/promoted deck seamlessly takes over the visualizer.
            var fftProvider = new FftSampleProvider(deck.VariSpeed, 2048, magnitudes =>
            {
                if (ReferenceEquals(_current, deck)) SpectrumChanged?.Invoke(this, magnitudes);
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

            deck.Output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
            deck.Output.Init(deck.Metering);
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
            if (promoted?.Output == null) return;

            _current = promoted;
            promoted.Output.Volume = _masterVolumeFraction;
            if (promoted.Output.PlaybackState != PlaybackState.Playing)
            {
                promoted.Output.Play();
            }

            LengthChanged?.Invoke(this, (long)(promoted.AudioFile?.TotalTime.TotalMilliseconds ?? 0));
            finishedDeck.Dispose();
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

            public WaveFormat WaveFormat => _source.WaveFormat;

            public FftSampleProvider(ISampleProvider source, int fftSize, Action<float[]> onFftCalculated)
            {
                _source = source;
                _fftSize = fftSize;
                _onFftCalculated = onFftCalculated;
                _buffer = new float[fftSize];
                _processingBuffer = new float[fftSize];
                _complexBuffer = new System.Numerics.Complex[fftSize];
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);

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
