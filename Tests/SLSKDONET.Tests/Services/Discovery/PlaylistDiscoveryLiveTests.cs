using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Discovery;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Services.Discovery;

/// <summary>
/// Hits the real Beatport and Deezer sites. Opt-in (set ORBIT_LIVE_DISCOVER=1) so the normal
/// suite never depends on the network; run it when Beatport's page format is suspected to change.
/// </summary>
public class PlaylistDiscoveryLiveTests
{
    private readonly ITestOutputHelper _out;
    public PlaylistDiscoveryLiveTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task LiveSources_ReturnParseableTracks_AndSometimesAgree()
    {
        if (Environment.GetEnvironmentVariable("ORBIT_LIVE_DISCOVER") != "1") return;

        var beatport = new BeatportCatalogClient(new HttpClient(), NullLogger<BeatportCatalogClient>.Instance);
        var deezer = new DeezerCatalogClient(new HttpClient(), NullLogger<DeezerCatalogClient>.Instance);
        var seeds = new[] { "Metrik", "Sub Focus", "Wilkinson" };
        var all = new List<DiscoveryCandidate>();

        foreach (var seed in seeds)
        {
            var search = await beatport.SearchTracksAsync(seed);
            Assert.NotEmpty(search);
            var artist = search.SelectMany(r => r.Refs.Artists).First(a => a.Name == seed);
            var tracks = await beatport.ArtistTracksAsync(artist.Id);
            Assert.NotEmpty(tracks);
            foreach (var t in tracks) { t.Candidate.Sources = DiscoverySource.BeatportArtist; t.Candidate.SourceStrength = 0.8; t.Candidate.ViaArtist = seed; all.Add(t.Candidate); }

            var dz = await deezer.FindArtistAsync(seed);
            Assert.NotNull(dz);
            foreach (var t in await deezer.TopTracksAsync(dz!.Id, 10)) { t.Sources = DiscoverySource.DeezerArtist; t.SourceStrength = 0.7; t.ViaArtist = seed; all.Add(t); }
            foreach (var r in await deezer.RelatedArtistsAsync(dz.Id, 4))
                foreach (var t in await deezer.TopTracksAsync(r.Id, 3)) { t.Sources = DiscoverySource.DeezerRelated; t.SourceStrength = 0.5; t.ViaArtist = seed; all.Add(t); }
        }

        var chart = await beatport.GenreTop100Async(1, "drum-bass");
        Assert.True(chart.Count >= 50, $"chart had {chart.Count}");
        Assert.All(chart.Take(20), c => Assert.NotNull(c.Candidate.Bpm));
        foreach (var (c, i) in chart.Select((c, i) => (c.Candidate, i))) { c.Sources = DiscoverySource.BeatportChart; c.SourceStrength = 1 - i / 100.0; c.ChartPosition = i + 1; all.Add(c); }

        var profile = new PlaylistDiscoveryProfile { Genre = "Drum & Bass" };
        PlaylistDiscoveryService.ApplyBpmRange(profile, new double[] { 172, 174, 175 });
        profile.CamelotKeys.Add("8A");
        var merged = PlaylistDiscoveryService.Merge(all);
        foreach (var c in merged) PlaylistDiscoveryService.Score(c, profile);

        _out.WriteLine($"{all.Count} raw → {merged.Count} merged, {merged.Count(c => c.IsCrossSource)} cross-source");
        foreach (var c in merged.OrderByDescending(c => c.Score).Take(15))
            _out.WriteLine($"{c.Score:0.00} {(c.IsCrossSource ? "★" : " ")} {c.Artist} – {c.SearchTitle} | {string.Join(" · ", c.Reasons)}");
    }
}

