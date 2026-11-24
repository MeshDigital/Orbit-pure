using System.Linq;
using SLSKDONET.Services.Library.Files;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Files;

/// <summary>#343 LF1 — the single path-template engine for downloads, Bulk Rename, Smart Sorter and Reorganise.</summary>
public class PathTemplateEngineTests
{
    private static readonly TrackFacts Facts = new(
        Artist: "Tiësto",
        AlbumArtist: "Tiësto",
        Title: "Adagio for Strings",
        Mix: "VIP",
        Album: "Just Be",
        TrackNumber: 7,
        Year: 2004,
        Genre: "Trance",
        Label: "Black Hole",
        Bpm: 138.0,
        BpmBracket: "135-140",
        Key: "8A",
        Energy: 0.8,
        Format: "flac",
        Bitrate: 900,
        User: "dj_user",
        Filename: "tiesto_-_adagio_for_strings",
        LengthSeconds: 300,
        SizeBytes: 123456);

    // ── Tokens ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{artist}", "Tiësto")]
    [InlineData("{albumartist}", "Tiësto")]
    [InlineData("{title}", "Adagio for Strings")]
    [InlineData("{mix}", "VIP")]
    [InlineData("{remix}", "VIP")]
    [InlineData("{album}", "Just Be")]
    [InlineData("{year}", "2004")]
    [InlineData("{genre}", "Trance")]
    [InlineData("{label}", "Black Hole")]
    [InlineData("{bpm}", "138")]
    [InlineData("{bpm_bracket}", "135-140")]
    [InlineData("{key}", "8A")]
    [InlineData("{energy}", "80")]
    [InlineData("{format}", "flac")]
    [InlineData("{bitrate}", "900")]
    [InlineData("{user}", "dj_user")]
    [InlineData("{filename}", "tiesto_-_adagio_for_strings")]
    [InlineData("{length}", "300")]
    [InlineData("{size}", "123456")]
    public void Render_SingleToken_Resolves(string template, string expected)
    {
        Assert.Equal(expected, PathTemplateEngine.Render(template, Facts).Leaf);
    }

    [Fact]
    public void Render_TrackToken_PadsWithFormat()
    {
        Assert.Equal("07", PathTemplateEngine.Render("{track:00}", Facts).Leaf);
        Assert.Equal("7", PathTemplateEngine.Render("{track}", Facts).Leaf);
    }

    [Fact]
    public void Render_UnknownToken_IsEmpty()
    {
        Assert.Empty(PathTemplateEngine.Render("{nope}", Facts).Segments);
    }

    // ── Fallbacks + conditional literals ──────────────────────────────────────

    [Fact]
    public void Render_Fallback_UsesFirstNonEmpty()
    {
        var facts = Facts with { Album = null };
        Assert.Equal("Tiësto", PathTemplateEngine.Render("{album|artist}", facts).Leaf);
    }

    [Fact]
    public void Render_Fallback_QuotedLiteral()
    {
        var facts = Facts with { Album = null, AlbumArtist = null };
        Assert.Equal("Singles", PathTemplateEngine.Render("{album|albumartist|\"Singles\"}", facts).Leaf);
    }

    [Fact]
    public void Render_NestedFallback()
    {
        var facts = Facts with { Artist = null };
        Assert.Equal("tiesto_-_adagio_for_strings", PathTemplateEngine.Render("{artist|{filename}}", facts).Leaf);
    }

    [Fact]
    public void Render_ConditionalLiteral_EmittedOnlyWhenTagPresent()
    {
        Assert.Equal("Tiësto - Adagio for Strings", PathTemplateEngine.Render("{artist( - )}{title}", Facts).Leaf);
        var empty = Facts with { Artist = null };
        Assert.Equal("Adagio for Strings", PathTemplateEngine.Render("{artist( - )}{title}", empty).Leaf);
    }

    // ── Optional groups ───────────────────────────────────────────────────────

    [Fact]
    public void Render_OptionalGroup_KeptWhenPresent()
    {
        Assert.Equal("Adagio for Strings (VIP)", PathTemplateEngine.Render("{title} ({mix})", Facts).Leaf);
    }

    [Fact]
    public void Render_OptionalGroup_DroppedWhenEmpty_NoDoubleSeparators()
    {
        var facts = Facts with { Mix = null };
        Assert.Equal("Adagio for Strings", PathTemplateEngine.Render("{title} ({mix})", facts).Leaf);
    }

    [Fact]
    public void Render_OptionalGroup_DropsAdjacentSeparator()
    {
        var facts = Facts with { Album = null };
        Assert.Equal("Tiësto - Adagio for Strings", PathTemplateEngine.Render("{artist} - {title} [{album}]", facts).Leaf);
    }

    // ── Segment splitting + sanitisation ──────────────────────────────────────

    [Fact]
    public void Render_FolderTemplate_BuildsSegments()
    {
        var path = PathTemplateEngine.Render("{artist}/{album}/{title}", Facts);
        Assert.Equal(new[] { "Tiësto", "Just Be", "Adagio for Strings" }, path.Segments);
    }

    [Fact]
    public void Render_InvalidCharacters_AreReplaced()
    {
        var facts = Facts with { Title = "a?b:c*d<e>f|g\"h" };
        Assert.Equal("a_b_c_d_e_f_g_h", PathTemplateEngine.Render("{title}", facts).Leaf);
    }

    [Fact]
    public void Render_DotOnlySegment_IsNeutralised()
    {
        var facts = Facts with { Title = ".." };
        Assert.Equal("_", PathTemplateEngine.Render("{title}", facts).Leaf);
    }

    [Fact]
    public void Render_TrailingDotAndSpace_AreStripped()
    {
        var facts = Facts with { Title = "Title . " };
        Assert.Equal("Title", PathTemplateEngine.Render("{title}", facts).Leaf);
    }

    [Fact]
    public void Render_ReservedDeviceName_IsPrefixed()
    {
        var facts = Facts with { Title = "CON" };
        Assert.Equal("_CON", PathTemplateEngine.Render("{title}", facts).Leaf);
    }

    [Fact]
    public void Render_AsciiSafe_FoldsDiacritics()
    {
        var path = PathTemplateEngine.Render("{artist}", Facts, new RenderOptions(AsciiSafe: true));
        Assert.Equal("Tiesto", path.Leaf);
    }

    [Fact]
    public void Render_LengthBudget_Truncates()
    {
        var facts = Facts with { Title = new string('x', 300) };
        var path = PathTemplateEngine.Render("{title}", facts, new RenderOptions(MaxSegmentLength: 100));
        Assert.Equal(100, path.Leaf.Length);
    }

    // ── Presets (from #343) ───────────────────────────────────────────────────

    [Theory]
    [InlineData("{artist} - {title} ({mix})", "Tiësto - Adagio for Strings (VIP)")]
    [InlineData("{albumartist|artist}/{year} - {album|\"Singles\"}/{track:00} - {title}", "Tiësto/2004 - Just Be/07 - Adagio for Strings")]
    [InlineData("{key|\"No Key\"}/{bpm_bracket}/{artist} - {title}", "8A/135-140/Tiësto - Adagio for Strings")]
    [InlineData("{user}/{filename}", "dj_user/tiesto_-_adagio_for_strings")]
    public void Render_Preset(string template, string expected)
    {
        Assert.Equal(expected, PathTemplateEngine.Render(template, Facts).RelativePath);
    }
}
