using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>
/// Tests that the Rekordbox XML "Location" attribute is a correctly percent-encoded
/// file:// URI — spaces/special characters are escaped, but a Windows drive-letter
/// colon (e.g. "C:") is preserved verbatim rather than corrupted to "%3A".
/// </summary>
public class PlaylistExportLocationUriTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportUri_{Guid.NewGuid():N}");

    public PlaylistExportLocationUriTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateAudioFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "fake audio");
        return path;
    }

    private static IDbContextFactory<AppDbContext> CreateInMemoryFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ExportUriTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private PlaylistExportService CreateService() =>
        new(NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory());

    private async Task<string> ExportSingleTrackAndGetLocationAsync(string filePath)
    {
        var outputXml = Path.Combine(_tempDir, $"out_{Guid.NewGuid():N}.xml");
        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = filePath },
        };

        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        var location = doc.Descendants("TRACK")
            .Where(e => e.Attribute("Location") != null)
            .Select(e => e.Attribute("Location")!.Value)
            .Single();
        return location;
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_PathWithSpaces_PercentEncodesSpaces()
    {
        var file = CreateAudioFile("Artist - Track Name.mp3");

        var location = await ExportSingleTrackAndGetLocationAsync(file);

        Assert.Contains("%20", location);
        Assert.DoesNotContain(" ", location);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_WindowsDrivePath_PreservesDriveLetterColon()
    {
        var file = CreateAudioFile("track.mp3");
        // The temp dir on this (Windows) test machine is already an absolute drive-lettered path.
        var driveSegment = Path.GetPathRoot(file)!.TrimEnd('\\', '/'); // e.g. "C:"

        var location = await ExportSingleTrackAndGetLocationAsync(file);

        Assert.Contains(driveSegment, location);
        Assert.DoesNotContain("%3A", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_PathWithSpecialCharacters_EncodesCorrectly()
    {
        var file = CreateAudioFile("Track (Remix) #1 & More.mp3");

        var location = await ExportSingleTrackAndGetLocationAsync(file);

        Assert.Contains("%28", location); // (
        Assert.Contains("%29", location); // )
        Assert.Contains("%23", location); // #
        Assert.Contains("%26", location); // &
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_LocationRoundTripsThroughUriParsing()
    {
        var file = CreateAudioFile("Round Trip Test (Final) #2.mp3");

        var location = await ExportSingleTrackAndGetLocationAsync(file);

        // file://localhost/... deliberately keeps the "localhost" authority (Rekordbox's own
        // convention), so Uri.LocalPath treats it as a UNC host prefix ("\\localhost\C:\...") —
        // strip that prefix before comparing, rather than expecting a bare local path.
        var parsed = new Uri(location).LocalPath;
        const string localhostPrefix = @"\\localhost\";
        if (parsed.StartsWith(localhostPrefix, StringComparison.OrdinalIgnoreCase))
            parsed = parsed[localhostPrefix.Length..];

        Assert.Equal(
            file.Replace('\\', '/'),
            parsed.Replace('\\', '/'),
            ignoreCase: true);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
