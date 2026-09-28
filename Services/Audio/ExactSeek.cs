using System;
using System.IO;
using NAudio.Wave;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Sample-accurate seeking for NAudio's <see cref="AudioFileReader"/>.
///
/// AudioFileReader decodes WAV/AIFF/MP3 itself and seeks those exactly, but hands every other
/// format (FLAC, M4A/AAC, OGG, WMA…) to Windows Media Foundation, whose seeking was measured on
/// this library (2026-09-28) to be broken in two ways:
/// <list type="bullet">
/// <item>a seek set before the first Read is silently dropped — playback starts at 0:00 while
/// CurrentTime still reports the requested time (every FLAC waveform click in the Mix editor
/// played from the top, and Mix transitions started the incoming FLAC track at 0:00 instead of
/// its mix-in point);</item>
/// <item>a seek after reading lands only near the target — up to ~0.9 s off, useless for
/// auditioning a cue nudged in 10 ms steps.</item>
/// </list>
/// The one thing Media Foundation does get exactly right is going back to the start, so an exact
/// seek is: prime, rewind to 0, decode forward to the target sample. That costs about 1.2 ms per
/// second of audio (~70 ms at one minute, ~300 ms at four), so callers run it off the UI thread.
/// </summary>
public static class ExactSeek
{
    private static readonly string[] NativelyExactExtensions = { ".wav", ".wave", ".mp3", ".aif", ".aiff" };

    /// <summary>True when the file is decoded by Media Foundation and needs <see cref="Seek"/> to land exactly.</summary>
    public static bool NeedsDecodeForward(string path) =>
        Array.IndexOf(NativelyExactExtensions, Path.GetExtension(path).ToLowerInvariant()) < 0;

    /// <summary>Positions <paramref name="reader"/> exactly at <paramref name="seconds"/>. Blocking — call off the UI thread for Media Foundation formats.</summary>
    public static void Seek(AudioFileReader reader, string path, double seconds)
    {
        var target = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, reader.TotalTime.TotalSeconds - 0.05)));
        if (!NeedsDecodeForward(path))
        {
            reader.CurrentTime = target;
            return;
        }

        // Prime (a seek before the first read is ignored), rewind exactly, decode forward.
        reader.Read(new float[64], 0, 64);
        reader.CurrentTime = TimeSpan.Zero;
        if (target <= TimeSpan.Zero) return;

        long remaining = (long)Math.Round(target.TotalSeconds * reader.WaveFormat.SampleRate) * reader.WaveFormat.Channels;
        var buffer = new float[65536];
        while (remaining > 0)
        {
            int read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) break;
            remaining -= read;
        }
    }
}
