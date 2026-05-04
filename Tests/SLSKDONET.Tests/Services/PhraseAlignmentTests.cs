using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using Xunit;

namespace SLSKDONET.Tests.Services
{
    public class PhraseAlignmentTests
    {
        private readonly PhraseAlignmentService _service;

        public PhraseAlignmentTests()
        {
            _service = new PhraseAlignmentService(new NullLogger<PhraseAlignmentService>());
        }

        [Fact]
        public async Task AlignPhrasesAsync_SnapsToGenreAwareTemplate()
        {
            var raw = new List<RawBoundary>
            {
                new() { StartTimeSeconds = 1.2, Label = "Intro", Confidence = 0.7f, EnergyLevel = 0.2f },
                new() { StartTimeSeconds = 59.8, Label = "Build", Confidence = 0.8f, EnergyLevel = 0.5f },
                new() { StartTimeSeconds = 90.5, Label = "Drop", Confidence = 0.9f, EnergyLevel = 0.9f }
            };

            var phrases = await _service.AlignPhrasesAsync(raw, 128, "EDM", "track-1");

            Assert.NotEmpty(phrases);
            Assert.Equal(PhraseType.Intro, phrases[0].Type);
            Assert.InRange(phrases[0].StartTimeSeconds, 0f, 0.5f);
            Assert.Contains(phrases, p => p.Type == PhraseType.Drop);
        }

        [Fact]
        public async Task AlignPhrasesAsync_UsesPopPresetShorterPhraseLengths()
        {
            var raw = new List<RawBoundary>
            {
                new() { StartTimeSeconds = 0.2, Label = "Intro", Confidence = 0.9f, EnergyLevel = 0.1f },
                new() { StartTimeSeconds = 16.1, Label = "Drop", Confidence = 0.9f, EnergyLevel = 0.85f }
            };

            var phrases = await _service.AlignPhrasesAsync(raw, 120, "Pop", "track-2");

            Assert.NotEmpty(phrases);
            Assert.Equal(PhraseType.Intro, phrases[0].Type);
            Assert.True(phrases[0].EndTimeSeconds > phrases[0].StartTimeSeconds);
            Assert.Contains(phrases, p => p.Type == PhraseType.Drop || p.Type == PhraseType.Build);
        }

        [Fact]
        public void DetermineOptimalTransitionTime_ReturnsPhraseBoundary()
        {
            // Arrange
            var trackA = new LibraryEntryEntity
            {
                AudioFeatures = new AudioFeaturesEntity
                {
                    PhraseSegmentsJson = JsonSerializer.Serialize(new List<PhraseSegment>
                    {
                        new PhraseSegment { Label = "Intro", Start = 0, Duration = 32 },
                        new PhraseSegment { Label = "Verse 1", Start = 32, Duration = 64 }
                    })
                }
            };
            var trackB = new LibraryEntryEntity();

            // Act
            var result = _service.DetermineOptimalTransitionTime(trackA, trackB, TransitionArchetype.QuickCut);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(32, result.Value.Time);
            Assert.Contains("phrase boundary", result.Value.Reason);
        }

        [Fact]
        public void DetermineOptimalTransitionTime_BuildToDrop_Override()
        {
            // Arrange
            var trackA = new LibraryEntryEntity
            {
                AudioFeatures = new AudioFeaturesEntity
                {
                    PhraseSegmentsJson = JsonSerializer.Serialize(new List<PhraseSegment>
                    {
                        new PhraseSegment { Label = "Build", Start = 60, Duration = 32 }
                    })
                }
            };
            var trackB = new LibraryEntryEntity
            {
                AudioFeatures = new AudioFeaturesEntity
                {
                    DropTimeSeconds = 10
                }
            };

            // Act
            var result = _service.DetermineOptimalTransitionTime(trackA, trackB, TransitionArchetype.BuildToDrop);

            // Assert
            Assert.NotNull(result);
            // Build end = 60 + 32 = 92. Drop time B = 10. Start B at 92 - 10 = 82.
            Assert.Equal(82, result.Value.Time);
            Assert.Contains("Build-to-Drop", result.Value.Reason);
        }

        [Fact]
        public void DetermineOptimalTransitionTime_VocalSafety_Priority()
        {
            // Arrange
            var trackA = new LibraryEntryEntity
            {
                VocalEndSeconds = 45,
                AudioFeatures = new AudioFeaturesEntity
                {
                    PhraseSegmentsJson = JsonSerializer.Serialize(new List<PhraseSegment>
                    {
                        new PhraseSegment { Label = "Chorus", Start = 32, Duration = 32 } // Ends at 64
                    })
                }
            };
            var trackB = new LibraryEntryEntity();

            // Act
            var result = _service.DetermineOptimalTransitionTime(trackA, trackB, TransitionArchetype.QuickCut);

            // Assert
            Assert.NotNull(result);
            // 64 is a phrase boundary AND after vocal end (45).
            Assert.Equal(64, result.Value.Time);
            Assert.Contains("vocals clear", result.Value.Reason);
            Assert.Contains("phrase boundary", result.Value.Reason);
        }
    }
}
