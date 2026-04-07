using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Builds <see cref="WaveformAnalysisData"/> for a single stem WAV file and
/// exposes it for binding to <see cref="SLSKDONET.Views.Avalonia.Controls.WaveformControl"/>.
///
/// One instance per stem (vocals / drums / bass / other).
/// All four instances share a common <see cref="ViewOffset"/> and <see cref="ZoomLevel"/>
/// so their horizontal positions stay synchronised with the master waveform.
/// </summary>
public sealed class StemWaveformRowViewModel : ReactiveObject
{
    public string StemName   { get; }
    public string AccentHex  { get; }

    // ── Waveform data ─────────────────────────────────────────────────────

    private WaveformAnalysisData? _waveformData;
    public WaveformAnalysisData?  WaveformData
    {
        get => _waveformData;
        private set => this.RaiseAndSetIfChanged(ref _waveformData, value);
    }

    private bool _isLoading;
    public bool  IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    // ── Scroll sync ───────────────────────────────────────────────────────

    private double _viewOffset;
    /// <summary>Horizontal scroll position (0.0–1.0) — bind to the master waveform's ViewOffset.</summary>
    public double ViewOffset
    {
        get => _viewOffset;
        set => this.RaiseAndSetIfChanged(ref _viewOffset, value);
    }

    private double _zoomLevel = 1.0;
    /// <summary>Zoom factor (1.0 = full track, 8.0 = max zoom) — bind to the master zoom.</summary>
    public double ZoomLevel
    {
        get => _zoomLevel;
        set => this.RaiseAndSetIfChanged(ref _zoomLevel, Math.Clamp(value, 0.25, 32.0));
    }

    private float _progress;
    /// <summary>Playhead position (0.0–1.0).</summary>
    public float Progress
    {
        get => _progress;
        set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public StemWaveformRowViewModel(string stemName, string accentHex)
    {
        StemName  = stemName;
        AccentHex = accentHex;
    }

    /// <summary>
    /// Builds <see cref="WaveformData"/> from a stem WAV file at 100 points/second resolution.
    /// Runs on a thread-pool thread; updates UI properties on the Avalonia dispatcher.
    /// </summary>
    public async Task LoadWavAsync(string wavFilePath)
    {
        if (!File.Exists(wavFilePath)) return;

        IsLoading = true;
        try
        {
            var data = await Task.Run(() => BuildWaveformData(wavFilePath));
            await Dispatcher.UIThread.InvokeAsync(() => { WaveformData = data; IsLoading = false; });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }

    // ─── Waveform building ────────────────────────────────────────────────

    private static WaveformAnalysisData BuildWaveformData(string wavFilePath)
    {
        const int pointsPerSecond = 100; // 10 ms resolution

        using var reader  = new NAudio.Wave.AudioFileReader(wavFilePath);
        double    totalSec = reader.TotalTime.TotalSeconds;
        int       sampleRate = reader.WaveFormat.SampleRate;
        int       channels   = reader.WaveFormat.Channels;

        int samplesPerPoint   = (int)(sampleRate / pointsPerSecond);
        int totalPoints       = (int)(totalSec * pointsPerSecond) + 1;

        var peakData = new byte[totalPoints];
        var rmsData  = new byte[totalPoints];

        var buf      = new float[samplesPerPoint * channels];
        int pointIdx = 0;

        while (pointIdx < totalPoints)
        {
            int read = reader.Read(buf, 0, buf.Length);
            if (read == 0) break;

            int frames = read / channels;
            float peak = 0f;
            float sumSq = 0f;

            for (int i = 0; i < read; i++)
            {
                float abs = MathF.Abs(buf[i]);
                if (abs > peak) peak = abs;
                sumSq += abs * abs;
            }

            float rms = MathF.Sqrt(sumSq / read);
            peakData[pointIdx] = (byte)(peak * 255f);
            rmsData[pointIdx]  = (byte)(rms  * 255f);
            pointIdx++;
        }

        return new WaveformAnalysisData
        {
            PeakData        = peakData,
            RmsData         = rmsData,
            PointsPerSecond = pointsPerSecond,
            DurationSeconds = totalSec
        };
    }
}

// ─── StemWaveformViewModel ────────────────────────────────────────────────────

/// <summary>
/// Aggregates four <see cref="StemWaveformRowViewModel"/>s and synchronises their
/// scroll + zoom position. Bind to <see cref="SLSKDONET.Views.Avalonia.StemWaveformView"/>.
/// </summary>
public sealed class StemWaveformViewModel : ReactiveObject
{
    public StemWaveformRowViewModel VocalsWaveform { get; } = new("Vocals", "#00CFFF");
    public StemWaveformRowViewModel DrumsWaveform  { get; } = new("Drums",  "#FF8C00");
    public StemWaveformRowViewModel BassWaveform   { get; } = new("Bass",   "#44FF88");
    public StemWaveformRowViewModel OtherWaveform  { get; } = new("Other",  "#BB88FF");

    private double _sharedViewOffset;
    public double SharedViewOffset
    {
        get => _sharedViewOffset;
        set
        {
            this.RaiseAndSetIfChanged(ref _sharedViewOffset, value);
            VocalsWaveform.ViewOffset = value;
            DrumsWaveform .ViewOffset = value;
            BassWaveform  .ViewOffset = value;
            OtherWaveform .ViewOffset = value;
        }
    }

    private double _sharedZoomLevel = 1.0;
    public double SharedZoomLevel
    {
        get => _sharedZoomLevel;
        set
        {
            this.RaiseAndSetIfChanged(ref _sharedZoomLevel, value);
            VocalsWaveform.ZoomLevel = value;
            DrumsWaveform .ZoomLevel = value;
            BassWaveform  .ZoomLevel = value;
            OtherWaveform .ZoomLevel = value;
        }
    }

    private float _sharedProgress;
    public float SharedProgress
    {
        get => _sharedProgress;
        set
        {
            this.RaiseAndSetIfChanged(ref _sharedProgress, value);
            VocalsWaveform.Progress = value;
            DrumsWaveform .Progress = value;
            BassWaveform  .Progress = value;
            OtherWaveform .Progress = value;
        }
    }

    /// <summary>
    /// Called when <see cref="StemMixerViewModel"/> completes separation.
    /// Loads all four stem waveforms in parallel.
    /// </summary>
    public Task LoadStemsAsync(string? vocalsPath, string? drumsPath, string? bassPath, string? otherPath)
    {
        return Task.WhenAll(
            vocalsPath != null ? VocalsWaveform.LoadWavAsync(vocalsPath) : Task.CompletedTask,
            drumsPath  != null ? DrumsWaveform .LoadWavAsync(drumsPath)  : Task.CompletedTask,
            bassPath   != null ? BassWaveform  .LoadWavAsync(bassPath)   : Task.CompletedTask,
            otherPath  != null ? OtherWaveform .LoadWavAsync(otherPath)  : Task.CompletedTask
        );
    }
}
