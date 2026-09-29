using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Opens any library file for playback. NAudio decodes FLAC/Opus/Ogg through Windows Media
/// Foundation, which rejects some perfectly valid files ("byte stream type unsupported",
/// 0xC00D36C4) — measured 2026-09-29: 123 of 3,422 library files (71 FLAC, 49 Opus, 2 Ogg, 1 WAV)
/// could not be played at all. Playback just stopped, while the UI kept the previous track's
/// length and playhead (analysis/waveforms use ffmpeg, so those tracks looked fine in the library).
///
/// Such files are decoded once with ffmpeg into a cached WAV (%LOCALAPPDATA%\ORBIT\PlaybackCache,
/// size-capped, oldest removed first) and played from there — exact length, exact seeking.
/// </summary>
public static class PlayableAudio
{
    private const long CacheLimitBytes = 4L * 1024 * 1024 * 1024; // 4 GB
    private static readonly ConcurrentDictionary<string, string> Resolved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ConvertLock = new();

    public static string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORBIT", "PlaybackCache");

    /// <summary>Opens <paramref name="path"/>, converting through ffmpeg if Media Foundation can't decode it.
    /// <paramref name="playablePath"/> is the file actually being read (the original or its cached WAV).</summary>
    public static AudioFileReader Open(string path, out string playablePath)
    {
        if (Resolved.TryGetValue(path, out var known) && File.Exists(known))
        {
            playablePath = known;
            return new AudioFileReader(known);
        }

        try
        {
            var reader = new AudioFileReader(path);
            playablePath = path;
            return reader;
        }
        catch (Exception ex) when (File.Exists(path))
        {
            Serilog.Log.Information("[PlayableAudio] Windows can't decode {File} ({Error}) — using an ffmpeg-decoded copy",
                Path.GetFileName(path), ex.Message.Split('\n')[0].Trim());
            playablePath = EnsureDecodedCopy(path);
            return new AudioFileReader(playablePath);
        }
    }

    /// <summary>Returns the cached WAV for <paramref name="path"/>, creating it with ffmpeg if needed.</summary>
    public static string EnsureDecodedCopy(string path)
    {
        var info = new FileInfo(path);
        var key = Hash($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        var wav = Path.Combine(CacheDirectory, key + ".wav");

        lock (ConvertLock)
        {
            if (!File.Exists(wav))
            {
                Directory.CreateDirectory(CacheDirectory);
                var temp = wav + ".tmp.wav";
                // Lossy sources gain nothing from more than 16 bits; FLAC may be 24-bit.
                var codec = info.Extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ? "pcm_s24le" : "pcm_s16le";
                var sw = Stopwatch.StartNew();
                RunFfmpeg($"-v error -y -i \"{path}\" -vn -map 0:a:0 -c:a {codec} \"{temp}\"");
                File.Move(temp, wav, overwrite: true);
                Serilog.Log.Information("[PlayableAudio] Decoded {File} in {Ms} ms ({Mb:0.0} MB cached)",
                    info.Name, sw.ElapsedMilliseconds, new FileInfo(wav).Length / 1e6);
                TrimCache(keep: wav);
            }
            else
            {
                File.SetLastAccessTimeUtc(wav, DateTime.UtcNow);
            }
        }

        Resolved[path] = wav;
        return wav;
    }

    private static void RunFfmpeg(string arguments)
    {
        var ffmpeg = AudioAnalysis.AudioIngestionPipeline.ResolveFfmpegPath();
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("ffmpeg did not start");
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("ffmpeg decode timed out");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not decode the file: {error.Trim()}");
    }

    private static void TrimCache(string keep)
    {
        try
        {
            var files = new DirectoryInfo(CacheDirectory).GetFiles("*.wav")
                .Where(f => !f.Name.EndsWith(".tmp.wav"))
                .OrderByDescending(f => f.LastAccessTimeUtc)
                .ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > CacheLimitBytes && !string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase))
                {
                    try { f.Delete(); } catch { /* in use — next trim */ }
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[PlayableAudio] Cache trim failed");
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..20].ToLowerInvariant();
}
