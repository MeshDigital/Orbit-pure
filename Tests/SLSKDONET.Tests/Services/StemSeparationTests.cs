using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NAudio.Wave;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.Tests.Services
{
    // ─────────────────────────────────────────────────────────────────────────────
    // NeuralMixEqSampleProvider tests — Task 6.5
    // ─────────────────────────────────────────────────────────────────────────────

    public class NeuralMixEqSampleProviderTests
    {
        private static ISampleProvider SilenceStereo44100()
            => new SilenceProvider44100();

        [Fact]
        public void DefaultGains_AreFlat_ZeroDb()
        {
            var eq = new NeuralMixEqSampleProvider(SilenceStereo44100());
            Assert.Equal(0f, eq.LowGainDb);
            Assert.Equal(0f, eq.MidGainDb);
            Assert.Equal(0f, eq.HighGainDb);
        }

        [Fact]
        public void SetLowGainDb_Clamped_AtMinusAndPlus12()
        {
            var eq = new NeuralMixEqSampleProvider(SilenceStereo44100());
            eq.LowGainDb = 99f;
            Assert.Equal(12f, eq.LowGainDb);
            eq.LowGainDb = -99f;
            Assert.Equal(-12f, eq.LowGainDb);
        }

        [Fact]
        public void SetMidGainDb_Clamped()
        {
            var eq = new NeuralMixEqSampleProvider(SilenceStereo44100());
            eq.MidGainDb = 20f;
            Assert.Equal(12f, eq.MidGainDb);
        }

        [Fact]
        public void SetHighGainDb_Clamped()
        {
            var eq = new NeuralMixEqSampleProvider(SilenceStereo44100());
            eq.HighGainDb = -50f;
            Assert.Equal(-12f, eq.HighGainDb);
        }

        [Fact]
        public void WaveFormat_MatchesSource()
        {
            var src = SilenceStereo44100();
            var eq  = new NeuralMixEqSampleProvider(src);
            Assert.Equal(44100, eq.WaveFormat.SampleRate);
            Assert.Equal(2,     eq.WaveFormat.Channels);
        }

        [Fact]
        public void FlatEq_PassesSilenceUnchanged()
        {
            // All gains at 0 dB → buffer should remain all-zero (silence through)
            var eq = new NeuralMixEqSampleProvider(SilenceStereo44100());
            var buf = new float[256];
            int read = eq.Read(buf, 0, buf.Length);
            Assert.Equal(buf.Length, read);
            Assert.All(buf, s => Assert.Equal(0f, s));
        }

        [Fact]
        public void Read_ReturnsSameCountAsSource()
        {
            var eq = new NeuralMixEqSampleProvider(SilenceStereo44100());
            eq.LowGainDb = 6f;   // non-flat so filter path is active
            var buf = new float[128];
            Assert.Equal(buf.Length, eq.Read(buf, 0, buf.Length));
        }

        // ── Minimal silence provider ──────────────────────────────────────────

        private sealed class SilenceProvider44100 : ISampleProvider
        {
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            public int Read(float[] buffer, int offset, int count)
            { Array.Clear(buffer, offset, count); return count; }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // StemChannelViewModel tests — Task 6.3
    // ─────────────────────────────────────────────────────────────────────────────

    public class StemChannelViewModelTests
    {
        private static StemMixerService CreateMixer()
            => new StemMixerService(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));

        [Fact]
        public void Defaults_GainZero_PanZero_NotMuted_NotSoloed()
        {
            var mixer = CreateMixer();
            var ch    = new SLSKDONET.ViewModels.StemChannelViewModel(StemType.Vocals, mixer);

            Assert.Equal(0f,   ch.GainDb);
            Assert.Equal(0f,   ch.Pan);
            Assert.False(ch.IsMuted);
            Assert.False(ch.IsSoloed);
        }

        [Fact]
        public void GainDb_Clamped_To_Minus60_Plus12()
        {
            var ch = new SLSKDONET.ViewModels.StemChannelViewModel(
                StemType.Drums, CreateMixer());

            ch.GainDb = 99f;
            Assert.Equal(12f, ch.GainDb);

            ch.GainDb = -99f;
            Assert.Equal(-60f, ch.GainDb);
        }

        [Fact]
        public void Pan_Clamped_To_MinusOne_PlusOne()
        {
            var ch = new SLSKDONET.ViewModels.StemChannelViewModel(
                StemType.Bass, CreateMixer());

            ch.Pan = 5f;
            Assert.Equal(1f, ch.Pan);

            ch.Pan = -5f;
            Assert.Equal(-1f, ch.Pan);
        }

        [Fact]
        public void MuteCommand_TogglesIsMuted()
        {
            var ch = new SLSKDONET.ViewModels.StemChannelViewModel(
                StemType.Other, CreateMixer());

            ch.MuteCommand.Execute(System.Reactive.Unit.Default).Subscribe();
            Assert.True(ch.IsMuted);

            ch.MuteCommand.Execute(System.Reactive.Unit.Default).Subscribe();
            Assert.False(ch.IsMuted);
        }

        [Fact]
        public void SoloCommand_TogglesIsSoloed()
        {
            var ch = new SLSKDONET.ViewModels.StemChannelViewModel(
                StemType.Vocals, CreateMixer());

            ch.SoloCommand.Execute(System.Reactive.Unit.Default).Subscribe();
            Assert.True(ch.IsSoloed);
        }

        [Fact]
        public void ResetCommand_RestoresDefaults()
        {
            var ch = new SLSKDONET.ViewModels.StemChannelViewModel(
                StemType.Drums, CreateMixer());

            ch.GainDb = 9f; ch.Pan = 0.5f; ch.IsMuted = true;
            ch.ResetCommand.Execute(System.Reactive.Unit.Default).Subscribe();

            Assert.Equal(0f,   ch.GainDb);
            Assert.Equal(0f,   ch.Pan);
            Assert.False(ch.IsMuted);
        }

        [Theory]
        [InlineData(StemType.Vocals, "VOCALS")]
        [InlineData(StemType.Drums,  "DRUMS")]
        [InlineData(StemType.Bass,   "BASS")]
        [InlineData(StemType.Other,  "OTHER")]
        public void DisplayName_CorrectPerStemType(StemType st, string name)
        {
            // Uppercase — distinct from NeuralMixEqViewModel's title-case DisplayName below;
            // this is the older stem-mixer-rack channel strip, a separate UI surface.
            var ch = new SLSKDONET.ViewModels.StemChannelViewModel(st, CreateMixer());
            Assert.Equal(name, ch.DisplayName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // StemMixerService pan tests — Task 6.3
    // ─────────────────────────────────────────────────────────────────────────────

    public class StemMixerServicePanTests
    {
        [Fact]
        public void GetPan_DefaultsToZero()
        {
            var svc = new StemMixerService(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
            // GetPan on non-existent stem returns 0f
            Assert.Equal(0f, svc.GetPan(StemType.Vocals));
        }

        [Fact]
        public void SetPan_Stored_And_Retrieved()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var svc = new StemMixerService(fmt);

            // Add a stem so SetPan has something to target
            var silence = new SilenceStemProvider(fmt);
            svc.AddStem(StemType.Vocals, silence);

            svc.SetPan(StemType.Vocals, 0.75f);
            Assert.Equal(0.75f, svc.GetPan(StemType.Vocals));
        }

        [Fact]
        public void SetPan_Clamped_To_PlusOne()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var svc = new StemMixerService(fmt);
            svc.AddStem(StemType.Bass, new SilenceStemProvider(fmt));

            svc.SetPan(StemType.Bass, 99f);
            Assert.Equal(1f, svc.GetPan(StemType.Bass));
        }

        [Fact]
        public void SetPan_Clamped_To_MinusOne()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var svc = new StemMixerService(fmt);
            svc.AddStem(StemType.Drums, new SilenceStemProvider(fmt));

            svc.SetPan(StemType.Drums, -99f);
            Assert.Equal(-1f, svc.GetPan(StemType.Drums));
        }

        private sealed class SilenceStemProvider : ISampleProvider
        {
            public WaveFormat WaveFormat { get; }
            public SilenceStemProvider(WaveFormat fmt) => WaveFormat = fmt;
            public int Read(float[] buffer, int offset, int count)
            { Array.Clear(buffer, offset, count); return count; }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // NeuralMixEqViewModel tests — Task 6.5
    // ─────────────────────────────────────────────────────────────────────────────

    public class NeuralMixEqViewModelTests
    {
        [Fact]
        public void AllFour_EqBands_Created()
        {
            var vm = new SLSKDONET.ViewModels.NeuralMixEqViewModel();
            Assert.NotNull(vm.VocalsEq);
            Assert.NotNull(vm.DrumsEq);
            Assert.NotNull(vm.BassEq);
            Assert.NotNull(vm.OtherEq);
        }

        [Fact]
        public void AllBands_HasFour_Items()
        {
            var vm = new SLSKDONET.ViewModels.NeuralMixEqViewModel();
            Assert.Equal(4, vm.AllBands.Count);
        }

        [Fact]
        public void GetProvider_ReturnsProvider_ForEachStemType()
        {
            var vm = new SLSKDONET.ViewModels.NeuralMixEqViewModel();
            Assert.NotNull(vm.GetProvider(StemType.Vocals));
            Assert.NotNull(vm.GetProvider(StemType.Drums));
            Assert.NotNull(vm.GetProvider(StemType.Bass));
            Assert.NotNull(vm.GetProvider(StemType.Other));
        }

        [Fact]
        public void ResetAll_SetsAllGainsToZero()
        {
            var vm = new SLSKDONET.ViewModels.NeuralMixEqViewModel();
            vm.VocalsEq.LowGainDb  = 6f;
            vm.DrumsEq.MidGainDb   = -3f;
            vm.BassEq.HighGainDb   = 9f;
            vm.OtherEq.LowGainDb   = -6f;

            vm.ResetAll();

            Assert.Equal(0f, vm.VocalsEq.LowGainDb);
            Assert.Equal(0f, vm.DrumsEq.MidGainDb);
            Assert.Equal(0f, vm.BassEq.HighGainDb);
            Assert.Equal(0f, vm.OtherEq.LowGainDb);
        }

        [Fact]
        public void StemEqViewModel_GainDb_WriteThrough_ToProvider()
        {
            var vm       = new SLSKDONET.ViewModels.NeuralMixEqViewModel();
            var provider = vm.GetProvider(StemType.Vocals);

            vm.VocalsEq.LowGainDb = 6f;
            Assert.Equal(6f, provider.LowGainDb);
        }

        [Fact]
        public void StemEqViewModel_Clamped_At_PlusMinus12()
        {
            var vm = new SLSKDONET.ViewModels.NeuralMixEqViewModel();
            vm.DrumsEq.MidGainDb = 50f;
            Assert.Equal(12f, vm.DrumsEq.MidGainDb);
        }

        [Theory]
        [InlineData(StemType.Vocals, "Vocals")]
        [InlineData(StemType.Drums,  "Drums")]
        [InlineData(StemType.Bass,   "Bass")]
        [InlineData(StemType.Other,  "Other")]
        public void DisplayName_CorrectPerStemType(StemType st, string expected)
        {
            var vm = new SLSKDONET.ViewModels.NeuralMixEqViewModel();
            var band = vm.AllBands.First(b => b.StemType == st);
            Assert.Equal(expected, band.DisplayName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // DemucsOnnxSeparator chunked-inference helpers — pure logic, no ONNX model needed
    // ─────────────────────────────────────────────────────────────────────────────

    public class DemucsOnnxSeparatorChunkingTests
    {
        [Fact]
        public void ResolveSegmentSamplesCore_UsesDeclaredFixedDimension_WhenGraphIsNotDynamic()
        {
            // e.g. a model exported with a hard-coded input shape [1, 2, 343980]
            var declared = new[] { 1, 2, 343980 };

            var result = DemucsOnnxSeparator.ResolveSegmentSamplesCore(declared, sampleRate: 44100, totalFrames: 10_000_000);

            Assert.Equal(343980, result);
        }

        [Fact]
        public void ResolveSegmentSamplesCore_FallsBackToDefaultSegment_WhenDimensionIsDynamic()
        {
            // -1 (or any non-positive value) marks a dynamic axis in ONNX metadata
            var declared = new[] { 1, 2, -1 };
            const int sampleRate = 44100;

            var result = DemucsOnnxSeparatorChunkingTests_LongTrack(declared, sampleRate);

            Assert.Equal((int)(10.0 * sampleRate), result);
        }

        private static int DemucsOnnxSeparatorChunkingTests_LongTrack(int[] declared, int sampleRate)
            => DemucsOnnxSeparator.ResolveSegmentSamplesCore(declared, sampleRate, totalFrames: sampleRate * 300);

        [Fact]
        public void ResolveSegmentSamplesCore_CapsToTrackLength_ForShortTracksOnDynamicGraph()
        {
            const int sampleRate = 44100;
            int shortTrackFrames = sampleRate * 3; // 3-second track, shorter than the 10s default segment

            var result = DemucsOnnxSeparator.ResolveSegmentSamplesCore(
                declaredDimensions: null, sampleRate, totalFrames: shortTrackFrames);

            Assert.Equal(shortTrackFrames, result);
        }

        [Fact]
        public void ResolveSegmentSamplesCore_NeverReturnsZeroOrNegative_ForDegenerateInput()
        {
            var result = DemucsOnnxSeparator.ResolveSegmentSamplesCore(
                declaredDimensions: null, sampleRate: 44100, totalFrames: 0);

            Assert.True(result > 0);
        }

        [Fact]
        public void BuildOverlapWindow_PeaksAtOne_InTheMiddle()
        {
            var window = DemucsOnnxSeparator.BuildOverlapWindow(101);

            Assert.Equal(1f, window[50], precision: 3);
        }

        [Fact]
        public void BuildOverlapWindow_NeverReachesExactZero_AtTheEdges()
        {
            // Guards against divide-by-zero during overlap-add normalisation at the very
            // start/end of a track, where only one chunk contributes.
            var window = DemucsOnnxSeparator.BuildOverlapWindow(64);

            Assert.True(window[0] > 0f);
            Assert.True(window[^1] > 0f);
        }

        [Fact]
        public void BuildOverlapWindow_IsSymmetric()
        {
            var window = DemucsOnnxSeparator.BuildOverlapWindow(50);

            for (int i = 0; i < window.Length / 2; i++)
            {
                Assert.Equal(window[i], window[^(i + 1)], precision: 5);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void BuildOverlapWindow_HandlesDegenerateLengths_WithoutThrowing(int length)
        {
            var window = DemucsOnnxSeparator.BuildOverlapWindow(length);

            Assert.Equal(length, window.Length);
        }

        [Fact]
        public void WeightedOverlapAdd_ReconstructsFlatValue_OutsideTheOverlapRegion()
        {
            // Simulates the actual reconstruction the real inference loop performs (weighted-sum
            // then divide-by-accumulated-weight), using two overlapping synthetic "model outputs"
            // (constant 2.0 for chunk A, constant 3.0 for chunk B) instead of real ONNX results.
            // Outside the overlap, each sample has exactly one contributor, so normalisation must
            // return that contributor's value exactly — this is what keeps the un-overlapped
            // majority of every track byte-identical to a single-chunk pass.
            const int segment = 1000;
            const int stride = 750; // segment - overlap(250), matching OverlapRatio
            const int totalFrames = stride + segment; // two chunks: [0,1000) and [750,1750)
            var window = DemucsOnnxSeparator.BuildOverlapWindow(segment);

            var accum = new float[totalFrames];
            var weight = new float[totalFrames];

            AddChunk(accum, weight, window, chunkStart: 0, value: 2.0f);
            AddChunk(accum, weight, window, chunkStart: stride, value: 3.0f);

            for (int i = 0; i < totalFrames; i++)
            {
                accum[i] /= weight[i];
            }

            // Well before the overlap (chunk A only) → exactly chunk A's value.
            Assert.Equal(2.0f, accum[100], precision: 4);
            // Well after the overlap (chunk B only) → exactly chunk B's value.
            Assert.Equal(3.0f, accum[1600], precision: 4);
            // Inside the overlap, the blend must move monotonically from A's value toward B's.
            Assert.InRange(accum[750], 2.0f, 3.0f);
            Assert.InRange(accum[900], 2.0f, 3.0f);
            Assert.True(accum[900] > accum[750], "Blend should move further toward chunk B's value as the overlap progresses.");
        }

        private static void AddChunk(float[] accum, float[] weight, float[] window, int chunkStart, float value)
        {
            for (int i = 0; i < window.Length; i++)
            {
                accum[chunkStart + i]  += value * window[i];
                weight[chunkStart + i] += window[i];
            }
        }
    }
}
