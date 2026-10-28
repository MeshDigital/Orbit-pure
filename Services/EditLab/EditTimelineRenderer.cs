using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.Services.EditLab;

/// <summary>
/// Pulls a <see cref="EditRenderPlan"/> as stereo float audio at the plan's sample rate, starting
/// at any frame. Each clip is a <see cref="ClipVoice"/> opened the moment the timeline reaches it
/// (or mid-clip after a seek) and mixed in at its exact frame, so clip starts are sample-accurate.
/// Used directly by the offline render and on a background thread by the live preview.
/// </summary>
public sealed class EditTimelineRenderer : ISampleProvider, IDisposable
{
    private readonly EditRenderPlan _plan;
    private readonly List<ClipVoice> _active = new();
    private readonly bool _throwOnVoiceError;
    private float[] _scratch = Array.Empty<float>();
    private int _nextClip;

    /// <summary>Next frame to be produced.</summary>
    public long Position { get; private set; }

    public WaveFormat WaveFormat { get; }

    /// <summary>Clips whose audio couldn't be opened (the clip is skipped). Read by the preview to report.</summary>
    public List<string> Errors { get; } = new();

    public EditTimelineRenderer(EditRenderPlan plan, long startFrame = 0, bool throwOnVoiceError = false)
    {
        _plan = plan;
        _throwOnVoiceError = throwOnVoiceError;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(plan.SampleRate, 2);
        Position = Math.Max(0, startFrame);

        var clips = plan.Clips;
        while (_nextClip < clips.Count && clips[_nextClip].StartFrame <= Position)
        {
            var clip = clips[_nextClip++];
            if (clip.EndFrame > Position) Activate(clip, Position - clip.StartFrame);
        }
    }

    private void Activate(ClipPlan clip, long offsetInClip)
    {
        try
        {
            _active.Add(new ClipVoice(clip, _plan.SampleRate, offsetInClip));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            var message = $"Couldn't open {Path.GetFileName(clip.AudioPath)}: {ex.Message}";
            if (_throwOnVoiceError) throw new InvalidOperationException(message, ex);
            Serilog.Log.Warning(ex, "[EditLab] {Message}", message);
            lock (Errors) Errors.Add(message);
        }
    }

    /// <summary>Always fills <paramref name="count"/> samples (silence past the last clip).</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        Array.Clear(buffer, offset, frames * 2);
        long blockEnd = Position + frames;

        var clips = _plan.Clips;
        while (_nextClip < clips.Count && clips[_nextClip].StartFrame < blockEnd)
            Activate(clips[_nextClip++], 0);

        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];

        for (int v = _active.Count - 1; v >= 0; v--)
        {
            var voice = _active[v];
            int writeStart = (int)Math.Max(0, voice.Plan.StartFrame - Position);
            int want = frames - writeStart;
            if (want > 0)
            {
                int got = voice.Read(_scratch, want);
                int dst = offset + writeStart * 2;
                for (int i = 0; i < got * 2; i++) buffer[dst + i] += _scratch[i];
            }
            if (voice.Finished)
            {
                voice.Dispose();
                _active.RemoveAt(v);
            }
        }

        Position = blockEnd;
        return frames * 2;
    }

    public void Dispose()
    {
        foreach (var voice in _active) voice.Dispose();
        _active.Clear();
    }
}

/// <summary>
/// One playing clip: file → exact seek to the in-point → stereo → speed/sample-rate conversion
/// (4-point Hermite, zero latency) → optional key-lock / semitone shift (NAudio's SMB phase
/// vocoder, latency-compensated) → gain and linear fades.
/// </summary>
internal sealed class ClipVoice : IDisposable
{
    /// <summary>Output delay of <see cref="SmbPitchShiftingSampleProvider"/> (FFT 4096, osamp 4:
    /// fftFrameSize − stepSize). Discarded at voice start so shifted clips stay on the grid.</summary>
    internal const int SmbLatencyFrames = 3072;

    private readonly AudioFileReader _reader;
    private readonly ISampleProvider _chain;
    private readonly int _outRate;
    private long _clipFrame;
    private int _discardFrames;
    private bool _sourceEnded;
    private float[] _discardBuffer = Array.Empty<float>();

