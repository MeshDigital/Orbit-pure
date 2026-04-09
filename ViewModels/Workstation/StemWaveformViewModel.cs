using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using NAudio.Wave;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Aggregates the four per-stem waveform rows and the shared zoom/offset controls
/// shown in <c>StemWaveformView.axaml</c>.  After stem separation completes,
/// call <see cref="LoadStemWaveformsAsync"/> to render each stem waveform.
/// </summary>
public sealed class StemWaveformViewModel : ReactiveObject
{
    // ── Per-stem rows ─────────────────────────────────────────────────────────

    public StemWaveformRowViewModel VocalsWaveform { get; } = new();
    public StemWaveformRowViewModel DrumsWaveform  { get; } = new();
    public StemWaveformRowViewModel BassWaveform   { get; } = new();
    public StemWaveformRowViewModel OtherWaveform  { get; } = new();

    // ── Shared controls ───────────────────────────────────────────────────────

    private double _sharedZoomLevel = 1.0;
    public double SharedZoomLevel
    {
        get => _sharedZoomLevel;
        set => this.RaiseAndSetIfChanged(ref _sharedZoomLevel, value);
    }

    private double _sharedViewOffset;
    public double SharedViewOffset
    {
        get => _sharedViewOffset;
        set => this.RaiseAndSetIfChanged(ref _sharedViewOffset, value);
    }

    private float _sharedProgress;
    public float SharedProgress
    {
        get => _sharedProgress;
        set
        {
            this.RaiseAndSetIfChanged(ref _sharedProgress, value);
            VocalsWaveform.Progress = value;
            DrumsWaveform.Progress  = value;
            BassWaveform.Progress   = value;
            OtherWaveform.Progress  = value;
        }
    }

    // ── API ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asyncronously renders waveform analysis data for each stem file path
    /// supplied by <see cref="StemMixerViewModel"/> after separation completes.
    /// </summary>
    public async Task LoadStemWaveformsAsync(
        string? vocalsPath, string? drumsPath,
        string? bassPath,   string? otherPath)
    {
        await Task.WhenAll(
            LoadRowAsync(VocalsWaveform, vocalsPath),
            LoadRowAsync(DrumsWaveform,  drumsPath),
            LoadRowAsync(BassWaveform,   bassPath),
            LoadRowAsync(OtherWaveform,  otherPath));
    }

    /// <summary>Clears all rows (call when a new track is loaded on the deck).</summary>
    public void Clear()
    {
        VocalsWaveform.Clear();
        DrumsWaveform.Clear();
        BassWaveform.Clear();
        OtherWaveform.Clear();
        SharedProgress  = 0f;
        SharedViewOffset = 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task LoadRowAsync(StemWaveformRowViewModel row, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        await Dispatcher.UIThread.InvokeAsync(() => row.IsLoading = true);
        try
        {
            var data = await Task.Run(() => ExtractWaveformData(filePath));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                row.WaveformData = data;
                row.IsLoading    = false;
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => row.IsLoading = false);
        }
    }

    /// <summary>
    /// Reads a WAV file and returns a compact RMS profile as <see cref="WaveformAnalysisData"/>.
    /// Uses a single-pass NAudio read; only occupies ~1 KB per stem in memory.
    /// </summary>
    private static WaveformAnalysisData ExtractWaveformData(string filePath)
    {
        const int targetBins = WaveformExtractionService.TargetSamples;

        using var reader = new AudioFileReader(filePath);
        double duration = reader.TotalTime.TotalSeconds;
        long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);

        var sums = new double[targetBins];
        var counts = new int[targetBins];
        var buffer = new float[4096];

        long samplesRead = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                int bin = totalSamples > 0
                    ? (int)((samplesRead + i) * targetBins / totalSamples)
                    : 0;
                bin = Math.Min(bin, targetBins - 1);
                sums[bin]   += Math.Abs(buffer[i]);
                counts[bin] += 1;
            }
            samplesRead += read;
        }

        var rmsBytes = new byte[targetBins];
        double max = 0;
        for (int b = 0; b < targetBins; b++)
            if (counts[b] > 0) max = Math.Max(max, sums[b] / counts[b]);

        if (max > 0)
            for (int b = 0; b < targetBins; b++)
                rmsBytes[b] = (byte)(255 * (counts[b] > 0 ? sums[b] / counts[b] / max : 0));

        return new WaveformAnalysisData
        {
            RmsData         = rmsBytes,
            PeakData        = rmsBytes,   // reuse RMS for stem waveforms (no tri-band)
            LowData         = System.Array.Empty<byte>(),
            MidData         = System.Array.Empty<byte>(),
            HighData        = System.Array.Empty<byte>(),
            DurationSeconds = duration,
        };
    }
}
