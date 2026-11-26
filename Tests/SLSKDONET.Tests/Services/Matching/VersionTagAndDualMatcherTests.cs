using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Services.Matching;
using Xunit;

namespace SLSKDONET.Tests.Services.Matching;

/// <summary>
/// Shared version-gate and edit-length tests verifying BOTH SearchResultMatcher and strict-mode
/// MatchScorer produce consistent verdicts (Epic #543 / #547 SQ3).
/// </summary>
public sealed class VersionTagAndDualMatcherTests
{
    [Fact]
    public void PlainRequest_PenalisesRemixInBothNormalAndStrictMatchers()
    {
        var request = new PlaylistTrack { Artist = "Frannabik", Title = "Down", CanonicalDuration = 240_000 };
        var original = new Track { Artist = "Frannabik", Title = "Down", Filename = "Frannabik - Down.flac", Format = "flac", Bitrate = 950, Length = 240, Size = 28_000_000 };
        var remix = new Track { Artist = "Frannabik", Title = "Down (Bad Legs Remix)", Filename = "Frannabik - Down (Bad Legs Remix).flac", Format = "flac", Bitrate = 950, Length = 240, Size = 28_000_000 };

        // Normal matcher
        var matcher = new SearchResultMatcher(NullLogger<SearchResultMatcher>.Instance, new AppConfig());
        var (normalBest, _) = matcher.FindBestMatchWithDiagnostics(request, new[] { remix, original });
        Assert.NotNull(normalBest);
        Assert.Equal(original.Filename, normalBest.Filename);
        Assert.Null(matcher.FindBestMatchWithDiagnostics(request, new[] { remix }).BestMatch);

        // Strict-mode MatchScorer
        var originalScore = MatchScorer.ScoreCandidate(request, original);
        var remixScore = MatchScorer.ScoreCandidate(request, remix);
        Assert.True(originalScore - remixScore >= 40, $"Expected strict scorer to penalise remix heavily (orig={originalScore}, remix={remixScore})");
    }

    [Fact]
    public void ExtendedRequest_PenalisesRadioEditBelowFullCandidate_WithOrWithoutDuration()
    {
        var reqNoDuration = new PlaylistTrack { Artist = "Bicep", Title = "Glue (Extended Mix)" };
        var extended = new Track { Artist = "Bicep", Title = "Glue (Extended Mix)", Filename = "Bicep - Glue (Extended Mix).flac", Format = "flac", Bitrate = 950, Length = 380, Size = 42_000_000 };
        var radio = new Track { Artist = "Bicep", Title = "Glue (Radio Edit)", Filename = "Bicep - Glue (Radio Edit).flac", Format = "flac", Bitrate = 950, Length = 210, Size = 24_000_000 };

        var verdictRadio = VersionTag.Evaluate(reqNoDuration.Title, "Bicep - Glue (Radio Edit)");
        Assert.Equal(-VersionTag.ShortForFullRequestPenalty, verdictRadio.ScoreDelta);

        var matcher = new SearchResultMatcher(NullLogger<SearchResultMatcher>.Instance, new AppConfig());
        var (best, _) = matcher.FindBestMatchWithDiagnostics(reqNoDuration, new[] { radio, extended });
        Assert.NotNull(best);
        Assert.Equal(extended.Filename, best.Filename);

        // Strict-mode MatchScorer also prefers Extended over Radio Edit
        var extScore = MatchScorer.ScoreCandidate(reqNoDuration, extended);
        var radioScore = MatchScorer.ScoreCandidate(reqNoDuration, radio);
        Assert.True(extScore > radioScore + 25, $"Expected Extended ({extScore}) >> Radio Edit ({radioScore})");
    }

    [Fact]
    public void UnlabelledRequest_PrefersFullOverUnlabelledOverRadioEdit_AsTieBreak()
    {
        var fullVerdict = VersionTag.Evaluate("Glue", "Bicep - Glue (Extended Mix)");
        var plainVerdict = VersionTag.Evaluate("Glue", "Bicep - Glue");
        var radioVerdict = VersionTag.Evaluate("Glue", "Bicep - Glue (Radio Edit)");

        Assert.True(fullVerdict.ScoreDelta > plainVerdict.ScoreDelta);
        Assert.True(plainVerdict.ScoreDelta > radioVerdict.ScoreDelta);
        Assert.False(radioVerdict.IsDerivativeMismatch);
    }

    [Fact]
    public void RadioEditRequest_PrefersRadioEditOverExtended()
    {
        var radioVerdict = VersionTag.Evaluate("Glue (Radio Edit)", "Bicep - Glue (Radio Edit)");
        var extVerdict = VersionTag.Evaluate("Glue (Radio Edit)", "Bicep - Glue (Extended Mix)");

        Assert.True(radioVerdict.ScoreDelta > extVerdict.ScoreDelta);
    }
}
