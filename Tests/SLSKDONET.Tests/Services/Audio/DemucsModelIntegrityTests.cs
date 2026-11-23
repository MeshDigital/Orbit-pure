using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SLSKDONET.Services.Audio.Separation;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>#319: the Demucs model is checked against a pinned SHA-256 before it is ever loaded.</summary>
public sealed class DemucsModelIntegrityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!, "demucs-" + Guid.NewGuid().ToString("N"));

    public DemucsModelIntegrityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* intentional: best-effort cleanup of the isolated test folder */ }
    }

    private string Model(byte[] bytes)
    {
        var path = Path.Combine(_dir, DemucsModelManager.ModelFileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Missing_Pinned_Mismatch_AndUnpinned()
    {
        var path = Path.Combine(_dir, DemucsModelManager.ModelFileName);
        Assert.Equal(DemucsModelIntegrity.Missing, new DemucsModelManager(path).CheckIntegrity());
        Assert.Contains("isn't installed", new DemucsModelManager(path).UnusableReason());

        var good = new byte[] { 1, 2, 3, 4 };
        Model(good);
        var goodHash = Convert.ToHexString(SHA256.HashData(good));

        var pinned = new DemucsModelManager(path, goodHash.ToLowerInvariant());
        Assert.Equal(DemucsModelIntegrity.Verified, pinned.CheckIntegrity());
        Assert.True(pinned.IsUsable);
        Assert.Null(pinned.UnusableReason());
        Assert.Equal("demucs-4s-" + goodHash[..8].ToLowerInvariant(), pinned.ModelTag);

        var tampered = new DemucsModelManager(path, Convert.ToHexString(SHA256.HashData(new byte[] { 9 })));
        Assert.Equal(DemucsModelIntegrity.Mismatch, tampered.CheckIntegrity());
        Assert.False(tampered.IsUsable);
        Assert.Contains("doesn't match", tampered.UnusableReason());

        // With the shipped pin: nothing published yet → Unpinned; once a hash is pinned, this test file is a mismatch.
        var expected = DemucsModelManager.PinnedSha256 is null ? DemucsModelIntegrity.Unpinned : DemucsModelIntegrity.Mismatch;
        Assert.Equal(expected, new DemucsModelManager(path).CheckIntegrity());
    }

    [Fact]
    public async Task Separator_RefusesAMismatchedModel_BeforeLoadingIt()
    {
        var path = Model(new byte[] { 7, 7, 7 });
        var separator = new DemucsOnnxSeparator(new DemucsModelManager(path, new string('0', 64)), null);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => separator.SeparateAsync(path, _dir));
        Assert.True(error is FileNotFoundException or InvalidOperationException);
        Assert.False(error.Message.Contains("releases/download", StringComparison.Ordinal)); // the dead 404 link is gone
    }

    [Fact]
    public void ChangedFile_IsRehashed()
    {
        var path = Model(new byte[] { 1 });
        var manager = new DemucsModelManager(path);
        var first = manager.Sha256;
        File.WriteAllBytes(path, new byte[] { 2, 2 });
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(first, manager.Sha256);
    }
}
