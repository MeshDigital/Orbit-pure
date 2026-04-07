using System;
using System.Collections.Generic;
using SkiaSharp;
using Xunit;
using SLSKDONET.Models;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Tests.Services.Timeline
{
    // ─────────────────────────────────────────────────────────────────────────
    // Task 13.2 — WaveformRenderer unit tests
    //
    // Covers Issue #115 checklist:
    //  ✓ RGB channel data is produced for a known audio fixture
    //  ✓ Zoom level calculations (correct sample range per zoom)
    //  ✓ Cue marker positions match stored cue timestamps
    // ─────────────────────────────────────────────────────────────────────────

    public class WaveformRendererTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Flat RMS profile of length <paramref name="count"/> at constant <paramref name="value"/>.</summary>
        private static float[] FlatProfile(int count, float value = 0.5f)
        {
            var p = new float[count];
            for (int i = 0; i < count; i++) p[i] = value;
            return p;
        }

        /// <summary>Ascending ramp 0→255 as byte array source data.</summary>
        private static byte[] RampBytes(int count)
        {
            var b = new byte[count];
            for (int i = 0; i < count; i++) b[i] = (byte)Math.Min(255, i * 255 / count);
            return b;
        }

        private static WaveformAnalysisData MakeData(int bins = 200)
        {
            var bytes = RampBytes(bins);
            return new WaveformAnalysisData
            {
                RmsData  = bytes,
                LowData  = bytes,
                MidData  = bytes,
                HighData = bytes,
                PeakData = bytes,
                DurationSeconds = 3.0,
            };
        }

        // ── ComputeRmsProfile ─────────────────────────────────────────────────

        [Fact]
        public void ComputeRmsProfile_EmptyInput_ReturnsEmpty()
        {
            var profile = WaveformRenderer.ComputeRmsProfile(Array.Empty<byte>(), 100);
            Assert.Empty(profile);
        }

        [Fact]
        public void ComputeRmsProfile_ZeroTargetBins_ReturnsEmpty()
        {
            var profile = WaveformRenderer.ComputeRmsProfile(new byte[] { 128 }, 0);
            Assert.Empty(profile);
        }

        [Fact]
        public void ComputeRmsProfile_SingleByte255_ReturnsOnePoint()
        {
            var profile = WaveformRenderer.ComputeRmsProfile(new byte[] { 255 }, 1);
            Assert.Single(profile);
            Assert.Equal(1.0f, profile[0], precision: 5);
        }

        [Fact]
        public void ComputeRmsProfile_SilentBytes_ReturnsZero()
        {
            var src     = new byte[100]; // all zeros
            var profile = WaveformRenderer.ComputeRmsProfile(src, 100);
            Assert.All(profile, v => Assert.Equal(0f, v));
        }

        [Fact]
        public void ComputeRmsProfile_DownsamplesCorrectly()
        {
            // 200 bytes → 100 bins: each bin averages two bytes
            var src = new byte[200];
            for (int i = 0; i < 200; i++) src[i] = 200; // constant 200
            var profile = WaveformRenderer.ComputeRmsProfile(src, 100);
            Assert.Equal(100, profile.Length);
            // 200/255 ≈ 0.7843
            Assert.All(profile, v => Assert.Equal(200f / 255f, v, precision: 4));
        }

        [Fact]
        public void ComputeRmsProfile_UpsamplesCorrectly()
        {
            // 10 bytes → 100 bins: interpolation must not crash
            var src = new byte[10];
            for (int i = 0; i < 10; i++) src[i] = 100;
            var profile = WaveformRenderer.ComputeRmsProfile(src, 100);
            Assert.Equal(100, profile.Length);
            Assert.All(profile, v => Assert.InRange(v, 0f, 1f));
        }

        // ── Render: basic size + bounds ───────────────────────────────────────

        [Fact]
        public void Render_ProducesExpectedBitmapSize()
        {
            var profile = FlatProfile(50);
            using var bmp = WaveformRenderer.Render(profile, 128, 64,
                SKColors.White, SKColors.Black);
            Assert.Equal(128, bmp.Width);
            Assert.Equal(64,  bmp.Height);
        }

        [Fact]
        public void Render_EmptyProfile_ReturnsBitmapWithBackground()
        {
            using var bmp = WaveformRenderer.Render(Array.Empty<float>(), 64, 32,
                SKColors.White, SKColors.DarkSlateGray);
            // Background fill only — no exception
            Assert.Equal(64, bmp.Width);
            Assert.Equal(32, bmp.Height);
        }

        // ── Zoom level calculations — checks sample range per zoom ────────────

        /// <summary>
        /// At zoom=1, the full profile must be visible:
        /// pixel width equals number of rendered bars ≈ profile length.
        /// We verify by checking that a bright spike in the first half
        /// of the profile is rendered — meaning samples from the start
        /// are used.
        /// </summary>
        [Fact]
        public void Render_ZoomOne_ShowsFullProfile()
        {
            // Profile with a bright spike at index 0 and dark everywhere else
            var profile             = new float[200];
            profile[0]              = 1.0f; // bright bar at the start

            using var bmp = WaveformRenderer.Render(profile, 200, 64,
                SKColors.White, SKColors.Black, zoom: 1.0, scrollOffset: 0.0);

            // The leftmost bar should be non-black (white from the spike at index 0)
            var px = bmp.GetPixel(0, 32); // centre row, first column
            Assert.True(px.Red > 100 || px.Green > 100 || px.Blue > 100,
                "Expected the first bar to be brighter than background at zoom=1");
        }

        [Fact]
        public void Render_ZoomTwo_ShowsFirstHalfOfProfile()
        {
            // At zoom=2 with scroll=0 we should only see the first half of the profile.
            // Use distinct value in 2nd half (0.0) and 1st half (1.0).
            int n          = 200;
            var profile    = new float[n];
            for (int i = 0; i < n / 2; i++) profile[i] = 1.0f;
            for (int i = n / 2; i < n; i++) profile[i] = 0.0f;

            using var bmp = WaveformRenderer.Render(profile, 200, 64,
                SKColors.White, SKColors.Black, zoom: 2.0, scrollOffset: 0.0);

            // The right edge of the bitmap corresponds to sample n/2 (under zoom=2).
            // The last bar should still be non-black (still within the bright 1st half).
            var px = bmp.GetPixel(bmp.Width - 1, 32);
            Assert.True(px.Red > 100 || px.Green > 100 || px.Blue > 100,
                "At zoom=2 scroll=0 the right edge should still be in the bright first-half");
        }

        [Fact]
        public void Render_ZoomTwo_ScrollOne_ShowsSecondHalf()
        {
            // At zoom=2, scroll=1: the second half (all 0 = flat/black) is shown.
            int n          = 200;
            var profile    = new float[n];
            for (int i = 0; i < n / 2; i++) profile[i] = 1.0f;
            for (int i = n / 2; i < n; i++) profile[i] = 0.0f;

            using var bmp = WaveformRenderer.Render(profile, 200, 64,
                SKColors.White, SKColors.Black, zoom: 2.0, scrollOffset: 1.0);

            // All bars should be background (black) since the second half is all 0.
            var centre = bmp.GetPixel(100, 32);
            Assert.True(centre.Red < 50 && centre.Green < 50 && centre.Blue < 50,
                "At zoom=2 scroll=1 the second half (all-zero profile) must be all background");
        }

        // ── RGB tri-band data ─────────────────────────────────────────────────

        /// <summary>
        /// When all three band byte arrays are populated, <see cref="WaveformRenderer.RenderRgb"/>
        /// must produce a bitmap where at least two colour channels are non-zero.
        /// (Additive blending overlaps the bands so the composite is coloured.)
        /// </summary>
        [Fact]
        public void RenderRgb_AllBandsFilled_ProducesColouredPixels()
        {
            var data = new WaveformAnalysisData
            {
                LowData  = new byte[200].Fill(200),
                MidData  = new byte[200].Fill(200),
                HighData = new byte[200].Fill(200),
                DurationSeconds = 3.0,
            };

            using var bmp = WaveformRenderer.RenderRgb(data, 200, 64, SKColors.Black);

            // The centre of the bitmap should be visibly lit (at least one channel > 50)
            var px = bmp.GetPixel(100, 32);
            Assert.True(px.Red > 50 || px.Green > 50 || px.Blue > 50,
                "RGB render of full-amplitude bands should produce non-black pixels");
        }

        [Fact]
        public void RenderRgb_EmptyBands_ProducesBackgroundOnly()
        {
            var data = new WaveformAnalysisData
            {
                LowData  = Array.Empty<byte>(),
                MidData  = Array.Empty<byte>(),
                HighData = Array.Empty<byte>(),
                DurationSeconds = 0,
            };

            using var bmp = WaveformRenderer.RenderRgb(data, 64, 32, SKColors.Black);

            // Nothing drawn — should remain background
            var px = bmp.GetPixel(32, 16);
            Assert.True(px.Red < 10 && px.Green < 10 && px.Blue < 10,
                "Empty band arrays should produce a near-black bitmap");
        }

        [Fact]
        public void RenderRgb_LowBandOnly_ContainsRedChannel()
        {
            var data = new WaveformAnalysisData
            {
                LowData  = new byte[200].Fill(220), // bass spike
                MidData  = Array.Empty<byte>(),
                HighData = Array.Empty<byte>(),
                DurationSeconds = 3.0,
            };

            using var bmp = WaveformRenderer.RenderRgb(data, 200, 64, SKColors.Black);
            var px = bmp.GetPixel(100, 32);
            // Bass maps to red channel (#FF4444). With additive blend, red should dominate.
            Assert.True(px.Red > px.Blue,
                "Low-band-only render should produce a red-dominant pixel");
        }

        // ── Cue marker positions match stored timestamps ───────────────────────

        [Fact]
        public void OverlayCueMarkers_MarkerAtStartIsLeftmost()
        {
            // Place a cue at t=0 with a track of 10 seconds.
            // At zoom=1, scroll=0: the cue's x position should be ≈0.
            using var bmp = WaveformRenderer.Render(FlatProfile(200), 200, 64,
                SKColors.White, SKColors.Black);

            var cues = new[] { (TimeSeconds: 0.0, Color: SKColors.Magenta, Label: (string?)null) };
            WaveformRenderer.OverlayCueMarkers(bmp, cues, trackDurationSeconds: 10.0);

            // After overlay the pixel at x=0 in the top rows must contain the magenta marker colour.
            var px = bmp.GetPixel(0, 0); // top of the triangle marker
            // Magenta = high R + high B; at minimum the R channel should exceed the grey background.
            Assert.True(px.Red > 100 || px.Blue > 100,
                "Cue at t=0 should place a coloured marker at the left edge of the bitmap");
        }

        [Fact]
        public void OverlayCueMarkers_MarkerAtMidpoint_IsHorizontallyCentred()
        {
            // Cue at t=5 on a 10s track → x should be ~width/2 (±2px tolerance).
            int width = 200;
            using var bmp = WaveformRenderer.Render(FlatProfile(200, 0.1f), width, 64,
                SKColors.DarkGray, SKColors.Black);

            var cues = new[] { (TimeSeconds: 5.0, Color: SKColors.Cyan, Label: (string?)null) };
            WaveformRenderer.OverlayCueMarkers(bmp, cues, trackDurationSeconds: 10.0);

            // Cyan = full green + full blue, almost no red → check G or B channel near centre
            int cx   = width / 2;
            bool foundMarker = false;
            for (int x = cx - 3; x <= cx + 3; x++)
            {
                var px = bmp.GetPixel(x, 0); // row 0 is tip of the triangle
                if (px.Green > 100 || px.Blue > 100)
                {
                    foundMarker = true;
                    break;
                }
            }
            Assert.True(foundMarker,
                "Cue at t=5 on a 10s track should place a cyan marker near the horizontal midpoint");
        }

        [Fact]
        public void OverlayCueMarkers_CueOutsideVisibleWindow_IsNotDrawn()
        {
            // At zoom=2, scroll=0 the visible window covers t=[0, 5] of a 10s track.
            // A cue at t=8 is outside that window and should not appear in the bitmap.
            int width = 200;
            using var bmp = WaveformRenderer.Render(FlatProfile(200, 0f), width, 64,
                SKColors.DarkGray, SKColors.Black, zoom: 2.0, scrollOffset: 0.0);

            var cues = new[] { (TimeSeconds: 8.0, Color: SKColors.Red, Label: (string?)null) };
            WaveformRenderer.OverlayCueMarkers(bmp, cues,
                trackDurationSeconds: 10.0, zoom: 2.0, scrollOffset: 0.0);

            // Bitmap should contain no strongly-red pixels (background is black silenced waveform)
            bool foundRed = false;
            for (int x = 0; x < width; x++)
            {
                var px = bmp.GetPixel(x, 0);
                if (px.Red > 150 && px.Green < 50 && px.Blue < 50) { foundRed = true; break; }
            }
            Assert.False(foundRed,
                "A cue outside the visible window must not be drawn");
        }

        // ── RenderFromWaveformData ─────────────────────────────────────────────

        [Fact]
        public void RenderFromWaveformData_NullishData_DoesNotThrow()
        {
            var data = new WaveformAnalysisData(); // all empty arrays
            using var bmp = WaveformRenderer.RenderFromWaveformData(data, 64, 32,
                SKColors.White, SKColors.Black);
            Assert.Equal(64, bmp.Width);
            Assert.Equal(32, bmp.Height);
        }

        [Fact]
        public void RenderFromWaveformData_WithRmsData_ProducesNonBlackBitmap()
        {
            var data = new WaveformAnalysisData
            {
                RmsData = new byte[100].Fill(200),
                DurationSeconds = 2.0,
            };

            using var bmp = WaveformRenderer.RenderFromWaveformData(data, 100, 64,
                SKColors.White, SKColors.Black);

            var px = bmp.GetPixel(50, 32);
            Assert.True(px.Red > 100 || px.Green > 100 || px.Blue > 100,
                "High-amplitude RmsData should produce bright pixels at the waveform centre");
        }
    }

    // ── Byte[] fill extension helper ─────────────────────────────────────────

    internal static class ByteArrayExtensions
    {
        public static byte[] Fill(this byte[] arr, byte value)
        {
            for (int i = 0; i < arr.Length; i++) arr[i] = value;
            return arr;
        }
    }
}
