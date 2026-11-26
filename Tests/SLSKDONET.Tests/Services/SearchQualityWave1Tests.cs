using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AutoDownload;
using Soulseek;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Regression tests for Epic #543 Wave 1 (#544 SQ0 + #545 SQ1):
/// Unicode-safe fuzzy normaliser, excluded phrases candidate filter, one lossless floor (>400 kbps),
/// and one shared SearchQualityProfiles definition.
/// </summary>
public sealed class SearchQualityWave1Tests
{
    [Fact]
    public void NormalizeFuzzy_FoldsDiacritics_PreservesNonLatinScripts_AndCollapsesInitialisms()
    {
        Assert.Equal("tiesto", SearchResultMatcher.NormalizeFuzzy("Tiësto"));
        Assert.Equal("rufus du sol", SearchResultMatcher.NormalizeFuzzy("RÜFÜS DU SOL"));
        Assert.Equal("amc", SearchResultMatcher.NormalizeFuzzy("A.M.C"));
        Assert.Equal("東京ナイト", SearchResultMatcher.NormalizeFuzzy("東京ナイト"));
        Assert.Equal("кино группа крови", SearchResultMatcher.NormalizeFuzzy("Кино - Группа крови"));
    }

    [Fact]
    public void Matcher_DifferentJapaneseTitlesDoNotScore1_SameJapaneseTitleMatches()
    {
        var config = new AppConfig { EnableFuzzyNormalization = true };
        var matcher = new SearchResultMatcher(NullLogger<SearchResultMatcher>.Instance, config);

        // Previously both normalized to "" and hit exp == act -> 1.0!
        Assert.Equal(0.0, matcher.CalculateSimilarity("初恋", "花束を君に"));
        Assert.Equal(1.0, matcher.CalculateSimilarity("初恋", "初恋"));
        Assert.Equal(1.0, matcher.CalculateSimilarity("Tiësto", "Tiesto"));
        Assert.Equal(0.0, matcher.CalculateSimilarity("!!!", "???"));

        var request = new PlaylistTrack { Artist = "宇多田ヒカル", Title = "初恋", CanonicalDuration = 240_000 };
        var wrongCandidate = new Track { Artist = "宇多田ヒカル", Title = "花束を君に", Filename = "宇多田ヒカル - 花束を君に.flac", Format = "flac", Bitrate = 900, Length = 240, Size = 28_000_000 };
        var rightCandidate = new Track { Artist = "宇多田ヒカル", Title = "初恋", Filename = "宇多田ヒカル - 初恋.flac", Format = "flac", Bitrate = 900, Length = 240, Size = 28_000_000 };

        var (bestRight, _) = matcher.FindBestMatchWithDiagnostics(request, new[] { wrongCandidate, rightCandidate });
        Assert.NotNull(bestRight);
        Assert.Equal(rightCandidate.Filename, bestRight.Filename);

        // Diacritic folding end-to-end: Tiësto matches Tiesto
        var tiestoReq = new PlaylistTrack { Artist = "Tiësto", Title = "Adagio For Strings", CanonicalDuration = 210_000 };
        var tiestoCand = new Track { Artist = "Tiesto", Title = "Adagio For Strings", Filename = "Tiesto - Adagio For Strings.flac", Format = "flac", Bitrate = 950, Length = 210, Size = 25_000_000 };
        var (bestTiesto, _) = matcher.FindBestMatchWithDiagnostics(tiestoReq, new[] { tiestoCand });
        Assert.NotNull(bestTiesto);
    }

