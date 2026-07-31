using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>
/// Tests that the Rekordbox XML &lt;PLAYLISTS&gt; tree mirrors the real playlist-folder
/// hierarchy when a folderId is supplied, and degrades gracefully (flat under ROOT) when
/// no folder is supplied, or the folder chain is missing/cyclic.
/// </summary>
public class PlaylistExportFolderNestingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportFolders_{Guid.NewGuid():N}");

    public PlaylistExportFolderNestingTests() => Directory.CreateDirectory(_tempDir);

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

    private static DbContextOptions<AppDbContext> CreateInMemoryOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ExportFoldersTest_{Guid.NewGuid():N}")
            .Options;

    private static PlaylistExportService CreateService(DbContextOptions<AppDbContext> options) =>
        new(NullLogger<PlaylistExportService>.Instance, new TestDbContextFactory(options));

    private List<PlaylistTrack> SingleTrack(string title = "T")
    {
        var file = CreateAudioFile($"{title}.mp3");
        return new List<PlaylistTrack> { new() { Id = Guid.NewGuid(), Title = title, Artist = "A", ResolvedFilePath = file } };
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_NoFolderId_WritesFlatNodeUnderRoot()
    {
        var options = CreateInMemoryOptions();
        var outputXml = Path.Combine(_tempDir, "out.xml");

        await CreateService(options).ExportToRekordboxXmlAsync("My Playlist", SingleTrack(), outputXml, folderId: null);

        var doc = XDocument.Load(outputXml);
        var root = doc.Descendants("PLAYLISTS").Single().Element("NODE")!;
        Assert.Equal("ROOT", root.Attribute("Name")?.Value);

        var children = root.Elements("NODE").ToList();
        Assert.Single(children);
        Assert.Equal("My Playlist", children[0].Attribute("Name")?.Value);
        Assert.Equal("1", children[0].Attribute("Type")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_SingleParentFolder_WritesOneNestedFolderNode()
    {
        var options = CreateInMemoryOptions();
        var folderId = Guid.NewGuid();

        await using (var db = new AppDbContext(options))
        {
            db.PlaylistFolders.Add(new PlaylistFolderEntity { Id = folderId, Name = "Techno", ParentFolderId = null });
            await db.SaveChangesAsync();
        }

        var outputXml = Path.Combine(_tempDir, "out.xml");
        await CreateService(options).ExportToRekordboxXmlAsync("My Playlist", SingleTrack(), outputXml, folderId: folderId);

        var doc = XDocument.Load(outputXml);
        var root = doc.Descendants("PLAYLISTS").Single().Element("NODE")!;
        var folderNode = root.Elements("NODE").Single();
        Assert.Equal("Techno", folderNode.Attribute("Name")?.Value);
        Assert.Equal("0", folderNode.Attribute("Type")?.Value);

        var playlistNode = folderNode.Elements("NODE").Single();
        Assert.Equal("My Playlist", playlistNode.Attribute("Name")?.Value);
        Assert.Equal("1", playlistNode.Attribute("Type")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_ThreeLevelFolderChain_WritesThreeNestedNodesInOrder()
    {
        var options = CreateInMemoryOptions();
        var grandparentId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var leafId = Guid.NewGuid();

        await using (var db = new AppDbContext(options))
        {
            db.PlaylistFolders.Add(new PlaylistFolderEntity { Id = grandparentId, Name = "Electronic", ParentFolderId = null });
            db.PlaylistFolders.Add(new PlaylistFolderEntity { Id = parentId, Name = "Techno", ParentFolderId = grandparentId });
            db.PlaylistFolders.Add(new PlaylistFolderEntity { Id = leafId, Name = "Peak Time", ParentFolderId = parentId });
            await db.SaveChangesAsync();
        }

        var outputXml = Path.Combine(_tempDir, "out.xml");
        await CreateService(options).ExportToRekordboxXmlAsync("My Playlist", SingleTrack(), outputXml, folderId: leafId);

        var doc = XDocument.Load(outputXml);
        var root = doc.Descendants("PLAYLISTS").Single().Element("NODE")!;
        var electronic = root.Elements("NODE").Single();
        Assert.Equal("Electronic", electronic.Attribute("Name")?.Value);

        var techno = electronic.Elements("NODE").Single();
        Assert.Equal("Techno", techno.Attribute("Name")?.Value);

        var peakTime = techno.Elements("NODE").Single();
        Assert.Equal("Peak Time", peakTime.Attribute("Name")?.Value);

        var playlistNode = peakTime.Elements("NODE").Single();
        Assert.Equal("My Playlist", playlistNode.Attribute("Name")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_OrphanedFolderId_FallsBackGracefully()
    {
        var options = CreateInMemoryOptions();
        var danglingFolderId = Guid.NewGuid(); // never inserted into PlaylistFolders

        var outputXml = Path.Combine(_tempDir, "out.xml");

        // Must not throw.
        await CreateService(options).ExportToRekordboxXmlAsync("My Playlist", SingleTrack(), outputXml, folderId: danglingFolderId);

        var doc = XDocument.Load(outputXml);
        var root = doc.Descendants("PLAYLISTS").Single().Element("NODE")!;
        Assert.Equal("ROOT", root.Attribute("Name")?.Value);
        var children = root.Elements("NODE").ToList();
        Assert.Single(children);
        Assert.Equal("My Playlist", children[0].Attribute("Name")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_CyclicFolderChain_DoesNotHang()
    {
        var options = CreateInMemoryOptions();
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        await using (var db = new AppDbContext(options))
        {
            // A's parent is B, B's parent is A — a pathological cycle that shouldn't exist
            // today but must not hang or stack-overflow the exporter.
            db.PlaylistFolders.Add(new PlaylistFolderEntity { Id = idA, Name = "A", ParentFolderId = idB });
            db.PlaylistFolders.Add(new PlaylistFolderEntity { Id = idB, Name = "B", ParentFolderId = idA });
            await db.SaveChangesAsync();
        }

        var outputXml = Path.Combine(_tempDir, "out.xml");

        var task = CreateService(options).ExportToRekordboxXmlAsync("My Playlist", SingleTrack(), outputXml, folderId: idA);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(task, completed); // didn't time out / hang
        Assert.True(File.Exists(outputXml));
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
