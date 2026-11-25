using System;
using System.IO;
using SLSKDONET.Services.Library.Files;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Files;

/// <summary>LF6 (#348): the quality-upgrade trash moves and restores a file.</summary>
public class OrbitTrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orbit-trash-{Guid.NewGuid():N}");

    public OrbitTrashTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void TrashFile_MovesIntoHiddenTrashFolder_AndRecordsOrigin()
    {
        var source = Path.Combine(_root, "Artist", "track.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "mp3");

        var trashed = OrbitTrash.TrashFile(source, _root);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(trashed));
        Assert.True(File.Exists(trashed + OrbitTrash.OriginSuffix));
        Assert.StartsWith(Path.Combine(_root, ".orbit-trash"), trashed);
    }

    [Fact]
    public void Restore_PutsTheFileBackAtItsOriginalPath()
    {
        var source = Path.Combine(_root, "Artist", "track.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "mp3");

        var trashed = OrbitTrash.TrashFile(source, _root);
        var restored = OrbitTrash.Restore(trashed);

        Assert.Equal(source, restored);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(trashed));
        Assert.False(File.Exists(trashed + OrbitTrash.OriginSuffix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetTrashDirectory_BlankDownloadDir_Throws(string? dir)
    {
        Assert.Throws<ArgumentException>(() => OrbitTrash.GetTrashDirectory(dir!));
    }
}
