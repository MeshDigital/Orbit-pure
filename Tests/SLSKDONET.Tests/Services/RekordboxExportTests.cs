using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Data;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Tests.Services
{
    public class RekordboxExportTests
    {
        [Fact]
        public void MetadataFormatter_FormatTrackMetadata_ShouldIncludeOrbitHeader()
        {
            // Arrange
            double energy = 0.85;
            string notes = "High energy drop";

            // Act
            string result = MetadataFormatter.FormatTrackMetadata(energyLevel: energy, forensicNotes: notes);

            // Assert
            Assert.Contains("[ORBIT]", result);
            Assert.Contains("Energy=0.85", result);
            Assert.Contains("Notes=High energy drop", result);
        }

        [Fact]
        public void MetadataFormatter_FormatTransitionMetadata_ShouldIncludeTransitionDetails()
        {
            // Arrange
            string type = "DropSwap";
            string reason = "Key match and energy lift";

            // Act
            string result = MetadataFormatter.FormatTransitionMetadata(type, reasoning: reason);

            // Assert
            Assert.Contains("[ORBIT TRANSITION]", result);
            Assert.Contains("Type=DropSwap", result);
            Assert.Contains("Reason=Key match and energy lift", result);
        }

        [Fact]
        public void RekordboxExportService_GenerateXml_ShouldProduceValidCollectionNodes()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<RekordboxExportService>>();
            var validator = new ExportValidator();
            var organizer = new ExportPackOrganizer();
            var service = new RekordboxExportService(loggerMock.Object, validator, organizer);

            var tracks = new List<ExportTrack>
            {
                new ExportTrack
                {
                    TrackId = "1",
                    Title = "Test Track",
                    Artist = "Test Artist",
                    FilePath = "C:/Music/test.mp3",
                    Duration = TimeSpan.FromMinutes(3)
                }
            };

            // Act
            string xml = service.GenerateXml(tracks, Enumerable.Empty<ExportPlaylist>());

            // Assert
            Assert.Contains("COLLECTION", xml);
            Assert.Contains("Entries=\"1\"", xml);
            Assert.Contains("Name=\"Test Track\"", xml);
            Assert.Contains("Artist=\"Test Artist\"", xml);
        }

        [Fact]
        public async Task RekordboxExportService_Mapping_ShouldProcessCuesCorrectly()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<RekordboxExportService>>();
            var validator = new ExportValidator();
            var organizer = new ExportPackOrganizer();
            var service = new RekordboxExportService(loggerMock.Object, validator, organizer);

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ORBIT_Test_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            string dummyFile = System.IO.Path.Combine(tempDir, "test.mp3");
            System.IO.File.WriteAllText(dummyFile, "dummy audio content");

            var track = new LibraryEntryEntity
            {
                UniqueHash = "hash123",
                Title = "Cued Track",
                Artist = "Producer",
                FilePath = dummyFile,
                CanonicalDuration = 300,
                BPM = 124,
                MusicalKey = "8A",
                AudioFeatures = new AudioFeaturesEntity { TrackUniqueHash = "hash123" }, // Mandatory for validator
                CuePointsJson = System.Text.Json.JsonSerializer.Serialize(new List<OrbitCue>
                {
                    new OrbitCue { Timestamp = 60, Name = "DROP", Role = CueRole.Drop, SlotIndex = 0 }
                })
            };

            var options = new ExportOptions { CueMode = CueExportMode.Both, ExportStructuralCues = false };

            try
            {
                // Act
                var result = await service.ExportTracksAsync(new[] { track }, tempDir, options);

                // Assert
                if (!result.Success)
                {
                    string errors = string.Join(", ", result.Errors);
                    string warnings = string.Join(", ", result.Warnings);
                    throw new Xunit.Sdk.XunitException($"Export failed: {errors}. Warnings: {warnings}");
                }

                Assert.True(result.Success);
                string xmlContent = System.IO.File.ReadAllText(result.XmlFilePath);
                
                // Verify XML contains the cue
                Assert.Contains("POSITION_MARK", xmlContent);
                Assert.Contains("Name=\"DROP\"", xmlContent);
                Assert.Contains("Type=\"0\"", xmlContent); // HotCue
                Assert.Contains("Type=\"1\"", xmlContent); // MemoryCue
                Assert.Contains("60.000", xmlContent); // Position
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                    System.IO.Directory.Delete(tempDir, true);
            }
        }
        [Fact]
        public void MetadataFormatter_ShouldUseInvariantCulture_ForNumericDecimals()
        {
            // Verify that regardless of system culture, decimals use dots.
            // Arrange
            double energy = 0.85;

            // Act
            string result = MetadataFormatter.FormatTrackMetadata(energyLevel: energy);

            // Assert
            Assert.Contains("Energy=0.85", result); // Not 0,85 or other separators
        }

        [Fact]
        public async Task RekordboxExportService_Mapping_ShouldExtractStructuralSegments()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<RekordboxExportService>>();
            var validator = new ExportValidator();
            var organizer = new ExportPackOrganizer();
            var service = new RekordboxExportService(loggerMock.Object, validator, organizer);

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ORBIT_Test_Segments_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            string dummyFile = System.IO.Path.Combine(tempDir, "test.mp3");
            System.IO.File.WriteAllText(dummyFile, "dummy");

            var track = new LibraryEntryEntity
            {
                UniqueHash = "hash456",
                Title = "Segmented Track",
                Artist = "Producer",
                FilePath = dummyFile,
                BPM = 120,
                AudioFeatures = new AudioFeaturesEntity 
                { 
                    TrackUniqueHash = "hash456",
                    PhraseSegmentsJson = System.Text.Json.JsonSerializer.Serialize(new List<PhraseSegment>
                    {
                        new PhraseSegment { Label = "Intro", Start = 0, Duration = 32 },
                        new PhraseSegment { Label = "Drop", Start = 64, Duration = 64 }
                    })
                }
            };

            var options = new ExportOptions { ExportStructuralCues = true, CueMode = CueExportMode.MemoryCues };

            try
            {
                // Act
                var result = await service.ExportTracksAsync(new[] { track }, tempDir, options);

                // Assert
                Assert.True(result.Success);
                string xmlContent = System.IO.File.ReadAllText(result.XmlFilePath);
                
                Assert.Contains("Name=\"INTRO\"", xmlContent);
                Assert.Contains("Name=\"DROP\"", xmlContent);
                Assert.Contains("64.000", xmlContent);
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                    System.IO.Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task RekordboxExportService_Mapping_ShouldHandlePathNormalization()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<RekordboxExportService>>();
            var service = new RekordboxExportService(loggerMock.Object, new ExportValidator(), new ExportPackOrganizer());

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ORBIT_Test_Path_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            string dummyFile = System.IO.Path.Combine(tempDir, "test space.mp3");
            System.IO.File.WriteAllText(dummyFile, "dummy");

            var track = new LibraryEntryEntity
            {
                UniqueHash = "hash789",
                Title = "Path Track",
                Artist = "Artist",
                FilePath = dummyFile,
                BPM = 120,
                AudioFeatures = new AudioFeaturesEntity { TrackUniqueHash = "hash789" }
            };

            try
            {
                // Act
                var result = await service.ExportTracksAsync(new[] { track }, tempDir, new ExportOptions());

                // Assert
                string xmlContent = System.IO.File.ReadAllText(result.XmlFilePath);
                // Rekordbox requires file://localhost/ format with escaped spaces
                Assert.Contains("Location=\"file://localhost/", xmlContent);
                Assert.Contains("test%20space.mp3\"", xmlContent);
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                    System.IO.Directory.Delete(tempDir, true);
            }
        }
        [Fact]
        public void MetadataFormatter_FormatTrackMetadata_ShouldIncludeEnergyScore()
        {
            // Arrange
            int energyScore = 7;

            // Act
            string result = MetadataFormatter.FormatTrackMetadata(energyScore: energyScore);

            // Assert
            Assert.Contains("EnergyScore=7/10", result);
        }

        [Fact]
        public async Task RekordboxExportService_Mapping_ShouldProcessSegmentedEnergyCorrectly()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<RekordboxExportService>>();
            var validator = new ExportValidator();
            var organizer = new ExportPackOrganizer();
            var service = new RekordboxExportService(loggerMock.Object, validator, organizer);

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ORBIT_Test_Energy_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            string dummyFile = System.IO.Path.Combine(tempDir, "energy_test.mp3");
            System.IO.File.WriteAllText(dummyFile, "dummy");

            var track = new LibraryEntryEntity
            {
                UniqueHash = "hash_energy",
                Title = "Energy Track",
                Artist = "Producer",
                FilePath = dummyFile,
                CanonicalDuration = 100, // 100 seconds
                BPM = 120,
                AudioFeatures = new AudioFeaturesEntity 
                { 
                    TrackUniqueHash = "hash_energy",
                    EnergyScore = 8,
                    SegmentedEnergyJson = System.Text.Json.JsonSerializer.Serialize(new List<int> { 2, 5, 9 }) // 3 points
                }
            };

            var options = new ExportOptions { IncludeForensicNotes = true, ExportStructuralCues = true, CueMode = CueExportMode.MemoryCues };

            try
            {
                // Act
                var result = await service.ExportTracksAsync(new[] { track }, tempDir, options);

                // Assert
                Assert.True(result.Success);
                string xmlContent = System.IO.File.ReadAllText(result.XmlFilePath);
                
                // Check Metadata
                Assert.Contains("EnergyScore=8/10", xmlContent);
                
                // Check E1-E3 Cues
                Assert.Contains("Name=\"E1\"", xmlContent);
                Assert.Contains("Name=\"E2\"", xmlContent);
                Assert.Contains("Name=\"E3\"", xmlContent);
                
                // Check Position spacing (100 / 3 = ~33.33)
                Assert.Contains("Start=\"0.000\"", xmlContent); // E1
                Assert.Contains("Start=\"33.333\"", xmlContent); // E2
                Assert.Contains("Start=\"66.667\"", xmlContent); // E3

                // Check Colors (E3 = Energy 9 = Red)
                // Red = 255, 0, 0
                Assert.Contains("Name=\"E3\"", xmlContent);
                Assert.Contains("Red=\"255\"", xmlContent);
                Assert.Contains("Green=\"0\"", xmlContent);
                Assert.Contains("Blue=\"0\"", xmlContent);
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                    System.IO.Directory.Delete(tempDir, true);
            }
        }
    }
}

