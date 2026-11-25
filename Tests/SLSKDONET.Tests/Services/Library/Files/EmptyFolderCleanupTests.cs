using System;
using System.IO;
using SLSKDONET.Services.Library.Files;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Files;

/// <summary>LF5 (#347): empty-folder cleanup only removes folders ORBIT emptied, never the root.</summary>
public class EmptyFolderCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orbit-clean-{Guid.NewGuid():N}");

    public EmptyFolderCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void RemoveEmptyParents_RemovesEmptiedChain_StopsAtRoot()
    {
        var nested = Path.Combine(_root, "Artist", "Album");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "track.flac");
        File.WriteAllText(file, "audio");

        File.Delete(file); // the reorganization moved the file out
        var removed = EmptyFolderCleanup.RemoveEmptyParents(file, _root);

        Assert.Equal(2, removed.Count);
        Assert.False(Directory.Exists(nested));
        Assert.False(Directory.Exists(Path.Combine(_root, "Artist")));
        Assert.True(Directory.Exists(_root)); // root is never removed
    }

    [Fact]
    public void RemoveEmptyParents_StopsAtANonEmptyFolder()
    {
        var artist = Path.Combine(_root, "Artist");
        Directory.CreateDirectory(artist);
        var other = Path.Combine(artist, "other.flac");
        File.WriteAllText(other, "keep");

        var album = Path.Combine(artist, "Album");
        Directory.CreateDirectory(album);
        var file = Path.Combine(album, "track.flac");
        File.WriteAllText(file, "audio");
        File.Delete(file);

        var removed = EmptyFolderCleanup.RemoveEmptyParents(file, _root);

        Assert.Single(removed);
        Assert.False(Directory.Exists(album));
        Assert.True(Directory.Exists(artist)); // still has other.flac
    }
}
