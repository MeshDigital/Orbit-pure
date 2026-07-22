using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class LibraryViewModelDuplicateDetectionTests
{
    private static LibraryEntry CreateEntry(string artist, double? durationSeconds, double? bpm, string? camelotKey)
        => new()
        {
            Artist = artist,
            DurationSeconds = durationSeconds.HasValue ? (int)durationSeconds.Value : null,
            BPM = bpm,
            CamelotKey = camelotKey
        };

    [Fact]
    public void BuildContentSignature_SameAudioDifferentTags_ProducesSameSignature()
    {
        // Same recording re-imported with a retagged artist casing/spacing and a rounding
        // difference in duration/BPM from a different encoder — should still collapse together.
        var original = CreateEntry("Artist Name", 215.4, 128.02, "8A");
        var retagged = CreateEntry("artist  name", 215.6, 127.97, "8A");

        Assert.Equal(
            LibraryViewModel.BuildContentSignature(original),
            LibraryViewModel.BuildContentSignature(retagged));
    }

    [Fact]
    public void BuildContentSignature_DifferentArtist_ProducesDifferentSignature()
    {
        var trackA = CreateEntry("Artist One", 200, 128, "8A");
        var trackB = CreateEntry("Artist Two", 200, 128, "8A");

        Assert.NotEqual(
            LibraryViewModel.BuildContentSignature(trackA),
            LibraryViewModel.BuildContentSignature(trackB));
    }

    [Fact]
    public void BuildContentSignature_DifferentDuration_ProducesDifferentSignature()
    {
        var shortEdit = CreateEntry("Artist Name", 180, 128, "8A");
        var extendedMix = CreateEntry("Artist Name", 360, 128, "8A");

        Assert.NotEqual(
            LibraryViewModel.BuildContentSignature(shortEdit),
            LibraryViewModel.BuildContentSignature(extendedMix));
    }

    [Fact]
    public void BuildContentSignature_DifferentKey_ProducesDifferentSignature()
    {
        var trackA = CreateEntry("Artist Name", 200, 128, "8A");
        var trackB = CreateEntry("Artist Name", 200, 128, "9A");

        Assert.NotEqual(
            LibraryViewModel.BuildContentSignature(trackA),
            LibraryViewModel.BuildContentSignature(trackB));
    }
}
