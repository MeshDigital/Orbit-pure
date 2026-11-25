using System;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>#455: IAudioOutputBackend cross-platform command selection and PosixPcmWavePlayer streaming tests.</summary>
public sealed class AudioOutputBackendTests
{
    [Fact]
    public void PosixBackend_LinuxPrefersPipeWireThenPulseAudioThenFfplay()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        var pwPath = Path.Combine("/usr/bin", "pw-play");
        var pwBackend = new PosixAudioOutputBackend(isMacOS: false, fileExists: p => p == pwPath, pathEnv: "");
        var pwPsi = pwBackend.BuildPlayerStartInfo(fmt, null);
        Assert.Equal(pwPath, pwPsi.FileName);
        Assert.Contains("--format=f32", pwPsi.ArgumentList);
        Assert.Contains("--rate=44100", pwPsi.ArgumentList);

        var paPath = Path.Combine("/usr/bin", "paplay");
        var paBackend = new PosixAudioOutputBackend(isMacOS: false, fileExists: p => p == paPath, pathEnv: "");
        var paPsi = paBackend.BuildPlayerStartInfo(fmt, "alsa_output.pci");
        Assert.Equal(paPath, paPsi.FileName);
        Assert.Contains("--device=alsa_output.pci", paPsi.ArgumentList);
    }

    [Fact]
    public void PosixBackend_MacOSUsesFfplayFromHomebrewOrPath()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var brewFfplay = Path.Combine("/opt/homebrew/bin", "ffplay");
        var macBackend = new PosixAudioOutputBackend(isMacOS: true, fileExists: p => p == brewFfplay, pathEnv: "");

        Assert.Equal("macOS (CoreAudio/ffplay)", macBackend.BackendName);
        Assert.Empty(macBackend.GetAsioDriverNames());

        var psi = macBackend.BuildPlayerStartInfo(fmt, null);
        Assert.Equal(brewFfplay, psi.FileName);
        Assert.Contains("-nodisp", psi.ArgumentList);
        Assert.Contains("f32le", psi.ArgumentList);
        Assert.Contains("48000", psi.ArgumentList);
    }

    [Fact]
    public void PosixPcmWavePlayer_StreamsScaledFloatPcmAndRaisesPlaybackStopped()
    {
        using var sink = new MemoryStream();
        var stopped = new ManualResetEventSlim(false);
        Exception? stopError = null;

        using var player = new PosixPcmWavePlayer(_ => new NonClosingStream(sink));
        player.PlaybackStopped += (_, e) => { stopError = e.Exception; stopped.Set(); };
        player.Volume = 0.5f;

        var source = new ConstantSampleProvider(sampleCount: 1024, value: 0.8f);
        player.Init(source.ToWaveProvider());
        player.Play();

        Assert.True(stopped.Wait(TimeSpan.FromSeconds(5)), "PlaybackStopped did not fire in time");
        Assert.Null(stopError);
        Assert.Equal(PlaybackState.Stopped, player.PlaybackState);
        Assert.Equal(1024 * sizeof(float), sink.Length);

        var firstSample = BitConverter.ToSingle(sink.ToArray(), 0);
        Assert.InRange(firstSample, 0.399f, 0.401f); // 0.8 * 0.5 volume
    }

    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private int _remaining;
        private readonly float _value;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        public ConstantSampleProvider(int sampleCount, float value)
        {
            _remaining = sampleCount;
            _value = value;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _remaining);
            for (int i = 0; i < n; i++) buffer[offset + i] = _value;
            _remaining -= n;
            return n;
        }
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* keep inner MemoryStream readable for assertions */ }
    }
}
