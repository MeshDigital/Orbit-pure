using System;
using System.Collections.Generic;
using System.Text.Json;
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
