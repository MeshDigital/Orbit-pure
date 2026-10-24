using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace SLSKDONET.Services.EditLab;

public enum EditRenderFormat { Wav, Flac }

/// <summary>What an offline render produced.</summary>
public sealed record EditRenderResult(string OutputPath, long Frames, int SampleRate, float Peak, long ClippedSamples)
{
    public double DurationSeconds => Frames / (double)SampleRate;
}

/// <summary>
/// Renders an <see cref="EditRenderPlan"/> (or a frame range of it) to a 24-bit WAV, or to FLAC via
/// ffmpeg, at the plan's sample rate. Progress is 0..1; cancelling deletes the partial file.
/// Samples beyond full scale are clamped and counted so the caller can warn about clipping.
/// </summary>
public static class EditOfflineRenderer
{
    private const int BlockFrames = 4096;

    public static async Task<EditRenderResult> RenderAsync(
        EditRenderPlan plan,
        string outputPath,
        EditRenderFormat format,
        long startFrame = 0,
        long? endFrame = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (plan.Problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", plan.Problems));
        long end = endFrame ?? plan.TotalFrames;
        long start = Math.Clamp(startFrame, 0, end);
        if (end - start <= 0)
            throw new InvalidOperationException("There's nothing to render: add clips to the timeline first.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string wavPath = format == EditRenderFormat.Wav
            ? outputPath
            : Path.Combine(Path.GetTempPath(), $"orbit-edit-{Guid.NewGuid():N}.wav");

        try
        {
            var (peak, clipped) = await Task.Run(() => RenderWav(plan, wavPath, start, end, format == EditRenderFormat.Wav ? progress : Scale(progress, 0.85), ct), ct)
                .ConfigureAwait(false);

            if (format == EditRenderFormat.Flac)
            {
                await TranscodeFlacAsync(wavPath, outputPath, ct).ConfigureAwait(false);
            }

            progress?.Report(1.0);
            return new EditRenderResult(outputPath, end - start, plan.SampleRate, peak, clipped);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            if (!string.Equals(wavPath, outputPath, StringComparison.OrdinalIgnoreCase)) TryDelete(wavPath);
        }
    }

    private static IProgress<double>? Scale(IProgress<double>? progress, double factor) =>
        progress == null ? null : new Progress<double>(p => progress.Report(p * factor));

    private static (float Peak, long Clipped) RenderWav(EditRenderPlan plan, string wavPath, long start, long end, IProgress<double>? progress, CancellationToken ct)
    {
        using var renderer = new EditTimelineRenderer(plan, start, throwOnVoiceError: true);
        using var writer = new WaveFileWriter(wavPath, new WaveFormat(plan.SampleRate, 24, 2));
        var buffer = new float[BlockFrames * 2];
        var bytes = new byte[BlockFrames * 2 * 3];
        float peak = 0;
        long clipped = 0;
        long total = end - start;
        long done = 0;
        double lastReported = -1;

        while (done < total)
        {
            ct.ThrowIfCancellationRequested();
            int frames = (int)Math.Min(BlockFrames, total - done);
            renderer.Read(buffer, 0, frames * 2);
            int b = 0;
            for (int i = 0; i < frames * 2; i++)
            {
                float s = buffer[i];
                float a = Math.Abs(s);
                if (a > peak) peak = a;
                if (a > 1f) { clipped++; s = s > 0 ? 1f : -1f; }
                int v = (int)Math.Round(s * 8388607.0);
                bytes[b++] = (byte)v;
                bytes[b++] = (byte)(v >> 8);
                bytes[b++] = (byte)(v >> 16);
            }
            writer.Write(bytes, 0, b);
            done += frames;

            double p = done / (double)total;
            if (p - lastReported >= 0.01 || done == total)
            {
                lastReported = p;
                progress?.Report(p);
            }
        }
        return (peak, clipped);
    }

    private static async Task TranscodeFlacAsync(string wavPath, string flacPath, CancellationToken ct)
    {
        string ffmpeg = SLSKDONET.Services.AudioAnalysis.AudioIngestionPipeline.ResolveFfmpegPath();
        var psi = new ProcessStartInfo(ffmpeg)
        {
            Arguments = $"-v error -y -i \"{wavPath}\" -c:a flac -compression_level 5 -sample_fmt s32 \"{flacPath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"ffmpeg didn't start ({ffmpeg}).");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); }
            catch (InvalidOperationException)
            {
                // intentional: the process already exited
            }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg couldn't write the FLAC file: {(await stderr.ConfigureAwait(false)).Trim()}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            Serilog.Log.Warning(ex, "[EditLab] Couldn't delete {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            Serilog.Log.Warning(ex, "[EditLab] Couldn't delete {Path}", path);
        }
    }
}
