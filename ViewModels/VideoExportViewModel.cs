using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Services.Video;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel for the Video Export dialog.
/// Lets users configure resolution, framerate, visual preset, output path,
/// and triggers the export via <see cref="VideoRenderer"/>.  Issue 5.3 / #37.
/// </summary>
public sealed class VideoExportViewModel : ReactiveObject, IDisposable
{
    private readonly VideoRenderer          _renderer;
    private readonly ILogger<VideoExportViewModel> _logger;
    private CancellationTokenSource?        _cts;

    // ── Configurable properties ──────────────────────────────────────────

    private int    _width          = 1920;
    private int    _height         = 1080;
    private int    _frameRate      = 30;
    private string _videoCodec     = "libx264";
    private string _outputPath     = string.Empty;
    private string _audioPath      = string.Empty;
    private VisualPreset _preset   = VisualPreset.Bars;

    // ── Progress / status ────────────────────────────────────────────────

    private bool   _isExporting;
    private double _progress;            // 0.0–100.0
    private string _statusMessage       = string.Empty;
    private string _errorMessage        = string.Empty;

    public VideoExportViewModel(
        VideoRenderer                 renderer,
        ILogger<VideoExportViewModel> logger)
    {
        _renderer = renderer;
        _logger   = logger;

        var canExport = this.WhenAnyValue(x => x.IsExporting, exporting => !exporting);
        var canCancel = this.WhenAnyValue(x => x.IsExporting);

        ExportCommand = ReactiveCommand.CreateFromTask(
            async () => await StartExportAsync(Frames ?? Array.Empty<VisualFrame>()),
            canExport);

        CancelCommand = ReactiveCommand.Create(CancelExport, canCancel);
    }

    // ── Commands exposed to XAML ─────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>
    /// Frames to render.  Set by the caller before the dialog is shown.
    /// </summary>
    public IReadOnlyList<VisualFrame>? Frames { get; set; }

    // ── Bindable properties ──────────────────────────────────────────────

    public int Width
    {
        get => _width;
        set => this.RaiseAndSetIfChanged(ref _width, value);
    }

    public int Height
    {
        get => _height;
        set => this.RaiseAndSetIfChanged(ref _height, value);
    }

    public int FrameRate
    {
        get => _frameRate;
        set => this.RaiseAndSetIfChanged(ref _frameRate, value);
    }

    public string VideoCodec
    {
        get => _videoCodec;
        set => this.RaiseAndSetIfChanged(ref _videoCodec, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => this.RaiseAndSetIfChanged(ref _outputPath, value);
    }

    public string AudioPath
    {
        get => _audioPath;
        set => this.RaiseAndSetIfChanged(ref _audioPath, value);
    }

    public VisualPreset Preset
    {
        get => _preset;
        set => this.RaiseAndSetIfChanged(ref _preset, value);
    }

    public bool IsExporting
    {
        get => _isExporting;
        private set => this.RaiseAndSetIfChanged(ref _isExporting, value);
    }

    public double Progress
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    // ── Available options (UI combo sources) ─────────────────────────────

    public static IReadOnlyList<(int W, int H, string Label)> Resolutions { get; } =
        new[]
        {
            (1920, 1080, "1080p (1920×1080)"),
            (1280,  720, "720p (1280×720)"),
            (3840, 2160, "4K (3840×2160)"),
        };

    public static IReadOnlyList<int> FrameRates { get; } = new[] { 24, 25, 30, 60 };

    public static IReadOnlyList<string> VideoCodecs { get; } =
        new[] { "libx264", "libx265", "libvpx-vp9" };

    public static IReadOnlyList<VisualPreset> VisualPresets { get; } =
        (VisualPreset[])Enum.GetValues(typeof(VisualPreset));

    // ── Validation ───────────────────────────────────────────────────────

    public string? Validate()
    {
        if (Width  <= 0) return "Width must be a positive integer.";
        if (Height <= 0) return "Height must be a positive integer.";
        if (FrameRate <= 0) return "Frame rate must be a positive integer.";
        if (string.IsNullOrWhiteSpace(OutputPath))
            return "Please choose an output file path.";
        if (string.IsNullOrWhiteSpace(VideoCodec))
            return "Please choose a video codec.";

        string? dir = Path.GetDirectoryName(OutputPath);
        // Allow empty dir (current directory) but not non-existent explicit dirs
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            // We'll create it during export; no error.
        }

        return null; // valid
    }

    // ── Commands ─────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the export.  Supply <paramref name="frames"/>; they are produced
    /// externally (e.g., from the timeline engine per-frame sample).
    /// </summary>
    public async Task StartExportAsync(IReadOnlyList<VisualFrame> frames)
    {
        string? error = Validate();
        if (error is not null)
        {
            ErrorMessage = error;
            return;
        }

        ErrorMessage   = string.Empty;
        IsExporting    = true;
        Progress       = 0;
        StatusMessage  = "Rendering frames…";

        _cts = new CancellationTokenSource();
        _renderer.ProgressChanged += OnProgress;

        try
        {
            var opts = new VideoRenderOptions
            {
                Width      = Width,
                Height     = Height,
                FrameRate  = FrameRate,
                VideoCodec = VideoCodec,
                OutputPath = OutputPath,
                AudioPath  = AudioPath,
                Preset     = Preset,
            };

            await _renderer.RenderAsync(frames, opts, _cts.Token);
            StatusMessage = $"Export complete: {Path.GetFileName(OutputPath)}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Export cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video export failed");
            ErrorMessage  = $"Export failed: {ex.Message}";
            StatusMessage = string.Empty;
        }
        finally
        {
            _renderer.ProgressChanged -= OnProgress;
            IsExporting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Cancels an in-progress export.</summary>
    public void CancelExport()
    {
        _cts?.Cancel();
    }

    // ── Internals ────────────────────────────────────────────────────────

    private void OnProgress(object? sender, RenderProgressEventArgs e)
    {
        Progress      = e.Percentage;
        StatusMessage = $"Rendering frame {e.FrameIndex} / {e.TotalFrames}…";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        ExportCommand.Dispose();
        CancelCommand.Dispose();
    }
}
