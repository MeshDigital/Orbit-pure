using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Discovery;

/// <summary>What the playlist sounds like, as far as suggestion ranking cares.</summary>
public sealed class PlaylistDiscoveryProfile
{
    /// <summary>Playlist artists, most frequent first.</summary>
    public List<(string Artist, int Count)> Artists { get; } = new();
    public double? BpmLow { get; set; }
    public double? BpmHigh { get; set; }
    public double? BpmMedian { get; set; }

    /// <summary>Camelot keys in the playlist, most common first.</summary>
    public List<string> CamelotKeys { get; } = new();

    /// <summary>Beatport genre most of the resolved seed tracks belong to.</summary>
    public string? Genre { get; set; }
    public int? GenreId { get; set; }
    public string? GenreSlug { get; set; }

    /// <summary>Everything already in the library or any playlist (downloaded, queued or missing).</summary>
    public OwnedTrackIndex Owned { get; } = new();
}

public sealed record PlaylistDiscoveryOptions(bool UseBeatport, bool UseDeezer, int Seed = 0);

public sealed record PlaylistDiscoveryResult(
    IReadOnlyList<DiscoveryCandidate> Suggestions,
    PlaylistDiscoveryProfile Profile,
    string Summary)
{
    /// <summary>The playlist's track identities these suggestions were made from (see <see cref="PlaylistDiscoveryService.Fingerprint"/>).</summary>
    public IReadOnlyList<string> Fingerprint { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Suggests tracks to add to a playlist: finds what the playlist's artists released next
/// (Beatport), what tops the playlist's Beatport genre chart, and what Deezer's related artists
/// play most, drops everything already in the library, and ranks the rest by how well its BPM,
/// key and genre fit the playlist. The result feeds the Discover sidepanel, whose Queue button
/// hands a suggestion to the normal Soulseek download pipeline.
/// </summary>
public sealed class PlaylistDiscoveryService
{
    private const int MaxSeedArtists = 5;
    private const int MaxBeatportSeedLookups = 6;
    private const int MaxSuggestions = 60;
    private const int MaxBeatportConfirmations = 15;

    private readonly ILibraryService _library;
    private readonly BeatportCatalogClient _beatport;
    private readonly DeezerCatalogClient _deezer;
    private readonly ILogger<PlaylistDiscoveryService> _logger;

    public PlaylistDiscoveryService(
        ILibraryService library,
        BeatportCatalogClient beatport,
        DeezerCatalogClient deezer,
        ILogger<PlaylistDiscoveryService> logger)
    {
        _library = library;
        _beatport = beatport;
        _deezer = deezer;
        _logger = logger;
    }

    public async Task<PlaylistDiscoveryResult> SuggestAsync(
        Guid playlistId,
        PlaylistDiscoveryOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Reading the playlist…");
        var playlistTracks = await _library.LoadPlaylistTracksAsync(playlistId).ConfigureAwait(false);
        var profile = new PlaylistDiscoveryProfile();
        if (playlistTracks.Count == 0)
            return new PlaylistDiscoveryResult(Array.Empty<DiscoveryCandidate>(), profile, "This playlist is empty.");

        await BuildProfileAsync(profile, playlistTracks, await LoadOwnedTracksAsync(playlistTracks).ConfigureAwait(false), ct).ConfigureAwait(false);

        var rng = new Random(options.Seed == 0 ? playlistId.GetHashCode() : options.Seed);
        var seedArtists = PickSeedArtists(profile, rng);
        var candidates = new List<DiscoveryCandidate>();

        if (options.UseBeatport)
            candidates.AddRange(await FromBeatportAsync(profile, playlistTracks, seedArtists, rng, progress, ct).ConfigureAwait(false));
        if (options.UseDeezer)
            candidates.AddRange(await FromDeezerAsync(seedArtists, progress, ct).ConfigureAwait(false));

        progress?.Report("Ranking…");
        var merged = Merge(candidates);
        var fresh = merged.Where(c => !IsOwned(c, profile) && !IsUnwantedVersion(c)).ToList();
        foreach (var c in fresh) Score(c, profile);

        // Deezer finds carry no BPM/key/genre. Look the best of them up on Beatport so they rank
        // on real data, get a buy link, and — when Beatport also suggested them — the cross-source boost.
        if (options.UseBeatport)
        {
            var toConfirm = fresh
                .Where(c => (c.Sources & DiscoverySource.BeatportSuggested) == 0)
                .OrderByDescending(c => c.Score)
                .Take(MaxBeatportConfirmations)
                .ToList();
            if (toConfirm.Count > 0)
            {
                progress?.Report($"Checking {toConfirm.Count} Deezer finds on Beatport…");
                await ConfirmOnBeatportAsync(toConfirm, ct).ConfigureAwait(false);
                foreach (var c in toConfirm) Score(c, profile);
            }
        }

        // The Beatport lookup can fill in a mix name ("Extended Mix"), so check ownership again.
        fresh.RemoveAll(c => IsOwned(c, profile));
        var ranked = fresh.OrderByDescending(c => c.Score).Take(MaxSuggestions).ToList();

        var summary = BuildSummary(profile, seedArtists, merged.Count, merged.Count - fresh.Count);
        _logger.LogInformation("[Discover] Playlist {Id}: {Found} candidates, {Owned} already owned, {Shown} shown ({Summary})",
            playlistId, merged.Count, merged.Count - fresh.Count, ranked.Count, summary);
        return new PlaylistDiscoveryResult(ranked, profile, summary) { Fingerprint = Fingerprint(playlistTracks) };
    }

    // ── Cache support (no network) ──────────────────────────────────────────

    /// <summary>"Owned" = library files plus every row of every playlist (downloaded, queued or
    /// missing), so tracks already on their way aren't suggested again.</summary>
    private async Task<IEnumerable<(string Artist, string Title)>> LoadOwnedTracksAsync(IEnumerable<PlaylistTrack> extra)
    {
        var libraryEntries = await _library.LoadAllLibraryEntriesAsync().ConfigureAwait(false);
        var allPlaylistTracks = await _library.GetAllPlaylistTracksAsync().ConfigureAwait(false);
        return libraryEntries.Select(e => (e.Artist, e.Title))
            .Concat(allPlaylistTracks.Select(t => (t.Artist, t.Title)))
            .Concat(extra.Select(t => (t.Artist, t.Title)))
            .ToList();
    }

    /// <summary>The playlist as it is now: its fingerprint, and saved suggestions minus anything
    /// that has since been downloaded or queued. Local data only — used to show cached results.</summary>
    public async Task<(IReadOnlyList<string> Fingerprint, List<DiscoveryCandidate> StillNew)> CheckCachedAsync(
        Guid playlistId, IEnumerable<DiscoveryCandidate> cached)
    {
        var playlistTracks = await _library.LoadPlaylistTracksAsync(playlistId).ConfigureAwait(false);
        var index = new OwnedTrackIndex();
        foreach (var (artist, title) in await LoadOwnedTracksAsync(playlistTracks).ConfigureAwait(false))
            index.Add(artist, title);
        return (Fingerprint(playlistTracks), cached.Where(c => !index.Contains(c)).ToList());
    }

    /// <summary>Order-independent identity of a playlist's tracks.</summary>
    public static IReadOnlyList<string> Fingerprint(IEnumerable<PlaylistTrack> tracks) =>
        tracks.Select(t => !string.IsNullOrEmpty(t.TrackUniqueHash) ? t.TrackUniqueHash : TrackHashUtil.Compute(t.Artist, t.Title))
              .Where(k => !string.IsNullOrEmpty(k))
              .Distinct(StringComparer.Ordinal)
              .OrderBy(k => k, StringComparer.Ordinal)
              .ToList();

    // ── Profile ─────────────────────────────────────────────────────────────

    private async Task BuildProfileAsync(
        PlaylistDiscoveryProfile profile,
        List<PlaylistTrack> tracks,
        IEnumerable<(string Artist, string Title)> library,
        CancellationToken ct)
    {
        foreach (var (artist, title) in library)
            profile.Owned.Add(artist, title);

        foreach (var group in tracks
                     .SelectMany(t => SplitArtists(t.Artist))
                     .GroupBy(NormalizeArtist)
                     .Where(g => g.Key.Length > 0)
                     .OrderByDescending(g => g.Count()))
            profile.Artists.Add((group.First(), group.Count()));

        var bpms = new List<double>();
        var keys = new List<string>();
        foreach (var track in tracks.Take(200))
        {
            ct.ThrowIfCancellationRequested();
            double? bpm = null;
            string? camelot = null;
            if (!string.IsNullOrEmpty(track.TrackUniqueHash))
            {
                var f = await _library.GetAudioFeaturesByHashAsync(track.TrackUniqueHash).ConfigureAwait(false);
                if (f != null)
                {
                    if (f.Bpm > 40) bpm = f.Bpm;
                    camelot = ToCamelot(!string.IsNullOrWhiteSpace(f.CamelotKey)
                        ? f.CamelotKey
                        : string.IsNullOrWhiteSpace(f.Key) ? null : f.Key + (f.Scale?.StartsWith("min", StringComparison.OrdinalIgnoreCase) == true ? "m" : ""));
                }
            }
            bpm ??= track.BPM is > 40 ? track.BPM : null;
            camelot ??= ToCamelot(track.MusicalKey);
            if (bpm is { } b) bpms.Add(b);
            if (camelot != null) keys.Add(camelot);
        }

        ApplyBpmRange(profile, bpms);
        profile.CamelotKeys.AddRange(keys.GroupBy(k => k).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key));
    }

    /// <summary>The playlist's working BPM band: 10th–90th percentile, at least ±3 around the median.</summary>
    public static void ApplyBpmRange(PlaylistDiscoveryProfile profile, IReadOnlyCollection<double> bpms)
    {
        if (bpms.Count == 0) return;
        var sorted = bpms.OrderBy(b => b).ToList();
        double Pct(double p) => sorted[(int)Math.Clamp(Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
        profile.BpmMedian = Pct(0.5);
        profile.BpmLow = Math.Min(Pct(0.1), profile.BpmMedian.Value - 3);
        profile.BpmHigh = Math.Max(Pct(0.9), profile.BpmMedian.Value + 3);
    }

    private static List<string> PickSeedArtists(PlaylistDiscoveryProfile profile, Random rng)
    {
        // Repeated artists define the playlist best; the rest of the slots are a random draw so
        // Refresh shows something different on playlists where every artist appears once.
        var repeated = profile.Artists.Where(a => a.Count > 1).Select(a => a.Artist).Take(3).ToList();
        var rest = profile.Artists.Select(a => a.Artist).Except(repeated).OrderBy(_ => rng.Next()).ToList();
        return repeated.Concat(rest).Take(MaxSeedArtists).ToList();
    }

    // ── Sources ─────────────────────────────────────────────────────────────

    private async Task<List<DiscoveryCandidate>> FromBeatportAsync(
        PlaylistDiscoveryProfile profile,
        List<PlaylistTrack> playlistTracks,
        List<string> seedArtists,
        Random rng,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var results = new List<DiscoveryCandidate>();
        progress?.Report("Finding the playlist on Beatport…");

        // Look up a few seed tracks to learn their Beatport artist ids and genre.
        var lookups = playlistTracks
            .Where(t => seedArtists.Any(a => SplitArtists(t.Artist).Any(x => NormalizeArtist(x) == NormalizeArtist(a))))
            .GroupBy(t => NormalizeArtist(SplitArtists(t.Artist).FirstOrDefault() ?? t.Artist))
            .Select(g => g.OrderBy(_ => rng.Next()).First())
            .Concat(playlistTracks.OrderBy(_ => rng.Next()))
            .DistinctBy(t => t.Id)
            .Take(MaxBeatportSeedLookups)
            .ToList();

        var artistIds = new Dictionary<string, int>(StringComparer.Ordinal); // normalized name → id
        var genres = new List<(int Id, string? Slug, string? Name)>();
        var searches = await Task.WhenAll(lookups.Select(t =>
            _beatport.SearchTracksAsync($"{PrimaryArtist(t.Artist)} {StripMixInfo(t.Title)}", ct))).ConfigureAwait(false);

        for (int i = 0; i < lookups.Count; i++)
        {
            var seedArtistKeys = SplitArtists(lookups[i].Artist).Select(NormalizeArtist).ToHashSet();
            var seedTitle = NormalizeTitle(lookups[i].Title);
            var hit = searches[i].FirstOrDefault(r =>
                NormalizeTitle(r.Candidate.Title) == seedTitle
                && r.Refs.Artists.Any(a => seedArtistKeys.Contains(NormalizeArtist(a.Name))));
            if (hit.Candidate == null)
                hit = searches[i].FirstOrDefault(r => r.Refs.Artists.Any(a => seedArtistKeys.Contains(NormalizeArtist(a.Name))));
            if (hit.Candidate == null) continue;

            foreach (var (id, name) in hit.Refs.Artists)
                if (seedArtistKeys.Contains(NormalizeArtist(name)))
                    artistIds.TryAdd(NormalizeArtist(name), id);
            if (hit.Refs.GenreId is { } gid)
                genres.Add((gid, hit.Refs.GenreSlug, hit.Candidate.Genre));
        }

        // A seed artist whose track wasn't looked up can still come through another search's credits.
        foreach (var r in searches.SelectMany(s => s))
            foreach (var (id, name) in r.Refs.Artists)
                if (seedArtists.Any(a => NormalizeArtist(a) == NormalizeArtist(name)))
                    artistIds.TryAdd(NormalizeArtist(name), id);

        var topGenre = genres.GroupBy(g => g.Id).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (topGenre != null)
        {
            profile.GenreId = topGenre.Key;
            profile.Genre = topGenre.First().Name;
            profile.GenreSlug = topGenre.Select(g => g.Slug).FirstOrDefault(s => s != null);
        }

        progress?.Report($"Checking new releases on Beatport{(profile.Genre != null ? $" and the {profile.Genre} chart" : "")}…");

        var artistTasks = artistIds.Take(MaxSeedArtists).Select(async kv =>
        {
            var tracks = await _beatport.ArtistTracksAsync(kv.Value, ct).ConfigureAwait(false);
            var via = seedArtists.FirstOrDefault(a => NormalizeArtist(a) == kv.Key) ?? kv.Key;
            return tracks.Select((t, i) =>
            {
                t.Candidate.Sources = DiscoverySource.BeatportArtist;
                t.Candidate.SourceStrength = 1.0 - Math.Min(i, 24) / 30.0; // newest first
                t.Candidate.ViaArtist = via;
                return t.Candidate;
            }).ToList();
        }).ToList();

        Task<List<(DiscoveryCandidate Candidate, BeatportTrackRefs Refs)>>? chartTask =
            profile.GenreId is { } genreId ? _beatport.GenreTop100Async(genreId, profile.GenreSlug, ct) : null;

        foreach (var list in await Task.WhenAll(artistTasks).ConfigureAwait(false))
            results.AddRange(list);

        if (chartTask != null)
        {
            var chart = await chartTask.ConfigureAwait(false);
            for (int i = 0; i < chart.Count; i++)
            {
                var c = chart[i].Candidate;
                c.Sources = DiscoverySource.BeatportChart;
                c.SourceStrength = 1.0 - i / 100.0;
                c.ChartPosition = i + 1;
                results.Add(c);
            }
        }
        return results;
    }

    private async Task<List<DiscoveryCandidate>> FromDeezerAsync(
        List<string> seedArtists, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Asking Deezer for related artists…");
        var perSeed = await Task.WhenAll(seedArtists.Select(async seed =>
        {
            var found = new List<DiscoveryCandidate>();
            var artist = await _deezer.FindArtistAsync(seed, ct).ConfigureAwait(false);
            if (artist == null) return found;

            var own = await _deezer.TopTracksAsync(artist.Id, 5, ct).ConfigureAwait(false);
            foreach (var (t, i) in own.Select((t, i) => (t, i)))
            {
                t.Sources = DiscoverySource.DeezerArtist;
                t.SourceStrength = 0.8 - i * 0.1;
                t.ViaArtist = seed;
                found.Add(t);
            }

            var related = await _deezer.RelatedArtistsAsync(artist.Id, 6, ct).ConfigureAwait(false);
            var relatedTop = await Task.WhenAll(related.Select(r => _deezer.TopTracksAsync(r.Id, 3, ct))).ConfigureAwait(false);
            for (int r = 0; r < related.Count; r++)
                foreach (var (t, i) in relatedTop[r].Select((t, i) => (t, i)))
                {
                    t.Sources = DiscoverySource.DeezerRelated;
                    t.SourceStrength = (1.0 - r / 8.0) * (1.0 - i * 0.15);
                    t.ViaArtist = seed;
                    found.Add(t);
                }
            return found;
        })).ConfigureAwait(false);
        return perSeed.SelectMany(x => x).ToList();
    }

    // ── Merge, filter, score ────────────────────────────────────────────────

    /// <summary>Folds duplicates (same artist and title from several sources) into one candidate.</summary>
    public static List<DiscoveryCandidate> Merge(IEnumerable<DiscoveryCandidate> candidates)
    {
        var byKey = new Dictionary<string, DiscoveryCandidate>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var key = NormalizeArtist(PrimaryArtist(c.Artist)) + "|" + NormalizeTitle(c.SearchTitle);
            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = c;
                continue;
            }
            existing.Sources |= c.Sources;
            existing.HitCount += c.HitCount;
            existing.SourceStrength = Math.Max(existing.SourceStrength, c.SourceStrength);
            existing.Bpm ??= c.Bpm;
            existing.CamelotKey ??= c.CamelotKey;
            existing.Genre ??= c.Genre;
            existing.Label ??= c.Label;
            existing.Album ??= c.Album;
            existing.ReleaseDate ??= c.ReleaseDate;
            existing.DurationSeconds ??= c.DurationSeconds;
            existing.ImageUrl ??= c.ImageUrl;
            existing.PreviewUrl ??= c.PreviewUrl;
            existing.BeatportUrl ??= c.BeatportUrl;
            existing.DeezerUrl ??= c.DeezerUrl;
            existing.Price ??= c.Price;
            existing.ViaArtist ??= c.ViaArtist;
            existing.ChartPosition ??= c.ChartPosition;
        }
        return byKey.Values.ToList();
    }

    public static bool IsOwned(DiscoveryCandidate c, PlaylistDiscoveryProfile profile) => profile.Owned.Contains(c);

    private static readonly Regex UnwantedVersion = new(@"\b(dj mix|continuous mix|mixed|live)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Continuous DJ mixes and very long uploads aren't tracks you'd add to a playlist.</summary>
    public static bool IsUnwantedVersion(DiscoveryCandidate c) =>
        (c.MixName != null && UnwantedVersion.IsMatch(c.MixName))
        || c.DurationSeconds is > 15 * 60;

    /// <summary>
    /// Ranks a candidate against the playlist and fills <see cref="DiscoveryCandidate.Reasons"/>.
    /// Weights: source 0.30, BPM 0.25, genre 0.20, cross-source agreement 0.15, key 0.10, artist
    /// link 0.10, repeat hits up to 0.10, recency 0.05 (max 1.25; the UI shows it capped at 100%).
    /// Unknown BPM/key/genre score as neutral, so Deezer-only finds aren't buried.
    /// </summary>
    public static void Score(DiscoveryCandidate c, PlaylistDiscoveryProfile profile)
    {
        c.Reasons.Clear();
        double score = 0.30 * Math.Clamp(c.SourceStrength, 0, 1);

        if (c.Sources.HasFlag(DiscoverySource.BeatportArtist) || c.Sources.HasFlag(DiscoverySource.DeezerArtist))
        {
            score += 0.10;
            c.Reasons.Add($"More from {c.ViaArtist ?? PrimaryArtist(c.Artist)}");
        }
        else if (c.Sources.HasFlag(DiscoverySource.DeezerRelated))
        {
            score += 0.07;
            c.Reasons.Add($"Related to {c.ViaArtist}");
        }
        if (c.ChartPosition is { } pos)
            c.Reasons.Add($"#{pos} {c.Genre ?? profile.Genre} chart");
        // Agreement: two independent recommenders (Beatport's DJ-store data and Deezer's listening
        // data) pointing at the same track is the strongest signal available here.
        if (c.IsCrossSource)
        {
            score += 0.15;
            c.Reasons.Insert(0, "Beatport + Deezer agree");
        }
        else if (c.Sources.HasFlag(DiscoverySource.BeatportMatch))
        {
            score += 0.02; // buyable, and ranked on real BPM/key/genre below
        }
        if (c.HitCount > 1)
        {
            score += Math.Min(0.10, 0.04 * (c.HitCount - 1));
            c.Reasons.Add($"Suggested {c.HitCount}×");
        }

        // BPM: in the playlist's band scores full; half/double time counts (87 listed = 174 DnB).
        if (c.Bpm is { } bpm && profile.BpmLow is { } lo && profile.BpmHigh is { } hi)
        {
            double off = new[] { bpm, bpm * 2, bpm / 2 }.Min(b => b < lo ? lo - b : b > hi ? b - hi : 0);
            score += 0.25 * Math.Max(0, 1 - off / 8.0);
            var shown = new[] { bpm, bpm * 2, bpm / 2 }.OrderBy(b => Math.Abs(b - (profile.BpmMedian ?? b))).First();
            c.Reasons.Add(off <= 0.5 ? $"{shown:0} BPM" : $"{shown:0} BPM (off)");
        }
        else score += 0.25 * 0.4;

        if (c.Genre != null && profile.Genre != null)
        {
            if (string.Equals(c.Genre, profile.Genre, StringComparison.OrdinalIgnoreCase)) score += 0.20;
            else c.Reasons.Add(c.Genre);
        }
        else score += 0.20 * 0.4;

        if (c.CamelotKey != null && profile.CamelotKeys.Count > 0)
        {
            var best = profile.CamelotKeys.Select(k => KeyConverter.GetHarmonicRelation(k, c.CamelotKey)).Max();
            score += 0.10 * (best == HarmonicRelation.Exact ? 1.0 : best == HarmonicRelation.Compatible ? 0.8 : 0.2);
            c.Reasons.Add(best == HarmonicRelation.None ? c.CamelotKey : $"{c.CamelotKey} fits");
        }
        else score += 0.10 * 0.4;

        if (c.ReleaseDate is { } released && released > DateTime.UtcNow.AddYears(-1))
        {
            score += 0.05;
            c.Reasons.Add("New");
        }

        // Not capped: the strongest matches exceed 1 and must still sort against each other.
        c.Score = Math.Round(score, 4);
    }

    private async Task ConfirmOnBeatportAsync(List<DiscoveryCandidate> candidates, CancellationToken ct)
    {
        var searches = await Task.WhenAll(candidates.Select(c =>
            _beatport.SearchTracksAsync($"{PrimaryArtist(c.Artist)} {StripMixInfo(c.Title)}", ct))).ConfigureAwait(false);
        for (int i = 0; i < candidates.Count; i++)
        {
            var match = FindSameTrack(candidates[i], searches[i].Select(r => r.Candidate));
            if (match != null) ApplyBeatportMatch(candidates[i], match);
        }
    }

    /// <summary>The search result that is the same recording (same credited artist and title incl. mix), or null.</summary>
    public static DiscoveryCandidate? FindSameTrack(DiscoveryCandidate wanted, IEnumerable<DiscoveryCandidate> results)
    {
        var artists = SplitArtists(wanted.Artist).Select(NormalizeArtist).ToHashSet();
        var title = NormalizeTitle(wanted.SearchTitle);
        return results.FirstOrDefault(r =>
            NormalizeTitle(r.SearchTitle) == title
            && SplitArtists(r.Artist).Any(a => artists.Contains(NormalizeArtist(a))));
    }

    /// <summary>Copies Beatport's facts onto a candidate found elsewhere. Beatport's suggestion flags aren't copied — a lookup isn't a recommendation.</summary>
    public static void ApplyBeatportMatch(DiscoveryCandidate target, DiscoveryCandidate beatport)
    {
        target.Sources |= DiscoverySource.BeatportMatch;
        target.Bpm ??= beatport.Bpm;
        target.CamelotKey ??= beatport.CamelotKey;
        target.Genre ??= beatport.Genre;
        target.Label ??= beatport.Label;
        target.ReleaseDate ??= beatport.ReleaseDate;
        target.BeatportUrl ??= beatport.BeatportUrl;
        target.Price ??= beatport.Price;
        target.MixName ??= beatport.MixName;
        target.PreviewUrl ??= beatport.PreviewUrl;
    }

    private static string BuildSummary(PlaylistDiscoveryProfile p, List<string> seeds, int found, int owned)
    {
        var parts = new List<string>();
        if (p.Genre != null) parts.Add(p.Genre);
        if (p.BpmLow is { } lo && p.BpmHigh is { } hi) parts.Add($"{lo:0}–{hi:0} BPM");
        if (seeds.Count > 0) parts.Add("via " + string.Join(", ", seeds.Take(4)) + (seeds.Count > 4 ? "…" : ""));
        if (owned > 0) parts.Add($"{owned} already owned hidden");
        return string.Join(" · ", parts);
    }

    // ── Normalisation ───────────────────────────────────────────────────────

    private static readonly Regex ArtistSplit = new(@"\s*(?:,|;|\bfeat\.?|\bft\.?|\bfeaturing\b|\bvs\.?|\s x\s)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // The default version, in both spellings libraries use: "Tesla (Original Mix)" and "Tesla - Original Mix".
    private const string DefaultMixNames = @"original mix|original version|original|extended mix|extended version|extended|radio edit|radio mix|radio version|club mix";
    private static readonly Regex DefaultMix = new(
        $@"[\(\[]\s*(?:{DefaultMixNames})\s*[\)\]]|\s+-\s+(?:{DefaultMixNames})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeatInTitle = new(@"[\(\[]?\s*\b(feat\.?|ft\.?|featuring)\s+[^\)\]]*[\)\]]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    public static IEnumerable<string> SplitArtists(string? artist) =>
        string.IsNullOrWhiteSpace(artist)
            ? Enumerable.Empty<string>()
            : ArtistSplit.Split(artist).Select(a => a.Trim()).Where(a => a.Length > 0);

    public static string PrimaryArtist(string? artist) => SplitArtists(artist).FirstOrDefault() ?? artist ?? string.Empty;

    public static string NormalizeArtist(string? artist)
    {
        var s = Fold(artist);
        if (s.StartsWith("the ")) s = s[4..];
        return NonAlnum.Replace(s, "");
    }

    /// <summary>
    /// Title without the default-mix tag or featured artists, lowercased, punctuation removed.
    /// Remix/VIP tags stay, but two store spellings of the same version are unified (see
    /// <see cref="CanonicalizeVersions"/>).
    /// </summary>
    public static string NormalizeTitle(string? title) =>
        NonAlnum.Replace(Fold(CanonicalizeVersions(FeatInTitle.Replace(DefaultMix.Replace(title ?? string.Empty, " "), " "))), "");

    private static readonly Regex VersionPart = new(@"[\(\[]([^\)\]]+)[\)\]]|\s-\s([^\(\)\[\]]+)$", RegexOptions.Compiled);
    private static readonly Regex VersionKeyword = new(
        @"\b(remix|rmx|mix|edit|vip|dub|version|bootleg|flip|rework|refix|instrumental|acapella|live|remaster(ed)?|re-?recorded|cover|extended)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExtendedRemix = new(@"^(.+?)\s+extended\s+(?:re)?mix$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExtendedEdit = new(@"^(.+?)\s+extended\s+edit$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CreditShape = new(@"\s(x|&|and|vs\.?)\s|,", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Unifies version tags that name the same recording differently, verified against the real
    /// library: Beatport's "(SOTA Extended Mix)" is the library's "(SOTA Remix)", and Beatport's
    /// "Desire (Sub Focus x Dimension)" is just an artist credit in the version slot, i.e. the
    /// original. Anything else ("(VIP)", "(Red Bull Symphonic 2025)", "(Instrumental)") stays.
    /// </summary>
    public static string CanonicalizeVersions(string title) =>
        VersionPart.Replace(title, m =>
        {
            var isBracket = m.Groups[1].Success;
            var content = (isBracket ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            string? replaced = null;
            if (ExtendedRemix.Match(content) is { Success: true } er) replaced = er.Groups[1].Value + " remix";
            else if (ExtendedEdit.Match(content) is { Success: true } ee) replaced = ee.Groups[1].Value + " edit";
            else if (!VersionKeyword.IsMatch(content) && CreditShape.IsMatch(content)) return " ";
            if (replaced == null) return m.Value;
            return isBracket ? $" ({replaced})" : $" - {replaced}";
        });

    public static string StripMixInfo(string title) => DefaultMix.Replace(title, " ").Trim();

    /// <summary>One key per credited artist, so "A, B - Title" and "B - Title" match.</summary>
    public static IEnumerable<string> IdentityKeys(string? artist, string? title)
    {
        var t = NormalizeTitle(title);
        if (t.Length == 0) yield break;
        foreach (var a in SplitArtists(artist))
        {
            var n = NormalizeArtist(a);
            if (n.Length > 0) yield return n + "|" + t;
        }
    }

    private static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Replace("&", " and ");
    }

    private static string? ToCamelot(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var c = KeyConverter.ToCamelot(key);
        return Regex.IsMatch(c, "^(1[0-2]|[1-9])[AB]$") ? c : null;
    }
}
