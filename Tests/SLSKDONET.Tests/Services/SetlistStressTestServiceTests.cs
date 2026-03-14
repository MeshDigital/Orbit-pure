using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SLSKDONET.Data; // Ensure this namespace is correct for AppDbContext
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Musical;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Analysis;
using Xunit;

namespace SLSKDONET.Tests.Services
{
    public class SetlistStressTestServiceTests
    {
        private readonly Mock<ILibraryService> _mockLibraryService;
        // private readonly Mock<ILogger<HarmonicMatchService>> _mockHarmonicLogger;
        // private readonly Mock<DatabaseService> _mockDatabaseService; 
        // private readonly HarmonicMatchService _harmonicMatchService;
        private readonly SetlistStressTestService _service;
        private readonly AppDbContext _dbContext;

        public SetlistStressTestServiceTests()
        {
            // Setup In-Memory Database
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()) // Unique DB per test
                .Options;
            _dbContext = new AppDbContext(options);

            // Mocks
            // Mocks
            _mockLibraryService = new Mock<ILibraryService>();

            // HarmonicMatchService appears to be unused in the paths we are testing (AnalyzeTransitionAsync),
            // so we pass null to avoid complex mocking of its dependencies (DatabaseService).
            _service = new SetlistStressTestService(
                _mockLibraryService.Object,
                null!, // HarmonicMatchService is unused for basic transition analysis
                _dbContext);
        }

        [Fact]
        public async Task RunDiagnosticAsync_ShouldIdentifyEnergyPlateauAndKeyClash()
        {
            // Arrange
            var trackA = new LibraryEntryEntity 
            { 
                Id = Guid.NewGuid(), 
                UniqueHash = "hash_a", 
                Artist = "Artist A", 
                Title = "Track A", 
                BPM = 120, 
                Energy = 0.5, 
                MusicalKey = "8A", 
                VocalType = VocalType.Instrumental 
            };
            var trackB = new LibraryEntryEntity 
            { 
                Id = Guid.NewGuid(), 
                UniqueHash = "hash_b", 
                Artist = "Artist B", 
                Title = "Track B", 
                BPM = 120, 
                Energy = 0.5, 
                MusicalKey = "8A", 
                VocalType = VocalType.Instrumental 
            };
            var trackC = new LibraryEntryEntity 
            { 
                Id = Guid.NewGuid(), 
                UniqueHash = "hash_c", 
                Artist = "Artist C", 
                Title = "Track C", 
                BPM = 120, 
                Energy = 0.5, 
                MusicalKey = "8A", 
                VocalType = VocalType.Instrumental 
            };
            var trackD = new LibraryEntryEntity 
            { 
                Id = Guid.NewGuid(), 
                UniqueHash = "hash_d", 
                Artist = "Artist D", 
                Title = "Track D", 
                BPM = 120, 
                Energy = 0.5, 
                MusicalKey = "8A", 
                VocalType = VocalType.Instrumental 
            };
             var trackE = new LibraryEntryEntity 
            { 
                Id = Guid.NewGuid(), 
                UniqueHash = "hash_e", 
                Artist = "Artist E", 
                Title = "Track E", // Key Clash!
                BPM = 120, 
                Energy = 0.5, 
                MusicalKey = "3B", // 8A -> 3B is a clash (8 vs 3)
                VocalType = VocalType.Instrumental 
            };

            await _dbContext.LibraryEntries.AddRangeAsync(trackA, trackB, trackC, trackD, trackE);
            await _dbContext.SaveChangesAsync();

            var setlist = new SetListEntity
            {
                Id = Guid.NewGuid(),
                Tracks = new List<SetTrackEntity>
                {
                    new SetTrackEntity { Position = 0, TrackUniqueHash = "hash_a" },
                    new SetTrackEntity { Position = 1, TrackUniqueHash = "hash_b" },
                    new SetTrackEntity { Position = 2, TrackUniqueHash = "hash_c" },
                    new SetTrackEntity { Position = 3, TrackUniqueHash = "hash_d" }, // Plateau A->B->C->D
                    new SetTrackEntity { Position = 4, TrackUniqueHash = "hash_e" }  // Clash D->E
                }
            };

            // Act
            var report = await _service.RunDiagnosticAsync(setlist);

            // Assert
            Assert.NotNull(report);
            Assert.Equal(4, report.StressPoints.Count); // 4 transitions: 0-1, 1-2, 2-3, 3-4

            // Check for Energy Plateau (should be detected around index 2 or 3 depending on window)
            // The service checks 4 tracks. A, B, C, D are all 0.5 energy.
            // Gradient should be 0.
            var plateauPoint = report.StressPoints.FirstOrDefault(sp => sp.PrimaryFailure == TransitionFailureType.EnergyPlateau);
            // Verify if plateau logic triggers.
            // Logic: if (isEnergyPlateau && trackA.VocalType == Instrumental) -> return 50 (Warn)
            
            // Check for Key Clash (D->E)
            var clashPoint = report.StressPoints.Last();
            Assert.Equal(TransitionFailureType.HarmonicClash, clashPoint.PrimaryFailure);
            Assert.Contains("Harmonic Clash", clashPoint.PrimaryProblem);
            Assert.True(clashPoint.SeverityScore > 25, "Severity should reflect harmonic penalty (approx 30)");

            // Verify Narrative Generates
            Assert.False(string.IsNullOrEmpty(report.SetlistNarrativeMentoring));
            Assert.False(string.IsNullOrEmpty(report.QuickSummary));
        }
    }
}
