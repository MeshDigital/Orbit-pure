using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Services.Enrichment.Providers;
using Xunit;

namespace SLSKDONET.Tests.Services.Enrichment;

/// <summary>
/// Manual only: hits the real Deezer and MusicBrainz APIs (two or three requests each) for one
/// well-known ISRC (Daft Punk – Get Lucky). Remove the Skip locally to check the parsers against
/// the live payloads; never enabled in CI.
/// </summary>
public class LiveProviderSmokeTests
{
    private const string Isrc = "USQX91300108";
    private static readonly EnrichmentTrack Track = new() { Hash = "live", Artist = "Daft Punk", Title = "Get Lucky", Isrc = Isrc, DurationSeconds = 369 };

    [Fact(Skip = "Manual: real network (Deezer)")]
    public async Task Deezer_Live_IsrcLookup()
    {
        var deezer = new DeezerProvider(new HttpClient(), new RateLimitAndBreakerTests.NoCache(), NullLogger<DeezerProvider>.Instance);
        var result = await deezer.LookupAsync(Track, new KnownIds(), CancellationToken.None);
        Assert.Equal(LookupStatus.Matched, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Candidate!.ArtworkUrl));
    }

    [Fact(Skip = "Manual: real network (MusicBrainz, 1 request/second)")]
    public async Task MusicBrainz_Live_IsrcLookup()
    {
        var mb = new MusicBrainzProvider(new HttpClient(), new RateLimitAndBreakerTests.NoCache(), NullLogger<MusicBrainzProvider>.Instance);
        var result = await mb.LookupAsync(Track, new KnownIds(), CancellationToken.None);
        Assert.Equal(LookupStatus.Matched, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Candidate!.MusicBrainzRecordingId));
    }
}
