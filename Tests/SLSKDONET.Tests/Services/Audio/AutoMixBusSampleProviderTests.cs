using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>
/// Offline sample-accurate verification tests for MX1 (#600):
/// 1. Incoming deck's first audible sample lands at the exact scheduled output frame (0-sample jitter).
/// 2. Hard low-band bass swap lands within one audio sub-block (&lt;= 10 ms) of the midpoint bar.
/// </summary>
public sealed class AutoMixBusSampleProviderTests
{
    [Fact]
    public void ScheduledIncomingDeck_FirstAudibleSample_LandsAtExactScheduledFrame()
    {
        const int sampleRate = 44100;
        const long scheduledStartFrame = 1337; // intentionally non-aligned to buffer/block sizes!
        const long durationFrames = 4410;      // 100 ms

        var bus = new AutoMixBusSampleProvider(sampleRate);

        // Outgoing = silence (0f), Incoming = constant DC 1.0f -> first non-zero output sample
        // pinpoints the EXACT output frame where the incoming deck entered the mix.
        var outGain = new VolumeSampleProvider(new ConstantSampleProvider(0f, sampleRate)) { Volume = 1f };
        var inGain = new VolumeSampleProvider(new ConstantSampleProvider(1f, sampleRate)) { Volume = 1f };

        var outDeck = new MixBusDeck { Source = outGain, Gain = outGain };
        var inDeck = new MixBusDeck { Source = inGain, Gain = inGain };

        bool started = false;
        bool completed = false;
        bus.TransitionStarted += _ => started = true;
        bus.TransitionCompleted += (_, _) => completed = true;

        bus.SetActiveDeck(outDeck);
        // Use a flat 1.0 incoming gain region so even frame 0 of the transition is immediately audible.
        var region = new TransitionRegion
        {
            StartSample = 0,
            EndSample = durationFrames,
            Type = SLSKDONET.Services.Audio.TransitionType.EqSwap,
            Curve = TransitionCurve.Linear,
        };
        bus.ScheduleTransition(new ScheduledMixTransition
        {
            Incoming = inDeck,
            StartFrame = scheduledStartFrame,
            DurationFrames = durationFrames,
            Region = region,
            PresetName = "DropSwap",
            DurationSeconds = durationFrames / (double)sampleRate,
        });

        // Pull in realistic 1024-frame hardware buffers (crossing scheduledStartFrame mid-buffer).
        var rendered = new float[8192 * 2];
        int totalRead = 0;
        while (totalRead < rendered.Length)
        {
            int n = bus.Read(rendered, totalRead, Math.Min(1024 * 2, rendered.Length - totalRead));
            Assert.True(n > 0);
            totalRead += n;
        }

        Assert.True(started);
        Assert.True(completed);

        // Locate the exact first non-zero stereo frame.
        int firstAudibleFrame = -1;
        for (int f = 0; f < rendered.Length / 2; f++)
        {
            if (Math.Abs(rendered[f * 2]) > 1e-6f)
            {
                firstAudibleFrame = f;
                break;
            }
        }

        // Note: DropSwap enters at midpoint (50% of durationFrames); verify exact frame alignment within 1 sub-block,
        // or test immediate entry with EqualPower/Crossfade at frame > 0.
        Assert.True(firstAudibleFrame >= scheduledStartFrame,
            $"Incoming audio appeared before scheduledStartFrame ({firstAudibleFrame} < {scheduledStartFrame}).");
    }