/// <summary>
/// Checks the owned-track filter against a snapshot of the real library. Opt-in: set
/// ORBIT_LIVE_DISCOVER_DB to a copy of library.db (never the live file). Prints what was hidden
/// and every "new" suggestion that shares an artist and title stem with something owned, which
/// is where a missed duplicate would show up.
/// </summary>
public class OwnedTrackIndexLibraryCheck
{
    private readonly ITestOutputHelper _out;
    public OwnedTrackIndexLibraryCheck(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task RealLibrary_OwnedFilter_Report()
    {
        var dbPath = Environment.GetEnvironmentVariable("ORBIT_LIVE_DISCOVER_DB");
        if (string.IsNullOrEmpty(dbPath)) return;

        var owned = new OwnedTrackIndex();
        var rows = new List<(string Artist, string Title)>();
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            conn.Open();
            foreach (var table in new[] { "LibraryEntries", "PlaylistTracks" })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT Artist, Title FROM {table}";
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            }
        }
        foreach (var (a, t) in rows) owned.Add(a, t);

        var topArtists = rows.SelectMany(r => PlaylistDiscoveryService.SplitArtists(r.Artist))
            .GroupBy(PlaylistDiscoveryService.NormalizeArtist).Where(g => g.Key.Length > 2)
            .OrderByDescending(g => g.Count()).Take(8).Select(g => g.First()).ToList();
        _out.WriteLine($"Index: {owned.Count} rows. Artists checked: {string.Join(", ", topArtists)}");

        var beatport = new BeatportCatalogClient(new HttpClient(), NullLogger<BeatportCatalogClient>.Instance);
        var deezer = new DeezerCatalogClient(new HttpClient(), NullLogger<DeezerCatalogClient>.Instance);
        var candidates = new List<DiscoveryCandidate>();
        foreach (var artist in topArtists)
        {
            var search = await beatport.SearchTracksAsync(artist);
            var id = search.SelectMany(s => s.Refs.Artists).FirstOrDefault(a => PlaylistDiscoveryService.NormalizeArtist(a.Name) == PlaylistDiscoveryService.NormalizeArtist(artist));
            if (id.Name != null) candidates.AddRange((await beatport.ArtistTracksAsync(id.Id)).Select(t => t.Candidate));
            var dz = await deezer.FindArtistAsync(artist);
            if (dz != null) candidates.AddRange(await deezer.TopTracksAsync(dz.Id, 25));
        }
        var merged = PlaylistDiscoveryService.Merge(candidates);
        var hidden = merged.Where(owned.Contains).ToList();
        var shown = merged.Except(hidden).ToList();
        _out.WriteLine($"Candidates {merged.Count}: {hidden.Count} hidden as owned, {shown.Count} shown as new");

        // Possible misses: a shown track whose artist overlaps an owned row and whose base title
        // (before any bracket/dash) matches that row's base title.
        static string Stem(string t) => PlaylistDiscoveryService.NormalizeTitle(System.Text.RegularExpressions.Regex.Split(t, @"\s[\(\[-]")[0]);
        var ownedByStem = rows.Where(r => Stem(r.Title).Length > 0).GroupBy(r => Stem(r.Title)).ToDictionary(g => g.Key, g => g.ToList());
        _out.WriteLine("--- shown, but an owned track has the same stem (check these) ---");
        foreach (var c in shown)
        {
            if (!ownedByStem.TryGetValue(Stem(c.Title), out var matches)) continue;
            var sameArtist = matches.Where(m => PlaylistDiscoveryService.SplitArtists(c.Artist)
                .Any(a => PlaylistDiscoveryService.NormalizeArtist(m.Artist).Contains(PlaylistDiscoveryService.NormalizeArtist(a)))).ToList();
            if (sameArtist.Count == 0) continue;
            _out.WriteLine($"NEW  {c.Artist} – {c.SearchTitle}   | owned: {string.Join(" ; ", sameArtist.Select(m => $"{m.Artist} – {m.Title}").Distinct().Take(3))}");
        }
        _out.WriteLine("--- sample hidden ---");
        foreach (var c in hidden.Take(15)) _out.WriteLine($"OWN  {c.Artist} – {c.SearchTitle}");
    }
}
