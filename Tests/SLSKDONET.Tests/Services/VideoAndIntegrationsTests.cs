using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SLSKDONET.Services.Integrations;
using SLSKDONET.Services.Video;

namespace SLSKDONET.Tests.Services
{
    // ─────────────────────────────────────────────────────────────────────────
    // VisualEngine tests (Issue 5.1 / #35)
    // ─────────────────────────────────────────────────────────────────────────

    public class VisualEngineTests
    {
        private static VisualFrame MakeFrame(
            float energy    = 0.5f,
            float beatPulse = 0f,
            float progress  = 0f,
            float bpm       = 120f,
            float[]? bands  = null)
            => new()
            {
                Energy         = energy,
                BeatPulse      = beatPulse,
                Progress       = progress,
                Bpm            = bpm,
                FrequencyBands = bands ?? new float[] { 0.3f, 0.5f, 0.4f, 0.6f, 0.2f, 0.1f },
            };

        // ── ComputeState ──────────────────────────────────────────────────

        [Fact]
        public void ComputeState_ScaleIncreasesWithEnergyAndPulse()
        {
            var engine = new VisualEngine();
            var low  = engine.ComputeState(MakeFrame(energy: 0f, beatPulse: 0f));
            var high = engine.ComputeState(MakeFrame(energy: 1f, beatPulse: 1f));
            Assert.True(high.Scale > low.Scale, "Scale should increase with energy and pulse");
        }

        [Fact]
        public void ComputeState_ScaleIsClampedTo0And2()
        {
            var engine = new VisualEngine();
            var state  = engine.ComputeState(MakeFrame(energy: 2f, beatPulse: 2f));
            Assert.InRange(state.Scale, 0f, 2f);
        }

        [Fact]
        public void ComputeState_MotionSpeedProportionalToBpm()
        {
            var engine = new VisualEngine();
            var slow = engine.ComputeState(MakeFrame(bpm: 60f));
            var fast = engine.ComputeState(MakeFrame(bpm: 240f));
            Assert.True(fast.MotionSpeed > slow.MotionSpeed,
                "MotionSpeed should scale with BPM");
        }

        [Fact]
        public void ComputeState_HueRotatesWithProgress()
        {
            var engine   = new VisualEngine { BaseHue = 200f };
            var start    = engine.ComputeState(MakeFrame(progress: 0f));
            var middle   = engine.ComputeState(MakeFrame(progress: 0.5f));
            Assert.NotEqual(start.Hue, middle.Hue);
        }

        [Fact]
        public void ComputeState_SaturationDrivenByEnergy()
        {
            var engine   = new VisualEngine();
            var low  = engine.ComputeState(MakeFrame(energy: 0f));
            var high = engine.ComputeState(MakeFrame(energy: 1f));
            Assert.True(high.Saturation > low.Saturation);
        }

        // ── RenderFrame ───────────────────────────────────────────────────

        [Theory]
        [InlineData(VisualPreset.Bars)]
        [InlineData(VisualPreset.Circles)]
        [InlineData(VisualPreset.Waveform)]
        [InlineData(VisualPreset.Particles)]
        public void RenderFrame_AllPresets_ReturnExpectedDimensions(VisualPreset preset)
        {
            var engine = new VisualEngine { Preset = preset, Width = 320, Height = 180 };
            using var bmp = engine.RenderFrame(MakeFrame());
            Assert.Equal(320, bmp.Width);
            Assert.Equal(180, bmp.Height);
        }

        [Fact]
        public void RenderFrame_NullFrame_Throws()
        {
            var engine = new VisualEngine();
            Assert.Throws<ArgumentNullException>(() => engine.RenderFrame(null!));
        }