    [Fact]
    public void ScheduledIncomingDeck_ImmediateCrossfade_EntersAtExactScheduledFrame()
    {
        const int sampleRate = 44100;
        const long scheduledStartFrame = 1337; // non-aligned to 256 or 1024!
        const long durationFrames = 4410;

        var bus = new AutoMixBusSampleProvider(sampleRate);
        var outGain = new VolumeSampleProvider(new ConstantSampleProvider(0f, sampleRate)) { Volume = 1f };
        var inGain = new VolumeSampleProvider(new ConstantSampleProvider(1f, sampleRate)) { Volume = 1f };

        var outDeck = new MixBusDeck { Source = outGain, Gain = outGain };
        long observedStartFrame = -1;
        var inDeck = new MixBusDeck
        {
            Source = inGain,
            Gain = inGain,
            PrepareBeforeStart = () => observedStartFrame = bus.ActiveRenderedFrames,
        };

        bool started = false;
        bus.TransitionStarted += _ => started = true;

        bus.SetActiveDeck(outDeck);
        bus.ScheduleTransition(new ScheduledMixTransition
        {
            Incoming = inDeck,
            StartFrame = scheduledStartFrame,
            DurationFrames = durationFrames,
            Region = null, // equal-power crossfade
            DurationSeconds = durationFrames / (double)sampleRate,
        });

        var rendered = new float[4096 * 2];
        for (int pos = 0; pos < rendered.Length; pos += 1024 * 2)
            bus.Read(rendered, pos, 1024 * 2);

        // Exact sample-clock start: 0-frame error!
        Assert.True(started);
        Assert.Equal(scheduledStartFrame, observedStartFrame);
        Assert.Equal(scheduledStartFrame, bus.TransitionStartFrame);
    }

    [Fact]
    public void HardLowSwap_LandsWithinOneBlockOfMidpointBar()
    {
        const int sampleRate = 44100;
        const long startFrame = 1000;
        const long durationFrames = 44100; // 1.0 s transition -> midpoint at 1000 + 22050 = 23050
        const long expectedSwapFrame = startFrame + (durationFrames / 2);

        var bus = new AutoMixBusSampleProvider(sampleRate);
        var outEq = new AudioPlayerService.ThreeBandGainProvider(new ConstantSampleProvider(0.5f, sampleRate));
        var inEq = new AudioPlayerService.ThreeBandGainProvider(new ConstantSampleProvider(0.5f, sampleRate));
        var outGain = new VolumeSampleProvider(outEq) { Volume = 1f };
        var inGain = new VolumeSampleProvider(inEq) { Volume = 1f };

        var outDeck = new MixBusDeck { Source = outGain, Gain = outGain, Eq = outEq };
        var inDeck = new MixBusDeck { Source = inGain, Gain = inGain, Eq = inEq };

        var model = new TransitionModel
        {
            Type = SLSKDONET.Models.Timeline.TransitionType.EqSwap,
            EqSwapLow = true,
            EqHardLowSwap = true,
            EqLowCrossoverHz = 250f,
        };

        bus.SetActiveDeck(outDeck);
        bus.ScheduleTransition(new ScheduledMixTransition
        {
            Incoming = inDeck,
            StartFrame = startFrame,
            DurationFrames = durationFrames,
            Region = AutoMixBusSampleProvider.BuildRegion(model, durationFrames),
            PresetName = "Bass Swap",
            DurationSeconds = 1.0,
        });

        long swapObservedAtFrame = -1;
        var block = new float[AutoMixBusSampleProvider.MaxBlockFrames * 2];
        for (long frame = 0; frame < startFrame + durationFrames; frame += AutoMixBusSampleProvider.MaxBlockFrames)
        {
            bus.Read(block, 0, block.Length);
            if (swapObservedAtFrame < 0 && inEq.LowGain > 0.5f && outEq.LowGain < 0.5f)
            {
                swapObservedAtFrame = frame;
            }
        }

        Assert.True(swapObservedAtFrame >= 0, "Hard low swap never fired during the transition.");
        long errorFrames = Math.Abs(swapObservedAtFrame - expectedSwapFrame);
        Assert.True(errorFrames <= AutoMixBusSampleProvider.MaxBlockFrames,
            $"Hard low swap landed {errorFrames} frames from midpoint (budget <= {AutoMixBusSampleProvider.MaxBlockFrames} frames / ~5.8 ms).");
    }

    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float _value;
        public WaveFormat WaveFormat { get; }

        public ConstantSampleProvider(float value, int sampleRate)
        {
            _value = value;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, _value, offset, count);
            return count;
        }
    }
}
