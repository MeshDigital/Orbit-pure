using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// On-demand vocal-activity detection (#285). The Essentia <c>voice_instrumental</c> head was never
/// viable (instrumental EDM scores as voice — see the issue's evaluation), so the vocal span, density
/// and class are instead derived from the RMS of the Demucs <em>vocal stem</em>, the accurate signal.
///
/// Cost is one stem separation per track, so this is only run when the DJ asks for it — "Check
/// clashes" in the Sets page — and <see cref="CachedStemSeparator"/> makes repeat runs instant.
/// A missing model, a missing file, or a track with no analysis row is logged and left untouched:
/// the vocal fields stay null, so every consumer keeps treating the data as unknown, which is honest.
/// </summary>
public interface IVocalActivityService
{
    /// <summary>True when the stem separator (Demucs) is available, so on-demand detection can run.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Ensures the vocal span/density are written for each given track hash that hasn't been measured,
    /// separating stems on demand (cache-through). Never throws; per-track failures are logged and skipped.
    /// </summary>
    Task EnsureVocalDataAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default);
}

public sealed class VocalActivityService : IVocalActivityService
{
    /// <summary>RMS window for the vocal curve, in seconds.</summary>
    public const double WindowSeconds = 0.5;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CachedStemSeparator _separator;
    private readonly ILogger<VocalActivityService> _logger;

    public VocalActivityService(
        IDbContextFactory<AppDbContext> dbFactory,
        CachedStemSeparator separator,
        ILogger<VocalActivityService> logger)
    {
        _dbFactory = dbFactory;
        _separator = separator;
        _logger = logger;
    }

    public bool IsAvailable => _separator.IsAvailable;

    public async Task EnsureVocalDataAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            _logger.LogInformation(
                "[VocalActivity] Stem separation isn't available (Demucs model missing or switched off); vocal detection skipped.");
            return;
        }

        var unique = hashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var hash in unique)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DetectAsync(hash, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VocalActivity] Couldn't detect vocals for {Hash}; vocal fields left unset.", hash);
            }
        }
    }

    private async Task DetectAsync(string hash, CancellationToken ct)
    {
        string? path;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var features = await db.AudioFeatures.AsNoTracking()
                .FirstOrDefaultAsync(f => f.TrackUniqueHash == hash, ct).ConfigureAwait(false);
            if (features is null)
            {
                _logger.LogInformation("[VocalActivity] No audio features row for {Hash}; skipping.", hash);
                return;
            }
            if (VocalActivity.IsMeasured(features))
                return; // already measured (even an instrumental has a curve)
            path = await ResolvePathAsync(db, hash, ct).ConfigureAwait(false);
        }

        if (path is null)
        {
            _logger.LogInformation("[VocalActivity] No local audio file for {Hash}; vocal fields left unset.", hash);
            return;
        }

        var outputDir = Path.Combine(Path.GetTempPath(), "orbit_vocal", Guid.NewGuid().ToString("N"));
        try
        {
            Dictionary<StemType, string> stems;
            try
            {
                stems = await _separator.SeparateAsync(path, outputDir, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "[VocalActivity] Stem separation failed for {Hash}; vocal fields left unset.", hash);
                return;
            }

            if (!stems.TryGetValue(StemType.Vocals, out var vocalPath) || !File.Exists(vocalPath))
            {
                _logger.LogInformation("[VocalActivity] Separation produced no vocal stem for {Hash}; vocal fields left unset.", hash);
                return;
            }

            var rms = await Task.Run(() => ReadStemRms(vocalPath, WindowSeconds, ct), ct).ConfigureAwait(false);
            if (rms.Length == 0)
            {
                _logger.LogInformation("[VocalActivity] The vocal stem for {Hash} decoded empty; vocal fields left unset.", hash);
                return;
            }
            var result = VocalActivity.FromStemRms(rms, WindowSeconds);

            await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var row = await db.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == hash, ct).ConfigureAwait(false);
                if (row is null) return;
                VocalActivity.Apply(result, row);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "[VocalActivity] {Hash}: density {Density:0.00} → {Type}, span {Start:F1}s–{End:F1}s.",
                hash, result.Density, result.Type, result.StartSeconds, result.EndSeconds);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    /// <summary>Best-effort removal of the per-run separation output so "Check clashes" doesn't fill the temp drive.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // intentional: a locked temp file (AV scanner, open handle) must not fail detection; the OS temp cleaner reaps it.
        }
        catch (UnauthorizedAccessException)
        {
            // intentional: same — temp cleanup must never break the user's clash check.
        }
    }

    /// <summary>The first on-disk path for a hash, in the analysis queue's preference order, or null.</summary>
    private static async Task<string?> ResolvePathAsync(AppDbContext db, string hash, CancellationToken ct)
    {
        var lib = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.UniqueHash == hash && e.FilePath != "")
            .Select(e => e.FilePath)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(lib) && File.Exists(lib)) return lib;

        var track = await db.Tracks.AsNoTracking()
            .Where(t => t.GlobalId == hash && t.LocalFilePath != null && t.LocalFilePath != "")
            .Select(t => t.LocalFilePath)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(track) && File.Exists(track)) return track;

        var playlist = await db.PlaylistTracks.AsNoTracking()
            .Where(t => t.TrackUniqueHash == hash && t.ResolvedFilePath != null && t.ResolvedFilePath != "")
            .Select(t => t.ResolvedFilePath)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(playlist) && File.Exists(playlist)) return playlist;

        return null;
    }

    /// <summary>Reads an isolated vocal stem into one RMS value per <paramref name="windowSeconds"/> window.</summary>
    internal static float[] ReadStemRms(string vocalPath, double windowSeconds, CancellationToken ct)
    {
        float[] samples;
        int rate, channels;
        using (var reader = new AudioFileReader(vocalPath))
        {
            rate = reader.WaveFormat.SampleRate;
            channels = reader.WaveFormat.Channels;
            var total = (int)(reader.Length / sizeof(float));
            var buffer = new float[total];
            var read = reader.Read(buffer, 0, total);
            samples = buffer[..read];
        }

        return ComputeWindowRms(samples, channels, rate, windowSeconds, ct);
    }

    /// <summary>RMS per window over interleaved float PCM, averaged to mono. The final partial window is kept.</summary>
    internal static float[] ComputeWindowRms(float[] interleaved, int channels, int sampleRate, double windowSeconds, CancellationToken ct = default)
    {
        if (interleaved.Length == 0 || channels <= 0 || sampleRate <= 0 || windowSeconds <= 0)
            return Array.Empty<float>();

        var windowFrames = Math.Max(1, (int)Math.Round(sampleRate * windowSeconds));
        var frames = interleaved.Length / channels;
        var windowCount = (int)Math.Ceiling(frames / (double)windowFrames);
        var rms = new float[windowCount];

        for (var w = 0; w < windowCount; w++)
        {
            ct.ThrowIfCancellationRequested();
            var startFrame = w * windowFrames;
            var endFrame = Math.Min(frames, startFrame + windowFrames);
            double sumSquares = 0.0;
            for (var f = startFrame; f < endFrame; f++)
            {
                float sample = 0f;
                for (var c = 0; c < channels; c++)
                    sample += interleaved[f * channels + c];
                sample /= channels;
                sumSquares += (double)sample * sample;
            }
            var count = endFrame - startFrame;
            rms[w] = count > 0 ? (float)Math.Sqrt(sumSquares / count) : 0f;
        }

        return rms;
    }
}
