using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Enrichment.Providers;

/// <summary>
/// The file's own tags (TagLib, read-only): embedded cover, album, label (publisher), year,
/// genre, track number, ISRC, and TBPM/TKEY as hints. Free and always first.
/// </summary>
public sealed class LocalTagsProvider : IMetadataProvider
{
    private readonly ILogger _logger;

    public LocalTagsProvider(ILogger<LocalTagsProvider> logger)
    {
        _logger = logger;
        Runtime = new ProviderRuntime(Name, RatePolicy.Unlimited);
    }

    public string Name => MetadataMerger.TagsSource;
    public string DisplayName => "File tags";
    public string Description => "Embedded cover art, album, label, year, genre and ISRC from the file itself. Free, no network.";
    public MetadataField Supplies =>
        MetadataField.Artwork | MetadataField.Album | MetadataField.Label | MetadataField.ReleaseDate | MetadataField.Isrc |
        MetadataField.Genre | MetadataField.TrackNumber | MetadataField.Duration | MetadataField.BpmKeyHint;
    public ProviderRuntime Runtime { get; }
    public ProviderAvailability GetAvailability() => ProviderAvailability.Ready;

    public Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
            return Task.FromResult(ProviderLookup.NoMatch());

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Runtime.CountRequest();
                var c = Read(track.FilePath!);
                if (c.Supplied == MetadataField.None) return ProviderLookup.NoMatch();
                Runtime.CountMatch();
                return ProviderLookup.Match(c);
            }
            catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or UnauthorizedAccessException
                                        or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                _logger.LogDebug(ex, "[Enrichment] Tags unreadable for {Path}", track.FilePath);
                return ProviderLookup.NoMatch();
            }
        }, ct);
    }

    /// <summary>Reads the tags of one file. Public for tests.</summary>
    public static MetadataCandidate Read(string path)
    {
        // AIFF-C: TagLib# rejects the form type, so read its embedded "ID3 " chunk directly
        // (same approach as Services/Audio/AudioTagReader).
        bool aiff = Audio.AudioFormats.Find(Path.GetExtension(path))?.Family == Audio.AudioContainerFamily.Aiff;
        if (aiff && Audio.AiffHeader.TryRead(path, out var header) && header.IsAifc)
        {
            var aifc = new MetadataCandidate { Confidence = 1.0, DurationSeconds = header.Duration.TotalSeconds > 0 ? header.Duration.TotalSeconds : null };
            if (header.Id3Tag is { Length: > 10 } id3) FromTag(new TagLib.Id3v2.Tag(new TagLib.ByteVector(id3)), aifc);
            return aifc;
        }

        using var file = TagLib.File.Create(path);
        var c = new MetadataCandidate { Confidence = 1.0 };
        FromTag(file.Tag, c);
        var seconds = file.Properties?.Duration.TotalSeconds ?? 0;
        if (seconds > 0) c.DurationSeconds = seconds;
        return c;
    }

    private static void FromTag(TagLib.Tag tag, MetadataCandidate c)
    {

        var picture = tag.Pictures?.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? tag.Pictures?.FirstOrDefault();
        if (picture?.Data?.Data is { Length: > 64 } bytes) c.ArtworkBytes = bytes;

        if (!string.IsNullOrWhiteSpace(tag.Album)) c.Album = tag.Album;
        if (!string.IsNullOrWhiteSpace(tag.Publisher)) c.Label = tag.Publisher;
        if (tag.Year is > 1900 and < 2200) c.ReleaseDate = new DateTime((int)tag.Year, 1, 1);
        foreach (var g in tag.Genres ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(g)) c.Genres.Add(g.Trim());
        if (tag.Track > 0) c.TrackNumber = (int)tag.Track;
        if (!string.IsNullOrWhiteSpace(tag.ISRC)) c.Isrc = tag.ISRC;
        if (tag.BeatsPerMinute > 0) c.BpmHint = tag.BeatsPerMinute;
        if (!string.IsNullOrWhiteSpace(tag.InitialKey)) c.KeyHint = tag.InitialKey;
    }
}
