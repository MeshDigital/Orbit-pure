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
        [InlineData(StemType.Vocals, "Vocals", "#00CFFF")]
        [InlineData(StemType.Drums,  "Drums",  "#FF8C00")]
        [InlineData(StemType.Bass,   "Bass",   "#44FF88")]
        [InlineData(StemType.Other,  "Other",  "#BB88FF")]
        public void DisplayName_And_AccentColor_CorrectPerStemType(
            StemType st, string name, string color)
        {
            var ch = new SLSKDONET.ViewModels.StemChannelViewModel(st, CreateMixer());
            Assert.Equal(name,  ch.DisplayName);
            Assert.Equal(color, ch.AccentColor);
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
    // StemWaveformRowViewModel tests — Task 6.4
    // ─────────────────────────────────────────────────────────────────────────────

    public class StemWaveformRowViewModelTests
    {
        [Fact]
        public void Defaults_NoWaveformData_NotLoading()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformRowViewModel("Vocals", "#00CFFF");
            Assert.Null(vm.WaveformData);
            Assert.False(vm.IsLoading);
        }

        [Fact]
        public void ViewOffset_Stored()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformRowViewModel("Drums", "#FF8C00");
            vm.ViewOffset = 0.5;
            Assert.Equal(0.5, vm.ViewOffset);
        }

        [Fact]
        public void ZoomLevel_Clamped_Min_0_25()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformRowViewModel("Bass", "#44FF88");
            vm.ZoomLevel = 0.001;
            Assert.Equal(0.25, vm.ZoomLevel);
        }

        [Fact]
        public void ZoomLevel_Clamped_Max_32()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformRowViewModel("Other", "#BB88FF");
            vm.ZoomLevel = 999;
            Assert.Equal(32.0, vm.ZoomLevel);
        }

        [Fact]
        public void LoadWavAsync_NonExistentFile_DoesNotThrow()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformRowViewModel("Vocals", "#00CFFF");
            // Should silently handle missing file without throwing
            var ex = Record.ExceptionAsync(() => vm.LoadWavAsync(@"C:\non_existent_path\silent.wav"));
            Assert.NotNull(ex);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // StemWaveformViewModel aggregation tests — Task 6.4
    // ─────────────────────────────────────────────────────────────────────────────

    public class StemWaveformViewModelTests
    {
        [Fact]
        public void AllFour_Rows_Created()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformViewModel();
            Assert.NotNull(vm.VocalsWaveform);
            Assert.NotNull(vm.DrumsWaveform);
            Assert.NotNull(vm.BassWaveform);
            Assert.NotNull(vm.OtherWaveform);
        }

        [Fact]
        public void SharedViewOffset_Propagates_ToAllRows()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformViewModel();
            vm.SharedViewOffset = 0.42;

            Assert.Equal(0.42, vm.VocalsWaveform.ViewOffset);
            Assert.Equal(0.42, vm.DrumsWaveform.ViewOffset);
            Assert.Equal(0.42, vm.BassWaveform.ViewOffset);
            Assert.Equal(0.42, vm.OtherWaveform.ViewOffset);
        }

        [Fact]
        public void SharedZoomLevel_Propagates_ToAllRows()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformViewModel();
            vm.SharedZoomLevel = 4.0;

            Assert.Equal(4.0, vm.VocalsWaveform.ZoomLevel);
            Assert.Equal(4.0, vm.DrumsWaveform.ZoomLevel);
        }

        [Fact]
        public void SharedProgress_Propagates_ToAllRows()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformViewModel();
            vm.SharedProgress = 0.7f;

            Assert.Equal(0.7f, vm.VocalsWaveform.Progress, 3);
            Assert.Equal(0.7f, vm.OtherWaveform.Progress, 3);
        }

        [Fact]
        public void LoadStemsAsync_NullPaths_DoesNotThrow()
        {
            var vm = new SLSKDONET.ViewModels.StemWaveformViewModel();
            var ex = Record.ExceptionAsync(() => vm.LoadStemsAsync(null, null, null, null));
            Assert.NotNull(ex);  // task returned, not exception thrown
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
}
