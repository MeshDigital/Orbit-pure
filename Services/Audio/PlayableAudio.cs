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
                // Lossy sources gain nothing from more than 16 bits; FLAC/AIFF-C/APE may be 24-bit.
                var codec = AudioFormats.IsLossless(info.Extension) ? "pcm_s24le" : "pcm_s16le";
                var sw = Stopwatch.StartNew();
                RunFfmpeg($"-v error -y -i \"{path}\" -vn -map 0:a:0 -c:a {codec} \"{temp}\"");
                UnwrapExtensibleFormat(temp);
                File.Move(temp, wav, overwrite: true);
                Serilog.Log.Information("[PlayableAudio] Decoded {File} in {Ms} ms ({Mb:0.0} MB cached)",
                    info.Name, sw.ElapsedMilliseconds, new FileInfo(wav).Length / 1e6);
                TrimCache(keep: wav);
            }
            else
            {
                UnwrapExtensibleFormat(wav); // copies cached before the header fix
                File.SetLastAccessTimeUtc(wav, DateTime.UtcNow);
            }
        }

        Resolved[path] = wav;
        return wav;
    }

    /// <summary>
    /// ffmpeg writes WAVE_FORMAT_EXTENSIBLE for anything over 16 bits or 48 kHz. NAudio's
    /// AudioFileReader only accepts plain PCM/float and hands EXTENSIBLE to an ACM converter that
    /// fails ("NoDriver calling acmFormatSuggest"), so every 24-bit fallback copy (FLAC, AIFF-C)
    /// could not be opened. The header is rewritten to the plain format tag its sub-format names;
    /// the samples are unchanged. Internal for tests.
    /// </summary>
    internal static void UnwrapExtensibleFormat(string wavPath)
    {
        try
        {
            UnwrapExtensibleFormatCore(wavPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Open by a player right now (already fixed, or fixed on the next open).
            Serilog.Log.Warning(ex, "[PlayableAudio] Couldn't rewrite the WAV header of {File}", Path.GetFileName(wavPath));
        }
    }

    private static void UnwrapExtensibleFormatCore(string wavPath)
    {
        using var stream = new FileStream(wavPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var head = new byte[12];
        if (stream.Read(head, 0, 12) != 12 || Encoding.ASCII.GetString(head, 0, 4) != "RIFF" || Encoding.ASCII.GetString(head, 8, 4) != "WAVE")
            return;

        var chunk = new byte[8];
        while (stream.Read(chunk, 0, 8) == 8)
        {
            var size = BitConverter.ToUInt32(chunk, 4);
            if (Encoding.ASCII.GetString(chunk, 0, 4) != "fmt ")
            {
                stream.Position += size + (size & 1);
                continue;
            }
            if (size < 40) return;
            var fmt = new byte[40];
            if (stream.Read(fmt, 0, 40) != 40) return;
            if (BitConverter.ToUInt16(fmt, 0) != 0xFFFE) return;
            // The sub-format GUID (offset 24) starts with the plain format tag: 1 = PCM, 3 = IEEE float.
            var subFormat = BitConverter.ToUInt16(fmt, 24);
            if (subFormat is not (1 or 3)) return;
            stream.Position -= 40;
            stream.Write(BitConverter.GetBytes(subFormat), 0, 2);
            return;
        }
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
