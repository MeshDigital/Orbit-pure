using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Enrichment;

/// <summary>The stored-field changes one enrichment pass wants to make for a track.</summary>
public sealed class MetadataPatch
{
    public MetadataPatch(string hash) => Hash = hash;

    public string Hash { get; }
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Album { get; set; }
    public string? Label { get; set; }
    public string? Genres { get; set; }
    public string? Isrc { get; set; }
    public string? MusicBrainzId { get; set; }
    public string? SpotifyTrackId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public int? DurationSeconds { get; set; }
    public double? TagBpm { get; set; }

    /// <summary>Provenance rows for every field set above.</summary>
    public Dictionary<string, MetadataProvenanceEntity> Provenance { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Provenance.Count == 0;

    /// <summary>"artwork deezer · label musicbrainz" style summary, for logs.</summary>
    public override string ToString() => string.Join(" · ", Provenance.Values.Select(p => $"{p.Field} {p.Source}"));
}

/// <summary>
/// Field-level merge of provider answers into a track, with provenance. The rules:
/// <list type="number">
/// <item>Analysis-owned BPM, key and energy are never written. A provider's BPM/key is a hint and
///   is dropped; only the file's own tag BPM fills the <c>TagBPM</c> side column when it is empty.</item>
/// <item>A field the user edited (provenance source "user") is never written.</item>
/// <item>An empty field is filled by the first provider that has it (providers run cheapest first).</item>
/// <item>A filled field without enrichment provenance (it came from the file at import, or from an
///   older code path) is left alone. A field an earlier enrichment wrote is replaced only by a
///   clearly more confident answer (+0.1), e.g. an ISRC-exact match over a fuzzy search.</item>
/// <item>Artist/title are replaced only for placeholder identities ("Unknown Artist", a file-name
///   title), and only at ≥ 0.85 confidence (AcoustID fingerprint, ISRC lookup).</item>
/// <item>Answers under <see cref="MinConfidence"/> are ignored.</item>
/// </list>
/// Pure: no I/O, so the rules are unit-tested directly.
/// </summary>
public static class MetadataMerger
{
    public const string UserSource = "user";
    public const string TagsSource = "tags";
    public const double MinConfidence = 0.6;
    public const double IdentityMinConfidence = 0.85;
    private const double UpgradeMargin = 0.1;
    private const double StyleBonus = 0.15;

    public static class Fields
    {
        public const string Artist = "artist";
        public const string Title = "title";
        public const string Album = "album";
        public const string Label = "label";
        public const string Genre = "genre";
        public const string Isrc = "isrc";
        public const string MusicBrainzId = "musicbrainzId";
        public const string SpotifyId = "spotifyId";
        public const string Artwork = "artwork";
        public const string ReleaseDate = "releaseDate";
        public const string Duration = "duration";
        public const string TagBpm = "tagBpm";
    }

    /// <summary>The wanted fields still empty on <paramref name="track"/>.</summary>
    public static MetadataField Missing(EnrichmentTrack track, MetadataField wanted)
    {
        var missing = MetadataField.None;
        if (wanted.HasFlag(MetadataField.Artwork) && string.IsNullOrWhiteSpace(track.AlbumArtUrl)) missing |= MetadataField.Artwork;
        if (wanted.HasFlag(MetadataField.Album) && string.IsNullOrWhiteSpace(track.Album)) missing |= MetadataField.Album;
        if (wanted.HasFlag(MetadataField.Label) && string.IsNullOrWhiteSpace(track.Label)) missing |= MetadataField.Label;
        if (wanted.HasFlag(MetadataField.ReleaseDate) && track.ReleaseDate is null) missing |= MetadataField.ReleaseDate;
        if (wanted.HasFlag(MetadataField.Isrc) && string.IsNullOrWhiteSpace(track.Isrc)) missing |= MetadataField.Isrc;
        if (wanted.HasFlag(MetadataField.Genre) && string.IsNullOrWhiteSpace(track.Genres)) missing |= MetadataField.Genre;
        if (wanted.HasFlag(MetadataField.Identity) && track.HasWeakIdentity) missing |= MetadataField.Identity;
        return missing;
    }

