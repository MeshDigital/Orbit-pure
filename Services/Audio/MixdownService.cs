using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SLSKDONET.Services.Audio;

/// <summary>Output format for <see cref="MixdownService"/>.</summary>
public enum ExportFormat { Wav, Mp3, Flac }

/// <summary>Optional quality settings for a mixdown export.</summary>
public record ExportSettings(
    ExportFormat Format    = ExportFormat.Wav,
    bool         Normalize = false,
    bool         Dither    = false,
    int          BitDepth  = 16,
    int          SampleRate = 44100);

/// <summary>
/// Offline render: reads each deck source file from disk and writes a stereo
/// 44 100 Hz / 32-bit IEEE-float WAV to <paramref name="outputPath"/>.
/// No real-time playback engine is involved — pure file-to-file.
/// </summary>
public sealed class MixdownService
{
    /// <summary>Describes one input deck for the mixdown.</summary>
    public record DeckSource(string FilePath, float Volume = 1f, bool IsMuted = false);

    /// <summary>
    /// Mixes the supplied deck sources offline and writes the result to
    /// <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="sources">Active deck sources (muted tracks are skipped).</param>
    /// <param name="outputPath">Destination file path (created/overwritten).</param>
    /// <param name="settings">Optional quality / format settings.</param>
    /// <param name="progress">Optional 0–1 progress callback.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ExportAsync(
        IEnumerable<DeckSource>  sources,
        string                   outputPath,
        ExportSettings?          settings = null,
        IProgress<double>?       progress = null,
        CancellationToken        ct       = default)
    {
        settings ??= new ExportSettings();
        var active = sources
            .Where(s => !s.IsMuted && File.Exists(s.FilePath))
            .ToList();

        if (active.Count == 0)
            throw new InvalidOperationException("No playable sources to mix down.");

        // For non-WAV formats we first render to a temp WAV then transcode with FFmpeg.
        bool needsTranscode = settings.Format != ExportFormat.Wav;
        string wavPath = needsTranscode
            ? Path.ChangeExtension(Path.GetTempFileName(), ".wav")
            : outputPath;

        await Task.Run(() =>
        {
            var readers = new List<AudioFileReader>();
            try
            {
                var providers = new List<ISampleProvider>();
                foreach (var src in active)
                {
                    ct.ThrowIfCancellationRequested();
                    var reader   = new AudioFileReader(src.FilePath);
                    readers.Add(reader);
                    var resampled = new WdlResamplingSampleProvider(reader,
                        WaveFormat.CreateIeeeFloatWaveFormat(settings.SampleRate, 2).SampleRate);
                    var vol = new VolumeSampleProvider(resampled) { Volume = src.Volume };
                    providers.Add(vol);
                }

                var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(settings.SampleRate, 2));
                foreach (var p in providers)
                    mixer.AddMixerInput(p);

                long totalSamples = readers.Max(r =>
                    (long)(r.TotalTime.TotalSeconds * settings.SampleRate) * 2);

                Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);

                if (settings.Normalize)
                {
                    // Two-pass: buffer entire mix, peak-normalize to −0.1 dBFS, then write.
                    var allSamples = new List<float>();
                    var buf2 = new float[4096];
                    int r2;
                    while ((r2 = mixer.Read(buf2, 0, buf2.Length)) > 0)
                        for (int i = 0; i < r2; i++) allSamples.Add(buf2[i]);

                    float peakVal = allSamples.Count > 0 ? allSamples.Max(Math.Abs) : 1f;
                    float gain    = peakVal > 0.001f ? (float)(Math.Pow(10, -0.1 / 20.0) / peakVal) : 1f;

                    using var wn = new WaveFileWriter(wavPath, mixer.WaveFormat);
                    const int chunk = 4096;
                    for (int offset = 0; offset < allSamples.Count; offset += chunk)
                    {
                        ct.ThrowIfCancellationRequested();
                        int count = Math.Min(chunk, allSamples.Count - offset);
                        var seg = new float[count];
                        for (int i = 0; i < count; i++) seg[i] = allSamples[offset + i] * gain;
                        wn.WriteSamples(seg, 0, count);
                        if (totalSamples > 0)
                            progress?.Report(Math.Min(0.8, (double)(offset + count) / totalSamples * 0.8));
                    }
                }
                else
                {
                    using var writer = new WaveFileWriter(wavPath, mixer.WaveFormat);
                    var buffer = new float[4096];
                    long written = 0;
                    int  read;
                    while ((read = mixer.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        writer.WriteSamples(buffer, 0, read);
                        written += read;
                        if (totalSamples > 0)
                            progress?.Report(Math.Min(0.8, (double)written / totalSamples * 0.8));
                    }
                }

                if (!needsTranscode) progress?.Report(1.0);
            }
            finally
            {
                foreach (var r in readers) r.Dispose();
            }
        }, ct);

        if (needsTranscode)
        {
            await TranscodeWithFfmpegAsync(wavPath, outputPath, settings, progress, ct);
            try { File.Delete(wavPath); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ── FFmpeg transcode ──────────────────────────────────────────────────────

    private static async Task TranscodeWithFfmpegAsync(
        string             inputWav,
        string             outputPath,
        ExportSettings     settings,
        IProgress<double>? progress,
        CancellationToken  ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string formatArgs = settings.Format switch
        {
            ExportFormat.Mp3  => "-codec:a libmp3lame -q:a 2",
            ExportFormat.Flac => $"-codec:a flac -compression_level 8 -sample_fmt s{settings.BitDepth}",
            _                 => string.Empty
        };

        var psi = new ProcessStartInfo("ffmpeg")
        {
            Arguments              = $"-y -i \"{inputWav}\" {formatArgs} \"{outputPath}\"",
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg. Ensure it is on PATH.");

        // Read stderr asynchronously (ffmpeg logs to stderr)
        _ = Task.Run(() =>
        {
            string? line;
            while ((line = proc.StandardError.ReadLine()) != null)
            {
                // Could parse "time=HH:MM:SS.ss" for granular progress; for now just report 90%
                if (line.Contains("time="))
                    progress?.Report(0.9);
            }
        }, ct);

        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {proc.ExitCode}.");

        progress?.Report(1.0);
    }
}