    public ClipPlan Plan { get; }
    public bool Finished => _clipFrame >= Plan.LengthFrames;

    public ClipVoice(ClipPlan plan, int outRate, long offsetInClip)
    {
        Plan = plan;
        _outRate = outRate;
        _clipFrame = Math.Max(0, offsetInClip);

        _reader = new AudioFileReader(plan.AudioPath);
        try
        {
            int srcRate = _reader.WaveFormat.SampleRate;
            double step = srcRate * plan.Speed / outRate;
            double srcFrame = plan.SourceInSeconds * srcRate + _clipFrame * step;
            double nearest = Math.Round(srcFrame);
            if (Math.Abs(srcFrame - nearest) < 1e-6) srcFrame = nearest; // keep whole-frame starts bit-exact

            long leadFrames = 0;
            double frac = 0;
            if (srcFrame < 0)
            {
                leadFrames = (long)Math.Round(-srcFrame);
            }
            else
            {
                long whole = (long)Math.Floor(srcFrame);
                frac = srcFrame - whole;
                if (whole > 0) SeekToFrame(_reader, plan.AudioPath, whole);
            }

            ISampleProvider chain = ToStereo(_reader);
            if (leadFrames > 0) chain = new OffsetSampleProvider(chain) { DelayBySamples = (int)Math.Min(int.MaxValue, leadFrames * 2) };
            chain = new StereoRateResampler(chain, outRate, step, frac);

            if (Math.Abs(plan.PitchFactor - 1.0) > 1e-6)
            {
                chain = new SmbPitchShiftingSampleProvider(chain) { PitchFactor = (float)plan.PitchFactor };
                _discardFrames = SmbLatencyFrames;
            }
            _chain = chain;
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    /// <summary>Frame-exact seek: WAV/AIFF/MP3 by byte position (a TimeSpan round-trip can land one
    /// frame early), Media Foundation formats (FLAC, M4A…) through <see cref="ExactSeek"/>.</summary>
    private static void SeekToFrame(AudioFileReader reader, string path, long frame)
    {
        if (ExactSeek.NeedsDecodeForward(path))
        {
            ExactSeek.Seek(reader, path, frame / (double)reader.WaveFormat.SampleRate);
            return;
        }
        long bytes = frame * reader.WaveFormat.BlockAlign;
        reader.Position = Math.Min(bytes, reader.Length);
    }

    private static ISampleProvider ToStereo(ISampleProvider source) => source.WaveFormat.Channels switch
    {
        1 => new MonoToStereoSampleProvider(source),
        2 => source,
        _ => new FirstTwoChannelsSampleProvider(source),
    };

    /// <summary>Writes up to <paramref name="frames"/> enveloped frames to <paramref name="buffer"/>[0..];
    /// returns the frames written (the rest of the clip once the source runs out is silence).</summary>
    public int Read(float[] buffer, int frames)
    {
        long remaining = Plan.LengthFrames - _clipFrame;
        int n = (int)Math.Min(frames, Math.Max(0, remaining));
        if (n == 0) return 0;

        if (_discardFrames > 0)
        {
            if (_discardBuffer.Length < 4096) _discardBuffer = new float[4096];
            while (_discardFrames > 0)
            {
                int take = Math.Min(_discardFrames, _discardBuffer.Length / 2);
                int got = _chain.Read(_discardBuffer, 0, take * 2) / 2;
                if (got <= 0) { _sourceEnded = true; break; }
                _discardFrames -= got;
            }
            _discardFrames = 0;
        }

        int read = 0;
        while (read < n && !_sourceEnded)
        {
            int got = _chain.Read(buffer, read * 2, (n - read) * 2) / 2;
            if (got <= 0) { _sourceEnded = true; break; }
            read += got;
        }
        if (read < n) Array.Clear(buffer, read * 2, (n - read) * 2);

        ApplyEnvelope(buffer, n);
        _clipFrame += n;
        return n;
    }

    private void ApplyEnvelope(float[] buffer, int n)
    {
        long len = Plan.LengthFrames;
        long fadeIn = Plan.FadeInFrames, fadeOut = Plan.FadeOutFrames;
        float gain = Plan.Gain;
        for (int i = 0; i < n; i++)
        {
            long f = _clipFrame + i;
            float g = gain;
            if (fadeIn > 0 && f < fadeIn) g *= f / (float)fadeIn;
            long fromEnd = len - f;
            if (fadeOut > 0 && fromEnd <= fadeOut) g *= (fromEnd - 1) / (float)fadeOut;
            buffer[i * 2] *= g;
            buffer[i * 2 + 1] *= g;
        }
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>Keeps channels 0 and 1 of a multichannel source.</summary>
internal sealed class FirstTwoChannelsSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buf = Array.Empty<float>();

    public FirstTwoChannelsSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        if (_buf.Length < frames * _channels) _buf = new float[frames * _channels];
        int got = _source.Read(_buf, 0, frames * _channels) / _channels;
        for (int i = 0; i < got; i++)
        {
            buffer[offset + i * 2] = _buf[i * _channels];
            buffer[offset + i * 2 + 1] = _buf[i * _channels + 1];
        }
        return got * 2;
    }
}

/// <summary>
/// Stereo variable-rate resampler: reads the source at <paramref name="step"/> source frames per
/// output frame using 4-point Hermite interpolation. One step does sample-rate conversion and
/// tempo change together, with zero latency (output frame 0 is source frame <c>startFraction</c>),
/// which keeps stretched clips exactly on the bar grid. Passes samples straight through at step 1.
/// </summary>
internal sealed class StereoRateResampler : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly double _step;
    private readonly bool _bypass;
    private float[] _buf = new float[16384]; // interleaved stereo
    private int _bufFrames;
    private double _pos; // fractional frame index into _buf
    private bool _eof;

    public StereoRateResampler(ISampleProvider source, int outRate, double step, double startFraction)
    {
        _source = source;
        _step = step;
        _pos = startFraction;
        _bypass = Math.Abs(step - 1.0) < 1e-12 && startFraction == 0;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_bypass) return _source.Read(buffer, offset, count);

        int frames = count / 2;
        int produced = 0;
        while (produced < frames)
        {
            int i = (int)Math.Floor(_pos);
            if (i + 2 >= _bufFrames && !_eof)
            {
                Refill(i);
                continue;
            }
            if (i >= _bufFrames) break; // source exhausted

            double t = _pos - i;
            int o = offset + produced * 2;
            buffer[o] = Interpolate(i, 0, t);
            buffer[o + 1] = Interpolate(i, 1, t);
            produced++;
            _pos += _step;
        }
        return produced * 2;
    }

