using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class StyleClassifierTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<StyleClassifierService>> _loggerMock;
    private readonly Mock<PersonalClassifierService> _personalClassifierMock;
    private readonly StyleClassifierService _service;

    public StyleClassifierTests()
    {
        // Use SQLite In-Memory for more faithful DB behavior
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        
        _context = new AppDbContext(options);
        _context.Database.EnsureCreated(); // Critical for SQLite in-memory

        var dbFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(_context);
        dbFactoryMock.Setup(f => f.CreateDbContextAsync(default)).ReturnsAsync(_context);

        _loggerMock = new Mock<ILogger<StyleClassifierService>>();
        
        // We need a real-ish PersonalClassifierService or mock it heavily
        var dbServiceMock = new Mock<DatabaseService>(null!, null!, null!, null!); // Mock DatabaseService
        _personalClassifierMock = new Mock<PersonalClassifierService>(dbServiceMock.Object);

        _service = new StyleClassifierService(dbFactoryMock.Object, _loggerMock.Object, _personalClassifierMock.Object);
    }

    [Fact]
    public async Task PredictAsync_ReturnsPrediction_WhenFeaturesAreValid()
    {
        // Arrange
        var embedding = new float[512];
        var features = new AudioFeaturesEntity
        {
            TrackUniqueHash = "test_hash",
            VectorEmbedding = embedding
        };

        var styleDef = new StyleDefinitionEntity
        {
            Name = "Neurofunk",
            ColorHex = "#FF0000"
        };
        styleDef.Centroid = new List<float> { 0.1f, 0.2f, 0.3f };
        
        _context.StyleDefinitions.Add(styleDef);
        await _context.SaveChangesAsync();

        _personalClassifierMock.Setup(pc => pc.Predict(It.IsAny<float[]>()))
            .Returns(("Neurofunk", 0.95f));

        // Act
        var result = await _service.PredictAsync(features);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Neurofunk", result.StyleName);
        Assert.Equal(0.95f, result.Confidence);
        Assert.Equal("#FF0000", result.ColorHex);
    }

    [Fact]
    public async Task PredictAsync_ReturnsDefault_WhenNoStylesDefined()
    {
        // Arrange
        var embedding = new float[512];
        var features = new AudioFeaturesEntity
        {
            TrackUniqueHash = "test_hash",
            VectorEmbedding = embedding
        };

        _personalClassifierMock.Setup(pc => pc.Predict(It.IsAny<float[]>()))
            .Returns(("Unknown", 0f));

        // Act
        var result = await _service.PredictAsync(features);

        // Assert
        // Service returns "Unknown (Bad Embedding)" if embedding is null/empty,
        // but here embedding is valid 512 array, so it gets to P.P. and returns "Unknown"
        Assert.Equal("Unknown", result.StyleName);
        Assert.Equal(0, result.Confidence);
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
