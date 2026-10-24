using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Services.Enrichment;
using Xunit;

namespace SLSKDONET.Tests.Services.Enrichment;

/// <summary>Answers HTTP requests from a function and records them. No real network.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public ConcurrentQueue<string> Requests { get; } = new();

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!.ToString());
        return Task.FromResult(_respond(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}

/// <summary>A provider with scripted answers, counting calls.</summary>
internal sealed class FakeProvider : IMetadataProvider
{
    private readonly Func<EnrichmentTrack, KnownIds, ProviderLookup> _answer;
    private int _calls;

    public FakeProvider(string name, MetadataField supplies, Func<EnrichmentTrack, KnownIds, ProviderLookup> answer)
    {
        Name = name;
        Supplies = supplies;
        _answer = answer;
        Runtime = new ProviderRuntime(name, RatePolicy.Unlimited);
    }

    public string Name { get; }
    public string DisplayName => Name;
    public string Description => "fake";
    public MetadataField Supplies { get; }
    public ProviderRuntime Runtime { get; }
    public ProviderAvailability Availability { get; set; } = ProviderAvailability.Ready;
    public int Calls => Volatile.Read(ref _calls);
    public ProviderAvailability GetAvailability() => Availability;

    public Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(_answer(track, ids));
    }
}

/// <summary>A throwaway library database under the suite's isolated temp folder.</summary>
internal sealed class TempLibraryDb : IDisposable
{
    public string Directory { get; }
    public IDbContextFactory<AppDbContext> Factory { get; }

    public TempLibraryDb()
    {
        var root = Path.GetDirectoryName(OrbitPaths.LibraryDbPath)!;
        Assert.Contains("orbit-tests", root); // never the user's AppData library
        Directory = Path.Combine(root, "enrichment-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Directory, "library.db")};Pooling=False").Options;
        Factory = new OptionsFactory(options);
        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void AddEntry(string hash, string artist, string title, Action<LibraryEntryEntity>? configure = null)
    {
        using var db = Factory.CreateDbContext();
        var e = new LibraryEntryEntity
        {
            UniqueHash = hash, Artist = artist, Title = title, Album = string.Empty,
            FilePath = Path.Combine(Directory, hash + ".flac"), AddedAt = DateTime.UtcNow, DurationSeconds = 300,
        };
        configure?.Invoke(e);
        db.LibraryEntries.Add(e);
        db.SaveChanges();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { /* temp folder; the suite's folder is removed at exit */ }
    }

    private sealed class OptionsFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public OptionsFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}

internal static class EnrichmentSettingsFor
{
    public static EnrichmentSettings Providers(params string[] names) =>
        new(true, names, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
}