    private float Sample(int frame, int ch)
    {
        if (frame < 0) frame = 0;
        return frame < _bufFrames ? _buf[frame * 2 + ch] : 0f;
    }

    private float Interpolate(int i, int ch, double t)
    {
        float xm1 = Sample(i - 1, ch), x0 = Sample(i, ch), x1 = Sample(i + 1, ch), x2 = Sample(i + 2, ch);
        double c1 = 0.5 * (x1 - xm1);
        double c2 = xm1 - 2.5 * x0 + 2.0 * x1 - 0.5 * x2;
        double c3 = 0.5 * (x2 - xm1) + 1.5 * (x0 - x1);
        return (float)(((c3 * t + c2) * t + c1) * t + x0);
    }

    /// <summary>Drops frames before <c>i − 1</c> and tops the buffer up from the source.</summary>
    private void Refill(int i)
    {
        int keepFrom = Math.Max(0, i - 1);
        if (keepFrom > 0)
        {
            int keep = Math.Max(0, _bufFrames - keepFrom);
            Array.Copy(_buf, keepFrom * 2, _buf, 0, keep * 2);
            _bufFrames = keep;
            _pos -= keepFrom;
        }
        if (_bufFrames * 2 >= _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);

        int got = _source.Read(_buf, _bufFrames * 2, _buf.Length - _bufFrames * 2);
        if (got <= 0) _eof = true;
        else _bufFrames += got / 2;
    }
}