        [Fact]
        public void RenderFrame_WithNoFrequencyBands_DoesNotThrow()
        {
            var engine = new VisualEngine { Preset = VisualPreset.Bars };
            var frame  = MakeFrame(bands: Array.Empty<float>());
            using var bmp = engine.RenderFrame(frame); // should not throw
            Assert.Equal(engine.Width,  bmp.Width);
            Assert.Equal(engine.Height, bmp.Height);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VideoRenderer command-line builder tests (Issue 5.2 / #36)
    // (RenderAsync requires a live FFmpeg process; only CLI helpers are unit-tested)
    // ─────────────────────────────────────────────────────────────────────────

    public class VideoRendererTests
    {
        [Fact]
        public void BuildFfmpegArgs_ContainsVideoSizeAndFrameRate()
        {
            var opts = new VideoRenderOptions
            {
                Width      = 1920,
                Height     = 1080,
                FrameRate  = 30,
                OutputPath = "out.mp4",
            };

            string args = VideoRenderer.BuildFfmpegArgs(opts);

            Assert.Contains("1920x1080", args);
            Assert.Contains("30",       args);
            Assert.Contains("rawvideo", args);
        }

        [Fact]
        public void BuildFfmpegArgs_WithAudioPath_IncludesAudioInput()
        {
            var opts = new VideoRenderOptions
            {
                Width      = 1280,
                Height     = 720,
                FrameRate  = 25,
                OutputPath = "out.mp4",
                AudioPath  = "mix.wav",
            };

            string args = VideoRenderer.BuildFfmpegArgs(opts);
            Assert.Contains("mix.wav", args);
            Assert.Contains("aac",     args);
        }

        [Fact]
        public void BuildFfmpegArgs_WithoutAudioPath_DisablesAudio()
        {
            var opts = new VideoRenderOptions
            {
                Width      = 640,
                Height     = 360,
                FrameRate  = 24,
                OutputPath = "out.mp4",
                AudioPath  = string.Empty,
            };

            string args = VideoRenderer.BuildFfmpegArgs(opts);
            Assert.Contains("-an", args);
        }

        [Fact]
        public void BuildFfmpegArgs_ContainsOutputPath()
        {
            var opts = new VideoRenderOptions
            {
                Width      = 1920,
                Height     = 1080,
                FrameRate  = 30,
                OutputPath = @"C:\exports\myset.mp4",
            };

            string args = VideoRenderer.BuildFfmpegArgs(opts);
            Assert.Contains("myset.mp4", args);
        }

        [Fact]
        public void VideoRenderOptions_Validate_ThrowsOnZeroWidth()
        {
            var opts = new VideoRenderOptions { Width = 0, Height = 1080, FrameRate = 30, OutputPath = "x.mp4" };
            Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
        }

        [Fact]
        public void VideoRenderOptions_Validate_ThrowsOnEmptyOutputPath()
        {
            var opts = new VideoRenderOptions { Width = 1920, Height = 1080, FrameRate = 30, OutputPath = "" };
            Assert.Throws<ArgumentException>(() => opts.Validate());
        }

        [Fact]
        public void VideoRenderOptions_Validate_ThrowsOnEmptyVideoCodec()
        {
            var opts = new VideoRenderOptions
            {
                Width = 1920, Height = 1080, FrameRate = 30, OutputPath = "x.mp4",
                VideoCodec = ""
            };
            Assert.Throws<ArgumentException>(() => opts.Validate());
        }

        [Fact]
        public void RenderProgressEventArgs_Percentage_CalculatedCorrectly()
        {
            var args = new RenderProgressEventArgs { FrameIndex = 30, TotalFrames = 60 };
            Assert.Equal(50.0, args.Percentage, precision: 5);
        }

        [Fact]
        public void RenderProgressEventArgs_ZeroTotalFrames_ReturnsZeroPercentage()
        {
            var args = new RenderProgressEventArgs { FrameIndex = 5, TotalFrames = 0 };
            Assert.Equal(0.0, args.Percentage, precision: 5);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AbletonLiveProjectWriter tests (Issue 6.2 / #39)
    // ─────────────────────────────────────────────────────────────────────────

    public class AbletonLiveProjectWriterTests
    {
        private static AbletonLiveProjectWriter MakeWriter()
            => new(NullLogger<AbletonLiveProjectWriter>.Instance);

        private static AbletonTrack MakeTrack(
            string title  = "Test Track",
            string artist = "DJ Test",
            float  bpm    = 128f,
            double dur    = 210.0,
            string key    = "F#",
            string scale  = "minor")
            => new(
                FilePath: @"C:\Music\test.mp3",
                Title:    title,
                Artist:   artist,
                DurationSeconds: dur,
                Bpm:      bpm,
                Key:      key,
                Scale:    scale);

        [Fact]
        public void BuildProjectXml_ContainsAbletonRootElement()
        {
            var writer = MakeWriter();
            var doc = writer.BuildProjectXml(new[] { MakeTrack() });
            Assert.Equal("Ableton", doc.Root!.Name.LocalName);
        }

        [Fact]
        public void BuildProjectXml_ContainsLiveSetAndTracks()
        {
            var writer = MakeWriter();
            var doc    = writer.BuildProjectXml(new[] { MakeTrack(), MakeTrack("B") });
            var tracks = doc.Descendants("AudioTrack").ToList();
            Assert.Equal(2, tracks.Count);
        }

        [Fact]
        public void BuildProjectXml_TrackNameContainsTitleAndArtist()
        {
            var writer = MakeWriter();
            var doc    = writer.BuildProjectXml(new[] { MakeTrack(title: "Alpha", artist: "Beta") });
            var name   = doc.Descendants("EffectiveName")
                            .FirstOrDefault(e => e.Attribute("Value")?.Value.Contains("Alpha") == true);
            Assert.NotNull(name);
        }

        [Fact]
        public void BuildProjectXml_EmptyList_ProducesValidDocument()
        {
            var writer = MakeWriter();
            var doc    = writer.BuildProjectXml(Array.Empty<AbletonTrack>());
            Assert.NotNull(doc.Root);
            Assert.Empty(doc.Descendants("AudioTrack"));
        }

        [Fact]
        public void BuildProjectXml_WarpMarkerPresentWhenBpmKnown()
        {
            var writer = MakeWriter();
            var doc    = writer.BuildProjectXml(new[] { MakeTrack(bpm: 128f) });
            var warpMarker = doc.Descendants("WarpMarker").FirstOrDefault();
            Assert.NotNull(warpMarker);
        }

        [Fact]
        public void BuildProjectXml_TempoSetFromFirstTrackBpm()
        {
            var writer = MakeWriter();
            var doc    = writer.BuildProjectXml(new[] { MakeTrack(bpm: 140f) });
            var tempo  = doc.Descendants("Tempo")
                            .FirstOrDefault(e => e.Parent?.Name.LocalName == "Transport");
            Assert.NotNull(tempo);
            string? val = tempo!.Attribute("Value")?.Value;
            Assert.Contains("140", val);
        }

        [Fact]
        public void Export_WritesGzippedFile()
        {
            var writer = MakeWriter();
            string path = Path.Combine(Path.GetTempPath(), $"orbit_ableton_test_{Guid.NewGuid():N}.als");
            try
            {
                writer.Export(new[] { MakeTrack() }, path);
                Assert.True(File.Exists(path), "ALS file was not created");

                // Verify it's a valid gzip stream
                using var fs = File.OpenRead(path);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var sr = new System.IO.StreamReader(gz);
                string xml  = sr.ReadToEnd();
                Assert.Contains("<Ableton", xml);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Export_ThrowsOnEmptyOutputPath()
        {
            var writer = MakeWriter();
            Assert.Throws<ArgumentException>(() =>
                writer.Export(new[] { MakeTrack() }, ""));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TraktorMetadataImporter tests (Issue 6.3 / #40)
    // ─────────────────────────────────────────────────────────────────────────

    public class TraktorMetadataImporterTests
    {
        private static TraktorMetadataImporter MakeImporter()
            => new(NullLogger<TraktorMetadataImporter>.Instance);

        private static string WriteNml(string xml)
        {
            string path = Path.Combine(Path.GetTempPath(), $"traktor_test_{Guid.NewGuid():N}.nml");
            File.WriteAllText(path, xml, Encoding.UTF8);
            return path;
        }

        private const string SampleNml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<NML VERSION=""19"">
  <COLLECTION ENTRIES=""2"">
    <ENTRY ARTIST=""Test Artist"" TITLE=""Track One"">
      <LOCATION DIR=""/Music/"" FILE=""track1.mp3"" VOLUME=""C:"" VOLUMEID=""c""/>
      <TEMPO BPM=""128.00""/>
      <MUSICAL_KEY VALUE=""0""/>
      <CUE_V2 NAME=""Drop"" START=""32000"" TYPE=""0"" HOTCUE=""0"" CUEID=""0""/>
    </ENTRY>
    <ENTRY ARTIST=""Another Artist"" TITLE=""Track Two"">
      <LOCATION DIR=""/Music/"" FILE=""track2.mp3"" VOLUME=""C:"" VOLUMEID=""c""/>
      <TEMPO BPM=""140.50""/>
      <MUSICAL_KEY VALUE=""23""/>
    </ENTRY>
  </COLLECTION>
</NML>";

        [Fact]
        public void ImportLibrary_ParsesTwoTracks()
        {
            string path = WriteNml(SampleNml);
            try
            {
                var results = MakeImporter().ImportLibrary(path);
                Assert.Equal(2, results.Count);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ImportLibrary_ParsesBpm()
        {
            string path = WriteNml(SampleNml);
            try
            {
                var results = MakeImporter().ImportLibrary(path);
                Assert.Equal(128.0, results[0].Bpm, precision: 2);
                Assert.Equal(140.5, results[1].Bpm, precision: 2);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ImportLibrary_ParsesArtistAndTitle()
        {
            string path = WriteNml(SampleNml);
            try
            {
                var results = MakeImporter().ImportLibrary(path);
                Assert.Equal("Test Artist",  results[0].Artist);
                Assert.Equal("Track One",    results[0].Title);
                Assert.Equal("Another Artist", results[1].Artist);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ImportLibrary_ParsesCuePoints()
        {
            string path = WriteNml(SampleNml);
            try
            {
                var results = MakeImporter().ImportLibrary(path);
                Assert.Single(results[0].Cues);
                Assert.Equal(32.0, results[0].Cues[0].TimestampSeconds, precision: 3);
                Assert.Equal("Drop", results[0].Cues[0].Name);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ImportLibrary_MapsTraktorKeyToOpenKey()
        {
            string path = WriteNml(SampleNml);
            try
            {
                var results = MakeImporter().ImportLibrary(path);
                // VALUE=0 → "1m" (Am), VALUE=23 → "1d" (F)
                Assert.Equal("1m", results[0].Key);
                Assert.Equal("1d", results[1].Key);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ImportLibrary_NonexistentFile_ReturnsEmptyList()
        {
            var results = MakeImporter().ImportLibrary("/nonexistent/path/collection.nml");
            Assert.Empty(results);
        }

        [Fact]
        public void ImportLibrary_MissingCollectionElement_ReturnsEmptyList()
        {
            string nml  = @"<?xml version=""1.0""?><NML VERSION=""19""></NML>";
            string path = WriteNml(nml);
            try
            {
                var results = MakeImporter().ImportLibrary(path);
                Assert.Empty(results);
            }
            finally { File.Delete(path); }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SeratoMetadataImporter tests (Issue 6.3 / #40)
    // ─────────────────────────────────────────────────────────────────────────

    public class SeratoMetadataImporterTests
    {
        [Fact]
        public void Import_NonexistentFile_ReturnsNull()
        {
            var importer = new SeratoMetadataImporter(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SeratoMetadataImporter>.Instance);

            var result = importer.Import("/nonexistent/audio.mp3");
            Assert.Null(result);
        }
    }
}
