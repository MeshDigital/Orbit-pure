using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Library;

namespace SLSKDONET.Services.Intake;

/// <summary>Everything Intake learns about one file before it is classified.</summary>
public sealed record IntakeInspection(
    AudioFileFacts? Facts,
    string? ReadError,
    string? ContentHash,
    int DecodeErrorCount,
    string? DecodeErrorPreview,
    SpectralIntegrityResult? Spectral);

/// <summary>Reads one incoming file: tags and stream facts, content hash, decode check, spectral check.</summary>
public interface IIntakeFileInspector
{
    Task<IntakeInspection> InspectAsync(string path, bool deepDecodeCheck, CancellationToken ct);
}

/// <summary>
/// The real inspector, built from the checks ORBIT already runs on downloads and imports:
/// <see cref="AudioTagReader"/>, <see cref="AudioContentHasher"/> (the same tag-independent hash the
/// library stores, so exact duplicates match), the FFmpeg corruption probe and the spectral
/// integrity check. Each check that fails is recorded on the result, never thrown, so one bad file
/// can't stop a scan.
/// </summary>
public sealed class IntakeFileInspector : IIntakeFileInspector
{
    /// <summary>Lossy files at or above this bitrate get the spectral check (an upscaled 128 kbps file labelled 320).</summary>
    private const int SpectralCheckMinLossyKbps = 256;

    private readonly AudioContentHasher _hasher;
    private readonly AudioIngestionPipeline _pipeline;
    private readonly IAudioIntegrityService _integrity;
    private readonly ILogger<IntakeFileInspector> _logger;

    public IntakeFileInspector(
        AudioContentHasher hasher,
        AudioIngestionPipeline pipeline,
        IAudioIntegrityService integrity,
        ILogger<IntakeFileInspector> logger)
    {
        _hasher = hasher;
        _pipeline = pipeline;
        _integrity = integrity;
        _logger = logger;
    }

    public async Task<IntakeInspection> InspectAsync(string path, bool deepDecodeCheck, CancellationToken ct)
    {
        AudioFileFacts facts;
        try
        {
            facts = AudioTagReader.Read(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Intake: can't read {Path}: {Message}", path, ex.Message);
            return new IntakeInspection(null, ex.Message, null, 0, null, null);
        }

        var hash = await _hasher.ComputeAsync(path, ct).ConfigureAwait(false);

        int decodeErrors = 0;
        string? decodePreview = null;
        if (deepDecodeCheck)
        {
            try
            {
                (decodeErrors, decodePreview) = await _pipeline.ProbeForCorruptionAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FFmpeg missing or failing is not the file's fault: record it, don't reject the file.
                _logger.LogWarning(ex, "Intake: decode check failed for {Path}", path);
                decodePreview = "Decode check unavailable: " + ex.Message;
            }
        }

        SpectralIntegrityResult? spectral = null;
        if (WantsSpectralCheck(Path.GetExtension(path), facts.BitrateKbps))
        {
            try
            {
                spectral = await _integrity.AnalyseAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Intake: spectral check failed for {Path}", path);
            }
        }

        return new IntakeInspection(facts, null, hash?.Value, decodeErrors, decodePreview, spectral);
    }

    /// <summary>Lossless files (fake-lossless check) and high-bitrate lossy files (upscale check).</summary>
    internal static bool WantsSpectralCheck(string? extension, int bitrateKbps)
    {
        var format = IntakeClassifier.Format(extension);
        if (AudioFormats.LosslessTokenSet.Contains(format)) return true;
        return bitrateKbps >= SpectralCheckMinLossyKbps;
    }
}
