using System;
using System.Collections.Generic;
using Xunit;
using SLSKDONET.Services;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using Microsoft.Extensions.Logging;
using Moq;

namespace SLSKDONET.Tests.Services
{
    public class VocalIntelligenceTests
    {
        private readonly VocalIntelligenceService _vocalService;

        public VocalIntelligenceTests()
        {
            _vocalService = new VocalIntelligenceService();
        }

        [Fact]
        public void AnalyzeVocalDensity_IdentifiesInstrumental()
        {
            float[] data = new float[] { 0, 0, 0, 0, 0 };
            var result = _vocalService.AnalyzeVocalDensity(data, 100);
            Assert.Equal(VocalType.Instrumental, result.Type);
            Assert.Equal(0, result.Intensity);
        }

        [Fact]
        public void CalculateOverlapHazard_DetectsHighConflict()
        {
            // Both tracks have high density in the overlap zone
            float[] curveA = new float[100];
            float[] curveB = new float[100];
            for (int i = 80; i < 100; i++) curveA[i] = 1.0f;
            for (int i = 0; i < 20; i++) curveB[i] = 1.0f;

            double hazard = _vocalService.CalculateOverlapHazard(curveA, curveB, 80, 100, 100);
            Assert.True(hazard > 0.8, $"Hazard should be high, was {hazard}");
        }
    }

    public class TransitionAdvisorTests
    {
        private readonly TransitionAdvisorService _advisor;
        private readonly Mock<VocalIntelligenceService> _vocalMock;
        private readonly Mock<HarmonicMatchService> _harmonicMock;
        private readonly Mock<IPhraseAlignmentService> _phraseMock;

        public TransitionAdvisorTests()
        {
            _vocalMock = new Mock<VocalIntelligenceService>();
            _phraseMock = new Mock<IPhraseAlignmentService>();
            
            _advisor = new TransitionAdvisorService(
                new Mock<ILogger<TransitionAdvisorService>>().Object,
                null!, // harmonicService - concrete class causing Moq proxy issues, currently unused by advisor
                _vocalMock.Object,
                _phraseMock.Object);
        }

        [Fact]
        public void AdviseTransition_SuggestsVocalToInstrumental()
        {
            var trackA = new LibraryEntryEntity { VocalType = VocalType.FullLyrics, VocalEndSeconds = 200, Energy = 0.5 };
            var trackB = new LibraryEntryEntity { VocalType = VocalType.Instrumental, Energy = 0.5 };

            var suggestion = _advisor.AdviseTransition(trackA, trackB);
            
            Assert.Equal(TransitionArchetype.VocalToInstrumental, suggestion.Archetype);
            Assert.Contains("instrumental", suggestion.Reasoning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FullLyrics", suggestion.Reasoning);
        }

        [Fact]
        public void CalculateFlowContinuity_PenalizesLyricalClash()
        {
            var trackA = new LibraryEntryEntity 
            { 
                VocalType = VocalType.FullLyrics, 
                BPM = 120, 
                MusicalKey = "8A",
                AudioFeatures = new AudioFeaturesEntity() // Avoid null checks in logic
            };
            var trackB = new LibraryEntryEntity 
            { 
                VocalType = VocalType.FullLyrics, 
                BPM = 120, 
                MusicalKey = "8A",
                AudioFeatures = new AudioFeaturesEntity()
            };
            
            var sequence = new List<(LibraryEntryEntity, SetTrackEntity)>
            {
                (trackA, new SetTrackEntity()),
                (trackB, new SetTrackEntity())
            };

            var score = _advisor.CalculateFlowContinuity(sequence);

            // Expect penalty: 1.0 - (0.3 * 0.9) = 0.73
            Assert.True(score < 0.9, $"Score {score} should be penalized for lyrical clash");
        }

        [Fact]
        public void CalculateFlowContinuity_RewardsInstrumentalTransition()
        {
            var trackA = new LibraryEntryEntity 
            { 
                VocalType = VocalType.FullLyrics, 
                BPM = 120, 
                MusicalKey = "8A",
                AudioFeatures = new AudioFeaturesEntity()
            };
            var trackB = new LibraryEntryEntity 
            { 
                VocalType = VocalType.Instrumental, 
                BPM = 120, 
                MusicalKey = "8A",
                AudioFeatures = new AudioFeaturesEntity()
            };

            var sequence = new List<(LibraryEntryEntity, SetTrackEntity)>
            {
                (trackA, new SetTrackEntity()),
                (trackB, new SetTrackEntity())
            };

            var score = _advisor.CalculateFlowContinuity(sequence);

            Assert.Equal(1.0, score);
        }

        [Fact]
        public void AdviseTransition_SuggestsDropSwap_ForHighEnergyClash()
        {
            var trackA = new LibraryEntryEntity 
            { 
                VocalType = VocalType.HookOnly,
                BPM = 128,
                Energy = 0.9,
                AudioFeatures = new AudioFeaturesEntity { Energy = 0.9f }
            };
            var trackB = new LibraryEntryEntity 
            { 
                VocalType = VocalType.FullLyrics,
                BPM = 128,
                Energy = 0.85,
                AudioFeatures = new AudioFeaturesEntity { Energy = 0.85f, DropTimeSeconds = 30 }
            };

            var suggestion = _advisor.AdviseTransition(trackA, trackB);

            // With builder, it's multi-line. Just check for key phrase
            Assert.Equal(TransitionArchetype.DropSwap, suggestion.Archetype);
            Assert.Contains("Drop-Swap", suggestion.Reasoning);
        }

        [Fact]
        public void AdviseTransition_SuggestsBuildToDrop_WhenBuildDetected()
        {
            var trackA = new LibraryEntryEntity 
            { 
                VocalType = VocalType.SparseVocals,
                AudioFeatures = new AudioFeaturesEntity 
                { 
                    // Emulate a Build segment
                    PhraseSegmentsJson = "[{\"Label\":\"Build\",\"Start\":120,\"Duration\":16}]"
                }
            };
            var trackB = new LibraryEntryEntity 
            { 
                VocalType = VocalType.Instrumental,
                AudioFeatures = new AudioFeaturesEntity 
                { 
                    DropTimeSeconds = 30 
                }
            };

            var suggestion = _advisor.AdviseTransition(trackA, trackB);

            Assert.Equal(TransitionArchetype.BuildToDrop, suggestion.Archetype);
            Assert.Contains("Build-to-Drop", suggestion.Reasoning);
        }
    }
}
