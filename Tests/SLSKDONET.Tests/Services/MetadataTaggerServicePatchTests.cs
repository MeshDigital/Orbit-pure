using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using SLSKDONET.Tests.Helpers;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// #211 — <see cref="ITaggerService.ApplyTagPatchAsync"/> must set and clear every
/// <see cref="TagField"/>, capture the previous values for undo, and return a failure (not throw)
/// for a corrupt file. Uses the same minimal-WAV fixture + <see cref="FakeFileWriteService"/> as
/// <see cref="MetadataTaggerServiceTests"/>.
/// </summary>
public class MetadataTaggerServicePatchTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_TaggerPatchTest_{Guid.NewGuid():N}");

    public MetadataTaggerServicePatchTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static MetadataTaggerService CreateService() =>
        new(NullLogger<MetadataTaggerService>.Instance, new FakeFileWriteService());

    private string CreateWav(string name)
    {
        var path = Path.Combine(_tempDir, name);
        WriteMinimalWav(path);
        return path;
    }

    private static void WriteMinimalWav(string path)
    {
        const int sampleRate = 8000;
        var samples = new short[sampleRate / 10];
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataSize = samples.Length * 2;

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        foreach (var s in samples) bw.Write(s);
    }

    [Fact]
    public async Task ApplyTagPatch_SetsFields_AndCapturesPreviousValues()
    {
        var path = CreateWav("set.wav");
        var service = CreateService();

        var result = await service.ApplyTagPatchAsync(path, new TagPatch(new[]
        {
            new TagPatchField(TagField.Title, "New Title"),
            new TagPatchField(TagField.Artist, "New Artist"),
            new TagPatchField(TagField.Album, "New Album"),
            new TagPatchField(TagField.Genre, "Drum & Bass"),
            new TagPatchField(TagField.Label, "New Label"),
        }));

        Assert.True(result.Success, result.Error);
        Assert.Equal(5, result.PreviousValues.Count);
        Assert.Null(result.PreviousValues[TagField.Title]); // was empty

        using var tagged = TagLib.File.Create(path);
        Assert.Equal("New Title", tagged.Tag.Title);
        Assert.Equal("New Artist", tagged.Tag.FirstPerformer);
        Assert.Equal("New Album", tagged.Tag.Album);
        Assert.Equal("Drum & Bass", tagged.Tag.FirstGenre);
        Assert.Equal("New Label", tagged.Tag.Publisher);
    }

    [Fact]
    public async Task ApplyTagPatch_ClearsFields_AndReportsPreviousValues()
    {
        var path = CreateWav("clear.wav");
        var service = CreateService();
        await service.ApplyTagPatchAsync(path, new TagPatch(new[]
        {
            new TagPatchField(TagField.Artist, "Calibre"),
            new TagPatchField(TagField.Genre, "DnB"),
        }));

        var cleared = await service.ApplyTagPatchAsync(path, new TagPatch(new[]
        {
            new TagPatchField(TagField.Artist, null),
            new TagPatchField(TagField.Genre, null),
        }));

        Assert.True(cleared.Success, cleared.Error);
        Assert.Equal("Calibre", cleared.PreviousValues[TagField.Artist]);
        Assert.Equal("DnB", cleared.PreviousValues[TagField.Genre]);

        using var tagged = TagLib.File.Create(path);
        Assert.Null(tagged.Tag.FirstPerformer);
        Assert.Null(tagged.Tag.FirstGenre);
    }

    [Fact]
    public async Task ApplyTagPatch_CorruptFile_ReturnsFailureNotThrow()
    {
        var path = Path.Combine(_tempDir, "corrupt.wav");
        File.WriteAllText(path, "this is not audio data at all");

        var result = await CreateService().ApplyTagPatchAsync(path, new TagPatch(new[]
        {
            new TagPatchField(TagField.Title, "X"),
        }));

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}
