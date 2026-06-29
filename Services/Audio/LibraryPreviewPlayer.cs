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

    // Raw PCM magnitudes from FFT — subscribe to drive a spectrum visualizer.
    event EventHandler<float[]>? SpectrumChanged;

    // Hover over a library row — debounced 250ms, then starts playback.
    void RequestPreview(string filePath, double? bpm = null);

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

    public LibraryPreviewPlayer(ILogger<LibraryPreviewPlayer> logger)
    {
        _logger = logger;
    }

    public void RequestPreview(string filePath, double? bpm = null)
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
                await Task.Delay(HoverDebounceMs, token);
                await StartPreviewAsync(filePath, token);
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

    private async Task StartPreviewAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogDebug("[LibraryPreview] File not found, skipping preview: {Path}", filePath);
            return;
        }

        // If we are already previewing this exact file, do nothing
        if (string.Equals(CurrentPreviewPath, filePath, StringComparison.OrdinalIgnoreCase) && IsPreviewPlaying)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Fade out whatever is currently playing before starting the new track
            await FadeOutAndDisposeAsync();

            ct.ThrowIfCancellationRequested();

            _reader = new AudioFileReader(filePath);
            _volumeProvider = new VolumeSampleProvider(_reader) { Volume = 1f };

            var fftProvider = new PreviewFftSampleProvider(_volumeProvider, FftSize, magnitudes =>
                SpectrumChanged?.Invoke(this, magnitudes));

            _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, latencyMilliseconds: 100);
            _output.Init(fftProvider);
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

        public WaveFormat WaveFormat => _source.WaveFormat;

        public PreviewFftSampleProvider(ISampleProvider source, int fftSize, Action<float[]> onFftReady)
        {
            _source = source;
            _fftSize = fftSize;
            _onFftReady = onFftReady;
            _accumulator = new float[fftSize];
            _complexBuffer = new System.Numerics.Complex[fftSize];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);

            for (int i = 0; i < read; i++)
            {
                _accumulator[_pos++] = buffer[offset + i];
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