    /// <summary>
    /// Merges <paramref name="candidate"/> from <paramref name="source"/> into
    /// <paramref name="patch"/>, and updates <paramref name="track"/> and
    /// <paramref name="provenance"/> so later providers see what is already filled.
    /// Returns the fields written.
    /// </summary>
    public static MetadataField Apply(
        EnrichmentTrack track,
        IDictionary<string, MetadataProvenanceEntity> provenance,
        MetadataCandidate candidate,
        string source,
        MetadataPatch patch,
        DateTime? nowUtc = null)
    {
        var written = MetadataField.None;
        var confidence = Math.Clamp(candidate.Confidence, 0, 1);
        if (confidence < MinConfidence) return written;
        var now = nowUtc ?? DateTime.UtcNow;

        bool Take(string field, bool isEmpty, double conf)
        {
            provenance.TryGetValue(field, out var prov);
            if (prov != null && string.Equals(prov.Source, UserSource, StringComparison.OrdinalIgnoreCase)) return false;
            if (isEmpty) return true;
            if (prov == null || string.Equals(prov.Source, TagsSource, StringComparison.OrdinalIgnoreCase)) return false;
            return conf > prov.Confidence + UpgradeMargin;
        }

        void Record(string field, double conf)
        {
            var row = new MetadataProvenanceEntity { TrackUniqueHash = track.Hash, Field = field, Source = source, Confidence = conf, UpdatedAtUtc = now };
            provenance[field] = row;
            patch.Provenance[field] = row;
        }

        // Identity: placeholders only, high confidence only.
        if (confidence >= IdentityMinConfidence && !string.IsNullOrWhiteSpace(candidate.Artist) && !string.IsNullOrWhiteSpace(candidate.Title))
        {
            if (EnrichmentTrack.IsPlaceholderArtist(track.Artist) && Take(Fields.Artist, true, confidence))
            {
                track.Artist = patch.Artist = candidate.Artist!.Trim();
                Record(Fields.Artist, confidence);
                written |= MetadataField.Identity;
            }
            if ((string.IsNullOrWhiteSpace(track.Title) || track.TitleLooksLikeFileName) && Take(Fields.Title, true, confidence))
            {
                track.Title = patch.Title = candidate.Title!.Trim();
                Record(Fields.Title, confidence);
                written |= MetadataField.Identity;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.Album) && Take(Fields.Album, string.IsNullOrWhiteSpace(track.Album), confidence))
        {
            track.Album = patch.Album = candidate.Album!.Trim();
            Record(Fields.Album, confidence);
            written |= MetadataField.Album;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Label) && Take(Fields.Label, string.IsNullOrWhiteSpace(track.Label), confidence))
        {
            track.Label = patch.Label = candidate.Label!.Trim();
            Record(Fields.Label, confidence);
            written |= MetadataField.Label;
        }

        if (candidate.ReleaseDate is { } date && date.Year > 1900 && Take(Fields.ReleaseDate, track.ReleaseDate is null, confidence))
        {
            track.ReleaseDate = patch.ReleaseDate = date;
            Record(Fields.ReleaseDate, confidence);
            written |= MetadataField.ReleaseDate;
        }

        if (NormalizeIsrc(candidate.Isrc) is { } isrc && Take(Fields.Isrc, string.IsNullOrWhiteSpace(track.Isrc), confidence))
        {
            track.Isrc = patch.Isrc = isrc;
            Record(Fields.Isrc, confidence);
            written |= MetadataField.Isrc;
        }

        var genreText = GenreText(candidate);
        if (genreText != null)
        {
            var genreConfidence = candidate.Styles.Count > 0 ? Math.Min(1.0, confidence + StyleBonus) : confidence;
            if (Take(Fields.Genre, string.IsNullOrWhiteSpace(track.Genres), genreConfidence))
            {
                track.Genres = patch.Genres = genreText;
                Record(Fields.Genre, genreConfidence);
                written |= MetadataField.Genre;
                if (candidate.Styles.Count > 0) written |= MetadataField.Style;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.ArtworkUrl) && Take(Fields.Artwork, string.IsNullOrWhiteSpace(track.AlbumArtUrl), confidence))
        {
            track.AlbumArtUrl = patch.AlbumArtUrl = candidate.ArtworkUrl;
            Record(Fields.Artwork, confidence);
            written |= MetadataField.Artwork;
        }

        if (!string.IsNullOrWhiteSpace(candidate.MusicBrainzRecordingId) && Take(Fields.MusicBrainzId, string.IsNullOrWhiteSpace(track.MusicBrainzId), confidence))
        {
            track.MusicBrainzId = patch.MusicBrainzId = candidate.MusicBrainzRecordingId;
            Record(Fields.MusicBrainzId, confidence);
            written |= MetadataField.ExternalIds;
        }

        if (!string.IsNullOrWhiteSpace(candidate.SpotifyTrackId) && Take(Fields.SpotifyId, string.IsNullOrWhiteSpace(track.SpotifyTrackId), confidence))
        {
            track.SpotifyTrackId = patch.SpotifyTrackId = candidate.SpotifyTrackId;
            Record(Fields.SpotifyId, confidence);
            written |= MetadataField.ExternalIds;
        }

        if (candidate.DurationSeconds is double seconds && seconds > 0 && Take(Fields.Duration, track.DurationSeconds is null or <= 0, confidence))
        {
            track.DurationSeconds = seconds;
            patch.DurationSeconds = (int)Math.Round(seconds);
            Record(Fields.Duration, confidence);
            written |= MetadataField.Duration;
        }

        // BPM/key: a web provider's value is a low-trust hint and is never stored. The file's own
        // tag only fills the TagBPM side column, which analysis consults on its own terms.
        if (string.Equals(source, TagsSource, StringComparison.OrdinalIgnoreCase) &&
            candidate.BpmHint is > 0 and < 400 && track.TagBpm is null && Take(Fields.TagBpm, true, confidence))
        {
            track.TagBpm = patch.TagBpm = candidate.BpmHint;
            Record(Fields.TagBpm, confidence);
            written |= MetadataField.BpmKeyHint;
        }

        return written;
    }

    /// <summary>Styles first (more specific), then genres; distinct, at most five.</summary>
    public static string? GenreText(MetadataCandidate c)
    {
        var all = c.Styles.Concat(c.Genres)
            .Select(g => g?.Trim())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        return all.Count == 0 ? null : string.Join(", ", all);
    }

    public static string? NormalizeIsrc(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc)) return null;
        var s = new string(isrc.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return s.Length == 12 ? s : null;
    }
}
