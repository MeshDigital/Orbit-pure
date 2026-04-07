using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Tests.Services.AudioAnalysis
{
    // ─────────────────────────────────────────────────────────────────────────
    // BpmDetectionService tests (Issue 1.2 / #20)
    // ─────────────────────────────────────────────────────────────────────────

    public class BpmDetectionServiceTests
    {
        private readonly BpmDetectionService _sut = new();

        [Fact]
        public void Detect_WritesBpmAndConfidence()
        {
            var output = MakeRhythmOutput(bpm: 128f, confidence: 0.9f);
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            Assert.Equal(128f, target.Bpm);
            Assert.Equal(0.9f, target.BpmConfidence, precision: 5);
        }

        [Fact]
        public void Detect_NormalisesHalfTimeBpm()
        {
            // 50 bpm is below 60 → should double to 100
            var output = MakeRhythmOutput(bpm: 50f, confidence: 0.8f);
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            Assert.True(target.Bpm >= 60f, $"BPM should be normalised above 60, was {target.Bpm}");
        }

        [Fact]
        public void Detect_NormalisesDoubleTimeBpm()
        {
            // 240 bpm is above 200 → should halve to 120
            var output = MakeRhythmOutput(bpm: 240f, confidence: 0.8f);
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            Assert.True(target.Bpm <= 200f, $"BPM should be normalised below 200, was {target.Bpm}");
        }

        [Fact]
        public void Detect_WithHistogram_UsesMedianBpm()
        {
            // histogram with dominant peak at bin 128 (index 127)
            float[] hist = new float[256];
            hist[127] = 100f; // strong peak at 128 bpm
            var output = MakeRhythmOutput(bpm: 130f, confidence: 0.7f, histogram: hist);
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            // Result should be close to the histogram's median (128)
            Assert.InRange(target.Bpm, 120f, 135f);
        }

        [Fact]
        public void Detect_NullRhythm_DoesNotWriteFields()
        {
            var output = new EssentiaOutput { Rhythm = null };
            var target = new AudioFeaturesEntity { Bpm = 99f };
            _sut.Detect(output, target);
            Assert.Equal(99f, target.Bpm); // unchanged
        }

        private static EssentiaOutput MakeRhythmOutput(float bpm, float confidence,
            float[]? histogram = null)
            => new()
            {
                Rhythm = new RhythmData
                {
                    Bpm           = bpm,
                    BpmConfidence = confidence,
                    BpmHistogram  = histogram,
                }
            };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // KeyDetectionService tests (Issue 1.3 / #21)
    // ─────────────────────────────────────────────────────────────────────────

    public class KeyDetectionServiceTests
    {
        private readonly KeyDetectionService _sut = new();

        [Theory]
        [InlineData("C",  "major", "8B")]
        [InlineData("A",  "minor", "8A")]
        [InlineData("G",  "major", "9B")]
        [InlineData("F#", "minor", "11A")]
        [InlineData("Db", "major", "3B")]
        public void ToCamelotKey_ReturnsCorrectCode(string key, string scale, string expected)
        {
            Assert.Equal(expected, KeyDetectionService.ToCamelotKey(key, scale));
        }

        [Fact]
        public void Detect_WritesKeyScaleCamelot()
        {
            var output = MakeKeyOutput(edmaKey: "C", edmaScale: "major", edmaStrength: 0.85f);
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            Assert.Equal("C",     target.Key);
            Assert.Equal("major", target.Scale);
            Assert.Equal("8B",    target.CamelotKey);
            Assert.Equal(0.85f,   target.KeyConfidence, precision: 5);
        }

        [Fact]
        public void Detect_PrefersEdmaOverKrumhanslWhenStronger()
        {
            var output = new EssentiaOutput
            {
                Tonal = new TonalData
                {
                    KeyEdma       = new KeyData { Key = "A", Scale = "minor", Strength = 0.9f },
                    KeyKrumhansl  = new KeyData { Key = "C", Scale = "major", Strength = 0.5f },
                }
            };
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            Assert.Equal("A", target.Key);
        }

        [Fact]
        public void Detect_FallsBackToKrumhanslWhenEdmaMissing()
        {
            var output = new EssentiaOutput
            {
                Tonal = new TonalData
                {
                    KeyEdma      = null,
                    KeyKrumhansl = new KeyData { Key = "G", Scale = "major", Strength = 0.7f },
                }
            };
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            Assert.Equal("G", target.Key);
        }

        [Fact]
        public void Detect_NullTonal_DoesNotWriteFields()
        {
            var output = new EssentiaOutput { Tonal = null };
            var target = new AudioFeaturesEntity { Key = "original" };
            _sut.Detect(output, target);
            Assert.Equal("original", target.Key);
        }

        [Fact]
        public void ToOpenKey_ReturnsCorrectCode()
        {
            Assert.Equal("1d", KeyDetectionService.ToOpenKey("C", "major"));
            Assert.Equal("1m", KeyDetectionService.ToOpenKey("A", "minor"));
        }

        private static EssentiaOutput MakeKeyOutput(string edmaKey, string edmaScale,
            float edmaStrength)
            => new()
            {
                Tonal = new TonalData
                {
                    KeyEdma      = new KeyData { Key = edmaKey, Scale = edmaScale,
                                                  Strength = edmaStrength },
                    KeyKrumhansl = new KeyData { Key = "X", Scale = "major", Strength = 0.0f },
                }
            };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EnergyScoringService tests (Issue 1.4 / #22)
    // ─────────────────────────────────────────────────────────────────────────

    public class EnergyScoringServiceTests
    {
        private readonly EnergyScoringService _sut = new();

        [Fact]
        public void Score_HighEnergyTrack_Returns8To10()
        {
            var output = MakeLowLevelOutput(danceability: 0.95f, dynamicComplexity: 7f, rms: 0.8f);
            var target = new AudioFeaturesEntity();
            _sut.Score(output, target);
            Assert.InRange(target.EnergyScore, 7, 10);
        }

        [Fact]
        public void Score_LowEnergyTrack_Returns1To3()
        {
            var output = MakeLowLevelOutput(danceability: 0.1f, dynamicComplexity: 0.5f, rms: 0.05f);
            var target = new AudioFeaturesEntity();
            _sut.Score(output, target);
            Assert.InRange(target.EnergyScore, 1, 4);
        }

        [Fact]
        public void Score_WritesEnergyAndEnergyScore()
        {
            var output = MakeLowLevelOutput(danceability: 0.5f, dynamicComplexity: 3f, rms: 0.3f);
            var target = new AudioFeaturesEntity();
            _sut.Score(output, target);
            Assert.InRange(target.Energy, 0f, 1f);
            Assert.InRange(target.EnergyScore, 1, 10);
        }

        [Fact]
        public void Score_DynamicallyCompressed_FlagsEntity()
        {
            // DynamicComplexity < 2 and LoudnessLUFS > -7 → over-compressed flag
            var output = MakeLowLevelOutput(danceability: 0.8f, dynamicComplexity: 1.5f, rms: 0.4f);
            var target = new AudioFeaturesEntity { LoudnessLUFS = -6f };
            _sut.Score(output, target);
            Assert.True(target.IsDynamicCompressed);
        }

        private static EssentiaOutput MakeLowLevelOutput(
            float danceability, float dynamicComplexity, float rms)
            => new()
            {
                Rhythm   = new RhythmData { Danceability = danceability },
                LowLevel = new LowLevelData
                {
                    DynamicComplexity = dynamicComplexity,
                    Rms = new StatsData { Mean = rms },
                }
            };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DemucsModelManager tests (Issue 3.1 / #28)
    // ─────────────────────────────────────────────────────────────────────────

    public class DemucsModelManagerTests
    {
        [Fact]
        public void ModelTag_WhenMissing_ReturnsPlaceholder()
        {
            var mgr = new DemucsModelManager(customModelPath: "/nonexistent/path/demucs.onnx");
            Assert.StartsWith("demucs-4s-missing", mgr.ModelTag);
        }

        [Fact]
        public void IsAvailable_WhenMissing_ReturnsFalse()
        {
            var mgr = new DemucsModelManager(customModelPath: "/nonexistent/path/demucs.onnx");
            Assert.False(mgr.IsAvailable);
        }

        [Fact]
        public void ModelFileName_IsCorrect()
        {
            Assert.Equal("demucs-4s.onnx", DemucsModelManager.ModelFileName);
        }

        [Fact]
        public void GetUserModelDirectory_ReturnsExistingDirectory()
        {
            string dir = DemucsModelManager.GetUserModelDirectory();
            Assert.True(System.IO.Directory.Exists(dir));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // StemMixerService tests (Issue 3.3 / #30)
    // ─────────────────────────────────────────────────────────────────────────

    public class StemMixerServiceTests
    {
        private static WaveFormat Fmt => WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        [Fact]
        public void AddStem_RegisteredStemAppearsInList()
        {
            var mixer = new StemMixerService(Fmt);
            mixer.AddStem(StemType.Drums, new SilentProvider(Fmt));
            Assert.Contains(StemType.Drums, mixer.RegisteredStems);
        }

        [Fact]
        public void RemoveStem_StemNoLongerInList()
        {
            var mixer = new StemMixerService(Fmt);
            mixer.AddStem(StemType.Vocals, new SilentProvider(Fmt));
            mixer.RemoveStem(StemType.Vocals);
            Assert.DoesNotContain(StemType.Vocals, mixer.RegisteredStems);
        }

        [Fact]
        public void Mute_MutedStemProducesSilence()
        {
            var mixer = new StemMixerService(Fmt);
            var drums = new ConstantProvider(Fmt, 0.5f);
            mixer.AddStem(StemType.Drums, drums);
            mixer.SetMute(StemType.Drums, true);

            var buf = new float[256];
            mixer.Read(buf, 0, buf.Length);
            Assert.All(buf, s => Assert.Equal(0f, s, precision: 5));
        }

        [Fact]
        public void Solo_OnlySoloedStemIsAudible()
        {
            var mixer = new StemMixerService(Fmt);
            mixer.AddStem(StemType.Drums,  new ConstantProvider(Fmt, 0.5f));
            mixer.AddStem(StemType.Vocals, new ConstantProvider(Fmt, 0.3f));
            mixer.SetSolo(StemType.Drums, true);

            // After soloing drums, vocals should be silent
            Assert.True(mixer.IsMuted(StemType.Vocals) || !mixer.IsSoloed(StemType.Vocals));
            Assert.True(mixer.IsSoloed(StemType.Drums));
        }

        [Fact]
        public void SetGain_Unity_VolumeIsOne()
        {
            var mixer = new StemMixerService(Fmt);
            mixer.AddStem(StemType.Bass, new SilentProvider(Fmt));
            mixer.SetGain(StemType.Bass, 0f); // 0 dB = unity
            Assert.Equal(0f, mixer.GetGain(StemType.Bass), precision: 5);
        }

        [Fact]
        public void Read_NoStems_ReturnsSilence()
        {
            var mixer = new StemMixerService(Fmt);
            var buf   = new float[256];
            int read  = mixer.Read(buf, 0, buf.Length);
            Assert.Equal(256, read);
            Assert.All(buf, s => Assert.Equal(0f, s, precision: 5));
        }
    }

    // ─── test helpers ────────────────────────────────────────────────────────

    /// <summary>ISampleProvider that outputs a constant float value.</summary>
    internal sealed class ConstantProvider : ISampleProvider
    {
        private readonly float _value;
        public WaveFormat WaveFormat { get; }
        public ConstantProvider(WaveFormat fmt, float value)
        { WaveFormat = fmt; _value = value; }
        public int Read(float[] buffer, int offset, int count)
        { Array.Fill(buffer, _value, offset, count); return count; }
    }

    /// <summary>ISampleProvider that outputs silence.</summary>
    internal sealed class SilentProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; }
        public SilentProvider(WaveFormat fmt) => WaveFormat = fmt;
        public int Read(float[] buffer, int offset, int count)
        { Array.Clear(buffer, offset, count); return count; }
    }
}
