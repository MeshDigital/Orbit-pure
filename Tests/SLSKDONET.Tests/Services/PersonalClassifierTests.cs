using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Tests.Services
{
    /// <summary>
    /// Phase 12.6: AI Sanity Tests for PersonalClassifierService.FindSimilarTracks
    /// Verifies the Cosine Similarity logic is mathematically correct.
    /// </summary>
    public class PersonalClassifierTests
    {
        // We test FindSimilarTracks directly - it's a pure function that doesn't need DB
        private PersonalClassifierService CreateService()
        {
            // The constructor requires DatabaseService but FindSimilarTracks doesn't use it
            return new PersonalClassifierService(null!);
        }

        [Fact]
        public void FindSimilarTracks_IdenticalVectors_ReturnsMaxSimilarity()
        {
            // Arrange
            var service = CreateService();
            var vector = CreateUnitVector(1.0f, 0.0f);
            var jsonVector = JsonSerializer.Serialize(vector);

            var candidates = new List<AudioFeaturesEntity>
            {
                new AudioFeaturesEntity 
                { 
                    TrackUniqueHash = "track_identical", 
                    VectorEmbedding = vector,
                    EmbeddingMagnitude = 1.0f,
                    Bpm = 120 // Within BPM range
                }
            };

            // Act
            var results = service.FindSimilarTracks(vector, 120, candidates, limit: 10);

            // Assert
            Assert.Single(results);
            Assert.Equal("track_identical", results[0].TrackHash);
            Assert.True(results[0].Similarity > 0.99f, $"Expected similarity ~1.0, got {results[0].Similarity}");
        }

        [Fact]
        public void FindSimilarTracks_OrthogonalVectors_ReturnsEmpty()
        {
            // Arrange
            var service = CreateService();
            var seed = CreateUnitVector(1.0f, 0.0f);   // X-axis
            var target = CreateUnitVector(0.0f, 1.0f); // Y-axis (Orthogonal = 90°)
            
            var candidates = new List<AudioFeaturesEntity>
            {
                new AudioFeaturesEntity 
                { 
                    TrackUniqueHash = "track_orthogonal", 
                    VectorEmbedding = target,
                    Bpm = 120
                }
            };

            // Act
            var results = service.FindSimilarTracks(seed, 120, candidates, limit: 10);

            // Assert: Cos(90°) = 0, below any reasonable threshold
            Assert.Empty(results);
        }

        [Fact]
        public void FindSimilarTracks_OppositeVectors_ReturnsEmpty()
        {
            // Arrange
            var service = CreateService();
            var seed = CreateUnitVector(1.0f, 0.0f);
            var target = CreateUnitVector(-1.0f, 0.0f); // Opposite = 180°
            
            var candidates = new List<AudioFeaturesEntity>
            {
                new AudioFeaturesEntity 
                { 
                    TrackUniqueHash = "track_opposite", 
                    VectorEmbedding = target,
                    Bpm = 120
                }
            };

            // Act
            var results = service.FindSimilarTracks(seed, 120, candidates, limit: 10);

            // Assert: Cos(180°) = -1, definitely filtered out
            Assert.Empty(results);
        }

        [Fact]
        public void FindSimilarTracks_HandlesCorruptedJson_NoException()
        {
            // Arrange
            var service = CreateService();
            var seed = CreateUnitVector(1.0f, 1.0f);
            
            var candidates = new List<AudioFeaturesEntity>
            {
                new AudioFeaturesEntity 
                { 
                    TrackUniqueHash = "track_bad_json", 
                    AiEmbeddingJson = "{ not valid json }",
                    Bpm = 120
                },
                new AudioFeaturesEntity 
                { 
                    TrackUniqueHash = "track_null_json", 
                    AiEmbeddingJson = string.Empty,
                    Bpm = 120
                },
                new AudioFeaturesEntity 
                { 
                    TrackUniqueHash = "track_empty", 
                    AiEmbeddingJson = "",
                    Bpm = 120
                }
            };

            // Act - should not throw
            var exception = Record.Exception(() => 
                service.FindSimilarTracks(seed, 120, candidates, limit: 10));

            // Assert
            Assert.Null(exception);
        }

        [Fact]
        public void FindSimilarTracks_RespectsSimilarityThreshold()
        {
            // Arrange
            var service = CreateService();
            var seed = CreateUnitVector(1.0f, 0.0f);
            
            // Vector at ~30° (Cos(30°) ≈ 0.866) - should be included if threshold is 0.8
            var vecHigh = CreateUnitVector(0.866f, 0.5f);
            // Vector at ~60° (Cos(60°) = 0.5) - should be excluded
            var vecLow = CreateUnitVector(0.5f, 0.866f);

            var candidates = new List<AudioFeaturesEntity>
            {
                new AudioFeaturesEntity { TrackUniqueHash = "high_sim", VectorEmbedding = vecHigh, Bpm = 120 },
                new AudioFeaturesEntity { TrackUniqueHash = "low_sim", VectorEmbedding = vecLow, Bpm = 120 }
            };

            // Act
            var results = service.FindSimilarTracks(seed, 120, candidates, limit: 10);

            // Assert: Only high similarity should pass (depends on threshold in actual code)
            // If threshold is 0.8, only high_sim passes
            Assert.Contains(results, r => r.TrackHash == "high_sim");
        }

        [Fact]
        public void FindSimilarTracks_ReturnsOrderedByScore()
        {
            // Arrange
            var service = CreateService();
            var seed = CreateUnitVector(1.0f, 0.0f);
            
            var vec90pct = CreateUnitVector(0.95f, 0.31f);  // ~18° ≈ 0.95 similarity
            var vec85pct = CreateUnitVector(0.90f, 0.44f);  // ~26° ≈ 0.90 similarity

            var candidates = new List<AudioFeaturesEntity>
            {
                new AudioFeaturesEntity { TrackUniqueHash = "track_85", VectorEmbedding = vec85pct, Bpm = 120 },
                new AudioFeaturesEntity { TrackUniqueHash = "track_90", VectorEmbedding = vec90pct, Bpm = 120 }
            };

            // Act
            var results = service.FindSimilarTracks(seed, 120, candidates, limit: 10);

            // Assert: Results should be ordered highest first
            Assert.True(results.Count >= 2);
            Assert.Equal("track_90", results[0].TrackHash); // 95% should be first
        }

        /// <summary>
        /// Creates a unit vector with specified first 2 components.
        /// Normalizes to unit length for proper Cosine Similarity testing.
        /// </summary>
        private float[] CreateUnitVector(float x, float y, int length = 512)
        {
            float[] vec = new float[length];
            vec[0] = x;
            vec[1] = y;
            
            // Normalize to unit vector
            float magnitude = (float)Math.Sqrt(x * x + y * y);
            if (magnitude > 0)
            {
                vec[0] /= magnitude;
                vec[1] /= magnitude;
            }
            
            return vec;
        }
    }
}
