using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Services.InputParsers;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

public class SpotifyScraperInputSourceTests
{
    private static SpotifyScraperInputSource CreateScraper(ILogger<SpotifyScraperInputSource>? logger = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        return new SpotifyScraperInputSource(logger ?? NullLogger<SpotifyScraperInputSource>.Instance, factory.Object);
    }

    [Fact]
    public void ExtractTracksFromHtml_Parses2026EmbedStateEntityTrackList_100Tracks()
    {
        var trackList = Enumerable.Range(1, 100).Select(i => new
        {
            uri = $"spotify:track:4cOdK2wGLETKBW3PvgPW{i:00}",
            title = i == 1 ? "Ok Without You 2026" : $"Track {i}",
            subtitle = i == 1 ? "Klaas,\u00A0Bodybangers" : $"Artist {i}, Featured {i}",
            duration = 170888 + i * 1000,
            entityType = "track"
        }).ToArray();

        var nextData = JsonSerializer.Serialize(new
        {
            props = new
            {
                pageProps = new
                {
                    state = new
                    {
                        data = new
                        {
                            entity = new
                            {
                                name = "Club Anthems 2026",
                                trackList
                            }
                        }
                    }
                }
            }
        });

        var html = $"<!doctype html><html><body><script id=\"__NEXT_DATA__\" type=\"application/json\">{nextData}</script></body></html>";
        var scraper = CreateScraper();

        var results = scraper.ExtractTracksFromHtml(html, "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M");

        Assert.Equal(100, results.Count);
        var first = results[0];
        Assert.Equal("Ok Without You 2026", first.Title);
        Assert.Equal("Klaas", first.Artist);
        Assert.Equal("Club Anthems 2026", first.SourceTitle);
        Assert.Equal(171888, first.CanonicalDuration);
        Assert.Equal(171, first.Length);
        Assert.Equal("spotify:track:4cOdK2wGLETKBW3PvgPW01", first.SpotifyUri);
        Assert.Equal("4cOdK2wGLETKBW3PvgPW01", first.SpotifyTrackId);
        Assert.False(string.IsNullOrWhiteSpace(first.TrackHash));
    }

    [Fact]
    public void ExtractTracksFromHtml_StillParsesLegacyInitialStateEntitiesShape()
    {
        var nextData = JsonSerializer.Serialize(new
        {
            props = new
            {
                pageProps = new
                {
                    initialState = new
                    {
                        headerData = new { title = "Legacy Playlist" },
                        entities = new
                        {
                            tracks = new
                            {
                                items = new[]
                                {
                                    new
                                    {
                                        track = new
                                        {
                                            name = "Legacy Track",
                                            artists = new[] { new { name = "Legacy Artist" } },
                                            album = new { name = "Legacy Album" },
                                            id = "1234567890123456789012"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

        var html = $"<html><body><script id=\"__NEXT_DATA__\" type=\"application/json\">{nextData}</script></body></html>";
        var scraper = CreateScraper();

        var results = scraper.ExtractTracksFromHtml(html, "https://open.spotify.com/playlist/legacy");

        Assert.Single(results);
        Assert.Equal("Legacy Track", results[0].Title);
        Assert.Equal("Legacy Artist", results[0].Artist);
        Assert.Equal("Legacy Album", results[0].Album);
        Assert.Equal("Legacy Playlist", results[0].SourceTitle);
    }

    [Fact]
    public void ExtractTracksFromHtml_UnknownShape_ReturnsEmptyAndLogsWarningWithKeys()
    {
        var loggerMock = new Mock<ILogger<SpotifyScraperInputSource>>();
        var scraper = CreateScraper(loggerMock.Object);
        var html = "<html><body><script id=\"__NEXT_DATA__\" type=\"application/json\">{\"props\":{\"pageProps\":{\"brandNewKey\":{}}}}</script></body></html>";

        var results = scraper.ExtractTracksFromHtml(html, "https://open.spotify.com/playlist/unknown");

        Assert.Empty(results);
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("brandNewKey")),
                It.IsAny<System.Exception?>(),
                It.IsAny<System.Func<It.IsAnyType, System.Exception?, string>>()),
            Times.Once);
    }
}
