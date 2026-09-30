using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SLSKDONET.Services.Audio;

public interface ILibraryPreviewPlayer : IDisposable
{
    bool IsPreviewPlaying { get; }
    string? CurrentPreviewPath { get; }

    /// <summary>Playback position of the current preview (seconds), or null when nothing plays.
    /// Runs slightly ahead of what is heard by the output buffer; callers snap it to the grid.</summary>
    double? PositionSeconds => null;

    // Raw PCM magnitudes from FFT — subscribe to drive a spectrum visualizer.
    event EventHandler<float[]>? SpectrumChanged;

    /// <summary>Raised when a preview stops, whether by user request, a new preview replacing
    /// it, or natural end-of-file — lets callers with a play/stop toggle reset their UI state.</summary>
    event EventHandler? PreviewStopped;

    // Hover over a library row — debounced 250ms, then starts playback. startSeconds > 0 is a
    // deliberate seek (e.g. clicking the Mix Transition Editor's waveform) — see the
    // implementation's doc comment for how that differs from the plain hover-preview default.
    void RequestPreview(string filePath, double? bpm = null, double startSeconds = 0);

    // Mouse left the library surface or a Stop button was pressed.
    void StopPreview();
}

/// <summary>
/// Lightweight, self-contained preview player for library row hover/click.
/// Uses its own independent WasapiOut instance so it never interferes with
/// the main Workstation AudioPlayerService.
///
/// Design rules:
///  - Only one preview plays at a time; switching tracks fades the current one out.
///  - 250 ms hover debounce prevents rapid-fire starts as the mouse moves through rows.
///  - Exposes SpectrumChanged (FFT magnitudes) so a future SpectrumVisualizer can subscribe.
/// </summary>
public sealed class LibraryPreviewPlayer : ILibraryPreviewPlayer
{
    public double? PositionSeconds
    {
        get { try { return IsPreviewPlaying ? _reader?.CurrentTime.TotalSeconds : null; } catch (ObjectDisposedException) { return null; } }
    }

    private const int FftSize = 1024;
    private const int FadeOutMs = 120;
    private const int HoverDebounceMs = 250;

    private readonly ILogger<LibraryPreviewPlayer> _logger;

    private IWavePlayer? _output;
    private AudioFileReader? _reader;
    private VolumeSampleProvider? _volumeProvider;
    private CancellationTokenSource? _debounceCts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsPreviewPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public string? CurrentPreviewPath { get; private set; }

    public event EventHandler<float[]>? SpectrumChanged;
    public event EventHandler? PreviewStopped;

    private readonly SLSKDONET.Configuration.AppConfig? _config;

    public LibraryPreviewPlayer(ILogger<LibraryPreviewPlayer> logger, SLSKDONET.Configuration.AppConfig? config = null)
    {
        _logger = logger;
        _config = config;
    }

