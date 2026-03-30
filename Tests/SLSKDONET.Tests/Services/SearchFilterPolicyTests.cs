using System.Collections.Generic;
using Soulseek;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class SearchFilterPolicyTests
{
    private static Soulseek.File MakeFile(string filename, int? bitrate = null)
    {
        var attrs = new List<FileAttribute>();
        if (bitrate.HasValue)
        {
            attrs.Add(new FileAttribute(FileAttributeType.BitRate, bitrate.Value));
        }

        return new Soulseek.File(1, filename, 1024, System.IO.Path.GetExtension(filename), attrs.Count > 0 ? attrs : null);
    }

    /// <summary>
    /// Files shared by Soulseek peers that lack bitrate metadata arrive with no BitRate
    /// attribute. Previously these were rejected because rawBitrate (0) was compared to
    /// bitrateFilter.Min (320), producing 0 &lt; 320 = true (rejected). This test ensures
    /// that files without bitrate information are allowed through so users see search results.
    /// </summary>
    [Fact]
    public void EvaluateFile_ShouldAccept_FilesWithNoBitrateAttribute()
    {
        var file = MakeFile("track.mp3"); // No bitrate attribute

        var decision = SearchFilterPolicy.EvaluateFile(
            file,
            formatSet: new HashSet<string> { "mp3", "flac" },
            bitrateFilter: (320, null),
            preferredMaxSampleRate: 0,
            excludedPhrases: new List<string>());

        Assert.True(decision.IsAccepted,
            "A file without a bitrate attribute should not be rejected by the bitrate filter.");
    }

    [Fact]
    public void EvaluateFile_ShouldReject_FilesWithKnownLowBitrate()
    {
        var file = MakeFile("track.mp3", bitrate: 128); // Known low bitrate

        var decision = SearchFilterPolicy.EvaluateFile(
            file,
            formatSet: new HashSet<string> { "mp3", "flac" },
            bitrateFilter: (320, null),
            preferredMaxSampleRate: 0,
            excludedPhrases: new List<string>());

        Assert.False(decision.IsAccepted);
        Assert.Equal(SearchRejectionReason.Bitrate, decision.Reason);
    }

    [Fact]
    public void EvaluateFile_ShouldAccept_FilesWithSufficientBitrate()
    {
        var file = MakeFile("track.mp3", bitrate: 320);

        var decision = SearchFilterPolicy.EvaluateFile(
            file,
            formatSet: new HashSet<string> { "mp3", "flac" },
            bitrateFilter: (320, null),
            preferredMaxSampleRate: 0,
            excludedPhrases: new List<string>());

        Assert.True(decision.IsAccepted);
    }

    [Fact]
    public void EvaluateFile_ShouldReject_FilesWithWrongFormat()
    {
        var file = MakeFile("track.ogg");

        var decision = SearchFilterPolicy.EvaluateFile(
            file,
            formatSet: new HashSet<string> { "mp3", "flac" },
            bitrateFilter: (null, null),
            preferredMaxSampleRate: 0,
            excludedPhrases: new List<string>());

        Assert.False(decision.IsAccepted);
        Assert.Equal(SearchRejectionReason.Format, decision.Reason);
    }
}
