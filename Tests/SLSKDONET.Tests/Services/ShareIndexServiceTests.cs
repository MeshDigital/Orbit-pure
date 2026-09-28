using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// What ORBIT shares on Soulseek (audited 2026-09-29 after a peer's leech check flagged the account).
/// </summary>
public class ShareIndexServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orbit-share-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string File(string relative, int bytes = 10)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private ShareIndexService Service(string sharedFolder, string? downloadFolder = null) =>
        new(new AppConfig { SharedFolderPath = sharedFolder, DownloadDirectory = downloadFolder }, null, NullLogger<ShareIndexService>.Instance);

    [Fact]
    public void SharesMusicOnly_NotPartialDownloadsArtworkOrEmptyFiles()
    {
        File(@"Music\Artist\Track.flac");
        File(@"Music\Artist\Track2.mp3");
        File(@"Music\Artist\cover.jpg");
        File(@"Music\Artist\Downloading.flac.part");
        File(@"Music\Artist\notes.txt");
        File(@"Music\Artist\Empty.flac", bytes: 0);

        var index = Service(Path.Combine(_root, "Music"));
        index.EnsureFresh();

        Assert.Equal(2, index.FileCount);
        var names = index.GetAllDirectories().SelectMany(d => d.Files).Select(f => f.Filename).ToList();
        Assert.Contains("Track.flac", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".part") || n.EndsWith(".jpg") || n.EndsWith(".txt"));
    }

    [Fact]
    public void VirtualPaths_StartAtTheShareName_AndNeverExposeTheLocalPath()
    {
        var track = File(@"Private\Users\someone\Music\Artist\Track.flac");
        var index = Service(Path.Combine(_root, @"Private\Users\someone\Music"));

        var dirs = index.GetAllDirectories();

        var dir = Assert.Single(dirs);
        Assert.Equal(@"Music\Artist", dir.Name);
        Assert.DoesNotContain(dirs, d => d.Name.Contains("someone") || d.Name.Contains(':'));
        Assert.True(index.TryGetEntry(@"Music\Artist\Track.flac", out var entry));
        Assert.Equal(track, entry!.LocalPath);
    }

    [Fact]
    public void Search_MatchesTheVirtualPath_NotTheHiddenLocalFolders()
    {
        File(@"someone\Music\Metrik\Gravity.flac");
        var index = Service(Path.Combine(_root, @"someone\Music"));

        Assert.Single(index.Search(new Soulseek.SearchQuery("metrik gravity")));
        Assert.Empty(index.Search(new Soulseek.SearchQuery("someone")));
    }

    [Fact]
    public void FolderCount_IsTheFoldersPeersSee_NotTheNumberOfRoots()
    {
        File(@"Music\A\1.flac");
        File(@"Music\A\2.flac");
        File(@"Music\B\1.flac");
        File(@"Music\B\Sub\1.flac");
        var index = Service(Path.Combine(_root, "Music"));
        index.EnsureFresh();

        Assert.Equal(4, index.FileCount);
        Assert.Equal(3, index.DirectoryCount); // Music\A, Music\B, Music\B\Sub
    }

    [Fact]
    public void NestedRoot_IsIndexedOnce()
    {
        File(@"Music\Downloads\New.flac");
        var index = Service(Path.Combine(_root, "Music"), downloadFolder: Path.Combine(_root, @"Music\Downloads"));
        index.EnsureFresh();

        Assert.Equal(1, index.FileCount);
    }

    [Fact]
    public void Aliases_AreUniqueWhenRootsShareAName()
    {
        var aliases = ShareIndexService.AssignAliases(new[] { @"C:\a\Music", @"D:\Music\", @"C:\a\Music\Sub", @"E:\Other" });

        Assert.Equal(3, aliases.Count); // C:\a\Music\Sub is inside C:\a\Music
        Assert.Equal(new[] { "Music", "Music (2)", "Other" }, aliases.Select(a => a.Alias).OrderBy(a => a));
    }

    [Fact]
    public void CountsChanged_FiresOnlyWhenTheCountsChange()
    {
        File(@"Music\A\1.flac");
        var index = Service(Path.Combine(_root, "Music"));
        int raised = 0;
        index.CountsChanged += (_, _) => raised++;

        index.EnsureFresh();
        index.Invalidate();
        index.EnsureFresh();          // same files → no event
        File(@"Music\A\2.flac");
        index.Invalidate();
        index.EnsureFresh();          // one more → event

        Assert.Equal(2, raised);
        Assert.Equal(2, index.FileCount);
    }
}