    /// <param name="startSeconds">0 = hover-preview default (plays from the top, debounced so it
    /// doesn't fire on every transient mouse-over). A positive value is a deliberate seek — e.g.
    /// clicking a spot on the Mix Transition Editor's waveform to audition that part of the
    /// track — and skips the debounce for immediate feedback, and always repositions even if
    /// this exact file is already playing (the hover-preview "already playing, do nothing"
    /// short-circuit below only applies to startSeconds == 0).</param>
    public void RequestPreview(string filePath, double? bpm = null, double startSeconds = 0)
    {
        // Cancel any pending debounce for the previous hover target
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        // Fire-and-forget; exceptions are caught inside
        _ = Task.Run(async () =>
        {
            try
            {
                if (startSeconds <= 0) await Task.Delay(HoverDebounceMs, token);
                await StartPreviewAsync(filePath, token, startSeconds);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LibraryPreview] Failed to start preview for {Path}", filePath);
            }
        }, token);
    }

    public void StopPreview()
    {
        _debounceCts?.Cancel();
        _ = Task.Run(async () => await FadeOutAndDisposeAsync());
    }

    private async Task StartPreviewAsync(string filePath, CancellationToken ct, double startSeconds = 0)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogDebug("[LibraryPreview] File not found, skipping preview: {Path}", filePath);
            return;
        }

        // If we are already previewing this exact file, do nothing — except for a deliberate
        // seek request (startSeconds > 0), which must always reposition even mid-playback.
        if (startSeconds <= 0 && string.Equals(CurrentPreviewPath, filePath, StringComparison.OrdinalIgnoreCase) && IsPreviewPlaying)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Fade out whatever is currently playing before starting the new track
            await FadeOutAndDisposeAsync();

            ct.ThrowIfCancellationRequested();

            _reader = PlayableAudio.Open(filePath, out var playablePath);
            if (startSeconds > 0)
            {
                var safeStart = Math.Clamp(startSeconds, 0, Math.Max(0, _reader.TotalTime.TotalSeconds - 0.25));
                // Not CurrentTime: on FLAC/M4A (Media Foundation) a seek before the first read is
                // dropped and playback started at 0:00 — see ExactSeek. Runs on this background task.
                ExactSeek.Seek(_reader, playablePath, safeStart);
                ct.ThrowIfCancellationRequested();
            }
            _volumeProvider = new VolumeSampleProvider(_reader) { Volume = 1f };

            var fftProvider = new PreviewFftSampleProvider(_volumeProvider, FftSize, magnitudes =>
                SpectrumChanged?.Invoke(this, magnitudes));

            _output = OpenOutput(fftProvider);
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Play();

            CurrentPreviewPath = filePath;
            _logger.LogDebug("[LibraryPreview] ▶ Preview started: {Path}", filePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The Settings output device (shared mode); the Windows default if that can't be opened.</summary>
    private IWavePlayer OpenOutput(ISampleProvider source)
    {
        IWavePlayer? output = null;
        try
        {
            output = AudioOutputProvider.CreatePreviewDevice(_config?.AudioOutputMode, _config?.AudioOutputDeviceName);
            output.Init(source);
            return output;
        }
        catch (Exception ex)
        {
            output?.Dispose();
            _logger.LogWarning(ex, "[LibraryPreview] Could not open output device {Device}; using the Windows default", _config?.AudioOutputDeviceName ?? "(default)");
            var fallback = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
            fallback.Init(source);
            return fallback;
        }
    }

    private async Task FadeOutAndDisposeAsync()
    {
        if (_volumeProvider == null || _output == null)
        {
            DisposePlaybackResources();
            return;
        }

        // Quick linear fade so there is no click/pop on sudden stop
        const int steps = 6;
        float stepSize = 1f / steps;
        int stepDelayMs = FadeOutMs / steps;

        for (int i = steps - 1; i >= 0; i--)
        {
            if (_volumeProvider != null)
                _volumeProvider.Volume = stepSize * i;
            await Task.Delay(stepDelayMs);
        }

        DisposePlaybackResources();
    }

    private void DisposePlaybackResources()
    {
        try
        {
            _output?.Stop();
            _output?.Dispose();
        }
        catch { }

        try { _reader?.Dispose(); } catch { }

        _output = null;
        _reader = null;
        _volumeProvider = null;
        CurrentPreviewPath = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            _logger.LogWarning(e.Exception, "[LibraryPreview] Playback stopped with error");
        DisposePlaybackResources();
        PreviewStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        DisposePlaybackResources();
        _gate.Dispose();
    }

    // ── Nested FFT provider ──────────────────────────────────────────────────

    private sealed class PreviewFftSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _fftSize;
        private readonly Action<float[]> _onFftReady;
        private readonly float[] _accumulator;
        private readonly System.Numerics.Complex[] _complexBuffer;
        private int _pos;
        private int _busy;
        private readonly int _channels;
        private float _frameSum;
        private int _frameChannel;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public PreviewFftSampleProvider(ISampleProvider source, int fftSize, Action<float[]> onFftReady)
        {
            _source = source;
            _fftSize = fftSize;
            _onFftReady = onFftReady;
            _accumulator = new float[fftSize];
            _complexBuffer = new System.Numerics.Complex[fftSize];
            _channels = Math.Max(1, source.WaveFormat.Channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);

            for (int i = 0; i < read; i++)
            {
                // Mono downmix: FFT'ing the interleaved stereo stream scrambles the frequency axis.
                _frameSum += buffer[offset + i];
                if (++_frameChannel < _channels) continue;
                _accumulator[_pos++] = _frameSum / _channels;
                _frameSum = 0;
                _frameChannel = 0;
                if (_pos < _fftSize) continue;

                _pos = 0;
                if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) continue;

                float[] snap = new float[_fftSize];
                Array.Copy(_accumulator, snap, _fftSize);

                _ = Task.Run(() =>
                {
                    try
                    {
                        for (int j = 0; j < _fftSize; j++)
                        {
                            double w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * j / (_fftSize - 1)));
                            _complexBuffer[j] = new System.Numerics.Complex(snap[j] * w, 0);
                        }

                        MathNet.Numerics.IntegralTransforms.Fourier.Forward(
                            _complexBuffer,
                            MathNet.Numerics.IntegralTransforms.FourierOptions.NoScaling);

                        var mag = new float[_fftSize / 2];
                        for (int j = 0; j < mag.Length; j++)
                            mag[j] = (float)_complexBuffer[j].Magnitude;

                        _onFftReady(mag);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busy, 0);
                    }
                });
            }

            return read;
        }
    }
}
