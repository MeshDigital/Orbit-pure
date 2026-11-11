using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Services;
using SLSKDONET.Services.Discovery;
using SLSKDONET.Services.Library.Maintenance;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Maintenance;

/// <summary>Table-driven tests for the pure #212 detector.</summary>
public class TagIssueDetectorTests
{
    private static LibraryIdentityIndex Index(string[]? artists = null, string[]? labels = null) => new(
        new HashSet<string>((artists ?? Array.Empty<string>()).Select(PlaylistDiscoveryService.NormalizeArtist), StringComparer.OrdinalIgnoreCase),
        new HashSet<string>((labels ?? Array.Empty<string>()).Select(PlaylistDiscoveryService.NormalizeArtist), StringComparer.OrdinalIgnoreCase));

    private static readonly string[] Known = { "Bicep", "Jay-Z", "Pendulum" };
    private static readonly LibraryIdentityIndex Idx = Index(artists: Known, labels: new[] { "Hospital Records" });

    private static ProposedTagChange? Detect(string artist, string title, string? file = null, TagIssueOptions? options = null, LibraryIdentityIndex? index = null)
    {
        var results = TagIssueDetector.Detect(
            new TagIdentity("h", artist, title, null, null, file),
            index ?? Idx,
            options ?? new TagIssueOptions());
        return results.Count == 0 ? null : results[0];
    }

    [Fact]
    public void CombinedArtistTitle_SplitsPlaceholderArtist()
    {
        var p = Detect("Unknown Artist", "Bicep - Glue");
        Assert.NotNull(p);
        Assert.Equal(TagIssueRule.CombinedArtistTitle.ToString(), p!.Rule);
        Assert.Equal("Bicep", Field(p, TagField.Artist));
        Assert.Equal("Glue", Field(p, TagField.Title));
        Assert.Equal(ProposalConfidence.High, p.Confidence);
        Assert.True(p.DefaultSelected);
    }

    [Fact]
    public void LeadingTrackNumber_ThenSplit()
    {
        var p = Detect("", "01 - Bicep - Glue");
        Assert.NotNull(p);
        Assert.Equal("Bicep", Field(p!, TagField.Artist));
        Assert.Equal("Glue", Field(p!, TagField.Title));
    }

    [Fact]
    public void CombinedArtistTitle_PreservesHyphenatedArtistName()
    {
        var p = Detect("Unknown Artist", "Jay-Z - 99 Problems");
        Assert.NotNull(p);
        Assert.Equal("Jay-Z", Field(p!, TagField.Artist));
        Assert.Equal("99 Problems", Field(p!, TagField.Title));
    }

    [Fact]
    public void CombinedArtistTitle_KeepsRemixTextInTitle()
    {
        var p = Detect("", "Bicep - Glue - Hammer Remix");
        Assert.NotNull(p);
        Assert.Equal("Bicep", Field(p!, TagField.Artist));
        Assert.Equal("Glue - Hammer Remix", Field(p!, TagField.Title));
    }

    [Fact]
    public void SwappedFields_HighWhenFilenameAgrees()
    {
        var p = Detect("Glue", "Bicep", @"C:\m\Bicep - Glue.mp3");
        Assert.NotNull(p);
        Assert.Equal(TagIssueRule.SwappedFields.ToString(), p!.Rule);
        Assert.Equal("Bicep", Field(p, TagField.Artist));
        Assert.Equal("Glue", Field(p, TagField.Title));
        Assert.Equal(ProposalConfidence.High, p.Confidence);
    }

    [Fact]
    public void LabelInArtist_MovesLabelAndDerivesArtistFromFilename()
    {
        var p = Detect("Hospital Records", "Hold Your Colour", @"C:\m\Pendulum - Hold Your Colour.flac");
        Assert.NotNull(p);
        Assert.Equal(TagIssueRule.LabelInArtist.ToString(), p!.Rule);
        Assert.Equal("Pendulum", Field(p, TagField.Artist));
        Assert.Equal("Hospital Records", Field(p, TagField.Label));
    }

    [Fact]
    public void LabelInArtist_NoDerivableArtist_NeedsReviewNoChange()
    {
        var p = Detect("Hospital Records", "Hold Your Colour", @"C:\m\track01.flac");
        Assert.NotNull(p);
        Assert.Equal(TagIssueRule.LabelInArtist.ToString(), p!.Rule);
        Assert.Equal(ProposalConfidence.NeedsReview, p!.Confidence);
        Assert.False(p.HasChanges);
    }

    [Fact]
    public void Underscores_AreReplacedWithSpaces()
    {
        var p = Detect("Bicep", "Glue_(Original_Mix)");
        Assert.NotNull(p);
        Assert.Equal("Glue (Original Mix)", Field(p!, TagField.Title));
    }

    [Fact]
    public void JunkSuffixes_AreStripped()
    {
        var p = Detect("Bicep", "Glue [320] (Official Video)");
        Assert.NotNull(p);
        Assert.Equal("Glue", Field(p!, TagField.Title));
    }

    [Fact]
    public void CleanIdentity_ProducesNoProposals()
    {
        Assert.Null(Detect("AC/DC", "Back In Black"));
        Assert.Null(Detect("Bicep", "Glue (Extended Mix)"));
    }

    [Fact]
    public void DetectorOutput_IsIdempotent()
    {
        var p = Detect("Unknown Artist", "Bicep - Glue");
        Assert.NotNull(p);
        var clean = new TagIdentity("h", Field(p!, TagField.Artist), Field(p!, TagField.Title), null, null, null);
        Assert.Empty(TagIssueDetector.Detect(clean, Idx, new TagIssueOptions()));
    }

    private static string? Field(ProposedTagChange p, TagField field) =>
        p.Changes.FirstOrDefault(c => c.Field == field)?.Value;
}