    [Fact]
    public void ExcludedPhrases_RejectsUnrequestedRemix_AllowsExplicitlyRequestedRemix()
    {
        var rejected = SoulseekSearchHelper.FindExcludedPhraseInCandidate(
            "Sub Focus - Solar System (VIP Remix).flac",
            "Sub Focus Solar System",
            "remix,cover,live,acoustic");
        Assert.Equal("remix", rejected);

        var allowed = SoulseekSearchHelper.FindExcludedPhraseInCandidate(
            "Sub Focus - Solar System (VIP Remix).flac",
            "Sub Focus Solar System (VIP Remix)",
            "remix,cover,live,acoustic");
        Assert.Null(allowed);

        var target = new PlaylistTrack { Artist = "Sub Focus", Title = "Solar System" };
        var remixCandidate = new Track
        {
            Artist = "Sub Focus",
            Title = "Solar System (Bootleg Remix)",
            Filename = "Sub Focus - Solar System (Bootleg Remix).flac",
            Format = "flac",
            Bitrate = 950,
            Size = 30_000_000
        };
        string? rejectedReason = null;
        var score = MatchScorer.ScoreCandidate(target, remixCandidate, new MatchScoringOptions
        {
            ExcludedPhrasesCsv = "remix,cover,live,acoustic",
            OnExcludedPhraseRejected = p => rejectedReason = p
        });
        Assert.Equal(0, score);
        Assert.Equal("remix", rejectedReason);
    }

    [Fact]
    public void OneLosslessFloor_AcceptsQuietFlacBetween401And700Kbps_RejectsFakeLowBitrateFlac()
    {
        Assert.True(SearchQualityProfiles.IsBitrateAcceptable("flac", 520, lossyMinBitrateKbps: 701));
        Assert.True(SearchQualityProfiles.IsBitrateAcceptable("flac", 0, lossyMinBitrateKbps: 701));
        Assert.False(SearchQualityProfiles.IsBitrateAcceptable("flac", 320, lossyMinBitrateKbps: 192));

        // Lossy still enforces lossy floor
        Assert.True(SearchQualityProfiles.IsBitrateAcceptable("mp3", 320, lossyMinBitrateKbps: 320));
        Assert.False(SearchQualityProfiles.IsBitrateAcceptable("mp3", 192, lossyMinBitrateKbps: 320));

        // Verify SearchFilterPolicy uses the single lossless floor rule
        var quietFlac = new Soulseek.File(1, "Artist - Acoustic.flac", 20_000_000, ".flac",
            new[] { new FileAttribute(FileAttributeType.BitRate, 550) });
        var decision = SearchFilterPolicy.EvaluateFile(
            quietFlac,
            new HashSet<string> { "flac" },
            (701, null),
            48000,
            Array.Empty<string>());
        Assert.True(decision.IsAccepted);
    }

    [Fact]
    public void SharedProfiles_RoundTripNonStrictStrictStricterAndCustom_AndMigrateLegacy701()
    {
        var config = new AppConfig();

        SearchQualityProfiles.Apply(config, SearchQualityProfileKind.NonStrict);
        Assert.Equal(SearchQualityProfileKind.NonStrict, SearchQualityProfiles.Detect(config));
        Assert.Equal(192, config.PreferredMinBitrate);

        SearchQualityProfiles.Apply(config, SearchQualityProfileKind.Strict);
        Assert.Equal(SearchQualityProfileKind.Strict, SearchQualityProfiles.Detect(config));
        Assert.DoesNotContain("320kbps floor", SearchQualityProfiles.Describe(config), StringComparison.OrdinalIgnoreCase);

        SearchQualityProfiles.Apply(config, SearchQualityProfileKind.Stricter);
        Assert.Equal(SearchQualityProfileKind.Stricter, SearchQualityProfiles.Detect(config));
        Assert.DoesNotContain("701", SearchQualityProfiles.Describe(config), StringComparison.OrdinalIgnoreCase);

        // Legacy 701 config still detects as Stricter
        config.PreferredMinBitrate = 701;
        Assert.Equal(SearchQualityProfileKind.Stricter, SearchQualityProfiles.Detect(config));

        // Custom formats -> Custom
        config.PreferredFormats = new List<string> { "flac", "mp3" };
        Assert.Equal(SearchQualityProfileKind.Custom, SearchQualityProfiles.Detect(config));
    }
}
