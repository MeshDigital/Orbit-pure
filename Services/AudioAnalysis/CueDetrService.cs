using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SLSKDONET.Engine.Analysis.CueDetr;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Runs CUE-DETR (ETH-DISCO — MIT licence, weights from Hugging Face disco-eth/cue-detr, exported
/// to ONNX by Tools/cue-detr/export_onnx.py) in-process. The model was trained on cue points DJs
/// placed in electronic music and predicts where a DJ would put cues from the spectrogram alone,
/// with no beat grid.
///
/// When the model file is missing, <see cref="IsAvailable"/> is false and detection returns null.
/// </summary>
public sealed class CueDetrService : IDisposable
{
    public static readonly string DefaultModelRelativePath = Path.Combine("Tools", "Essentia", "models", "cue-detr.onnx");
    private const int BatchWindows = 8;

    private readonly ILogger<CueDetrService> _logger;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1); // one Run at a time (DirectML is not re-entrant safe)
    private readonly object _loadGate = new();
    private InferenceSession? _session;
    private bool _loadAttempted;
    private string? _modelTag;

    public CueDetrService(ILogger<CueDetrService> logger, string? modelPath = null)
    {
        _logger = logger;
        _modelPath = modelPath ?? Path.Combine(AppContext.BaseDirectory, DefaultModelRelativePath);
    }

    public bool IsAvailable { get { EnsureLoaded(); return _session != null; } }

    /// <summary>"cue-detr|{sha256 prefix}" — stored with the results so a new model re-runs them.</summary>
    public string? ModelTag { get { EnsureLoaded(); return _modelTag; } }

    /// <summary>Execution provider in use ("DirectML" or "CPU"), for logs/diagnostics.</summary>
    public string Provider { get; private set; } = "none";

    /// <summary>
    /// Cue points for mono audio at any sample rate ≥ 22050 Hz, or null when the model is
    /// unavailable, the audio is shorter than one window (~8 s), or inference failed.
    /// </summary>
    public Task<List<CueDetrPoint>?> DetectAsync(float[] mono, int sampleRate, CancellationToken ct = default)
    {
        EnsureLoaded();
        if (_session == null) return Task.FromResult<List<CueDetrPoint>?>(null);
        return Task.Run(() => Detect(mono, sampleRate, ct), ct);
    }

    /// <summary>Decodes <paramref name="path"/> with ffmpeg (44.1 kHz mono, as the analysis
    /// pipeline does) and detects. For tools and backfills; analysis passes its own PCM.</summary>
    public async Task<List<CueDetrPoint>?> DetectFileAsync(string path, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        var mono = await DecodeMono44kAsync(path, ct).ConfigureAwait(false);
        return mono == null ? null : await DetectAsync(mono, 44100, ct).ConfigureAwait(false);
    }

    private List<CueDetrPoint>? Detect(float[] mono, int sampleRate, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var y = CueDetrFrontEnd.ResampleTo22050(mono, sampleRate);
            long tResample = sw.ElapsedMilliseconds;
            var mel = CueDetrFrontEnd.MelDb(y, out int frames);
            long tMel = sw.ElapsedMilliseconds;
            long tInfer = 0;
            if (frames < CueDetrFrontEnd.WindowWidth) return null;
            var image = CueDetrFrontEnd.ToImage(mel, frames);
            var borders = CueDetrFrontEnd.WindowBorders(frames);
            ct.ThrowIfCancellationRequested();

            int plane = 3 * CueDetrFrontEnd.Mels * CueDetrFrontEnd.WindowWidth;
            float[]? logits = null, boxes = null;
            int queries = 0, classes = 0;
            for (int start = 0; start < borders.Length; start += BatchWindows)
            {
                int count = Math.Min(BatchWindows, borders.Length - start);
                var input = new DenseTensor<float>(new[] { count, 3, CueDetrFrontEnd.Mels, CueDetrFrontEnd.WindowWidth });
                for (int b = 0; b < count; b++)
                    CueDetrFrontEnd.Normalise(CueDetrFrontEnd.Window(image, frames, borders[start + b]),
                        input.Buffer.Span.Slice(b * plane, plane));

                _inferenceLock.Wait(ct);
                long before = sw.ElapsedMilliseconds;
                try
                {
                    using var results = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor("pixel_values", input) });
                    var l = results.First(r => r.Name == "logits").AsTensor<float>();
                    var bx = results.First(r => r.Name == "pred_boxes").AsTensor<float>();
                    if (logits == null)
                    {
                        // Shapes come from the output: the export leaves the query axis symbolic.
                        queries = l.Dimensions[1];
                        classes = l.Dimensions[2];
                        logits = new float[borders.Length * queries * classes];
                        boxes = new float[borders.Length * queries * 4];
                    }
                    CopyOutput(l, logits, start * queries * classes);
                    CopyOutput(bx, boxes!, start * queries * 4);
                }
                finally { _inferenceLock.Release(); tInfer += sw.ElapsedMilliseconds - before; }
                ct.ThrowIfCancellationRequested();
            }

            var cues = CueDetrPostProcessor.Process(logits!, boxes!, borders, queries, classes);
            _logger.LogDebug("[CueDetr] {Count} cues from {Windows} windows in {Ms} ms ({Provider}; resample {R} ms, mel {M} ms, inference {I} ms)",
                cues.Count, borders.Length, sw.ElapsedMilliseconds, Provider, tResample, tMel - tResample, tInfer);
            return cues;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CueDetr] Detection failed — skipping AI cue points for this track");
            return null;
        }
    }

    private static void CopyOutput(Tensor<float> tensor, float[] target, int offset)
    {
        if (tensor is DenseTensor<float> dense) dense.Buffer.Span.CopyTo(target.AsSpan(offset));
        else tensor.ToArray().CopyTo(target, offset);
    }

    private void EnsureLoaded()
    {
        lock (_loadGate)
        {
            if (_loadAttempted) return;
            _loadAttempted = true;
            if (!File.Exists(_modelPath))
            {
                _logger.LogWarning("[CueDetr] Model not found at {Path}; AI cue points are disabled.", _modelPath);
                return;
            }
            try
            {
                try
                {
                    var forced = Environment.GetEnvironmentVariable("ORBIT_CUEDETR_PROVIDER");
                    if (forced is "cpu") throw new NotSupportedException("CPU forced");
                    var dml = new SessionOptions();
                    dml.AppendExecutionProvider_DML(forced is "dml1" ? 1 : 0);
                    _session = new InferenceSession(_modelPath, dml);
                    Provider = "DirectML";
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("[CueDetr] DirectML unavailable ({Error}); using CPU.", ex.Message);
                    _session = new InferenceSession(_modelPath, new SessionOptions());
                    Provider = "CPU";
                }
                _modelTag = BuildModelTag(_modelPath);
                _logger.LogInformation("[CueDetr] Loaded {Tag} on {Provider}.", _modelTag, Provider);
            }
            catch (Exception ex)
            {
                _session = null;
                _logger.LogError(ex, "[CueDetr] Failed to load {Path}; AI cue points are disabled.", _modelPath);
            }
        }
    }

    private static string BuildModelTag(string path)
    {
        using var fs = File.OpenRead(path);
        return "cue-detr|" + Convert.ToHexString(SHA256.HashData(fs))[..8];
    }

    public static async Task<float[]?> DecodeMono44kAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(AudioIngestionPipeline.ResolveFfmpegPath(),
            $"-v error -i \"{path}\" -vn -map 0:a:0 -ac 1 -ar 44100 -f f32le -")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process == null) return null;
        _ = process.StandardError.ReadToEndAsync(ct);
        using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0 || buffer.Length < 4) return null;
        var samples = new float[buffer.Length / 4];
        Buffer.BlockCopy(buffer.GetBuffer(), 0, samples, 0, samples.Length * 4);
        return samples;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _inferenceLock.Dispose();
    }
}
