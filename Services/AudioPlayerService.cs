using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Timers;

namespace SLSKDONET.Services
{
    public class AudioPlayerService : IAudioPlayerService, IDisposable
    {
        private IWavePlayer? _outputDevice;
        private AudioFileReader? _audioFile;
        // private WdlResamplingSampleProvider? _resampler;
        private SampleChannel? _sampleChannel;
        private MeteringSampleProvider? _meteringProvider;
        private bool _isInitialized;
        private System.Timers.Timer? _timer;

        public event EventHandler<long>? TimeChanged;
        public event EventHandler<float>? PositionChanged;
        public event EventHandler<long>? LengthChanged;
        public event EventHandler<AudioLevelsEventArgs>? AudioLevelsChanged;
        public event EventHandler<float[]>? SpectrumChanged;
        public event EventHandler? EndReached;
        public event EventHandler? PausableChanged;




        private DateTime _lastSpectrumUpdate = DateTime.MinValue;
        private int _vuMeterSkipCounter = 0;
        private const int VU_METER_SKIP_FRAMES = 5; // Only update VU meter every 5th buffer

        private double _pitch = 1.0;
        public double Pitch 
        { 
            get => _pitch; 
            set 
            {
                _pitch = value;
                // resampler pitch adjustment placeholder
            }
        }

        public AudioPlayerService()
        {
            _isInitialized = true;
            _timer = new System.Timers.Timer(50); // 20fps is sufficient for progress updates
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
        }


        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_audioFile != null && _outputDevice?.PlaybackState == PlaybackState.Playing)
            {
                TimeChanged?.Invoke(this, (long)_audioFile.CurrentTime.TotalMilliseconds);
                PositionChanged?.Invoke(this, (float)(_audioFile.Position / (double)_audioFile.Length));
            }
        }

        public bool IsInitialized => _isInitialized;
        public bool IsPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;
        public long Length => (long)(_audioFile?.TotalTime.TotalMilliseconds ?? 0);
        public double Duration => _audioFile?.TotalTime.TotalSeconds ?? 0;
        public long Time => (long)(_audioFile?.CurrentTime.TotalMilliseconds ?? 0);

        public float Position
        {
            get => (float)(_audioFile != null ? _audioFile.Position / (double)_audioFile.Length : 0);
            set
            {
                if (_audioFile != null)
                {
                    _audioFile.Position = (long)(value * _audioFile.Length);
                }
            }
        }

        public int Volume
        {
            get => (int)((_outputDevice?.Volume ?? 1f) * 100);
            set { if (_outputDevice != null) _outputDevice.Volume = value / 100f; }
        }

        public bool IsVisualizerActive { get; set; }

        public void Play(string filePath)
        {
            Stop();

            try
            {
                _audioFile = new AudioFileReader(filePath);
                
                // Set up channel and resampler for pitch (turntable style)
                _sampleChannel = new SampleChannel(_audioFile, true);
                
                // 1. Intercept for FFT (Spectrum) - Using larger buffer to reduce frequency
                var fftProvider = new FftSampleProvider(_sampleChannel, 2048, magnitudes => 
                {
                     SpectrumChanged?.Invoke(this, magnitudes);
                });

                // 2. Wrap in Metering for VU
                _meteringProvider = new MeteringSampleProvider(fftProvider);
                _meteringProvider.StreamVolume += OnStreamVolume;
                
                _outputDevice = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
                _outputDevice.Init(_meteringProvider);
                _outputDevice.PlaybackStopped += (s, e) => EndReached?.Invoke(this, EventArgs.Empty);
                
                _outputDevice.Play();
                LengthChanged?.Invoke(this, (long)_audioFile.TotalTime.TotalMilliseconds);
                PausableChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioPlayerService] Playback error: {ex.Message}");
                throw;
            }
        }

        private void OnStreamVolume(object? sender, StreamVolumeEventArgs e)
        {
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

        public void Pause()
        {
            if (_outputDevice?.PlaybackState == PlaybackState.Playing)
                _outputDevice.Pause();
            else if (_outputDevice?.PlaybackState == PlaybackState.Paused)
                _outputDevice.Play();
                
            PausableChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            _outputDevice?.Stop();
            _audioFile?.Dispose();
            _outputDevice?.Dispose();
            _audioFile = null;
            _outputDevice = null;
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
