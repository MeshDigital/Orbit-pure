using System;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Files;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Files;

/// <summary>LF3 (#345): the post-analysis re-file re-files only when the template uses analysis tokens.</summary>
[Collection(NonParallelCollection.Name)] // FreshDatabaseScope swaps the process-wide ORBIT_DB_PATH
public class PostAnalysisRefileServiceTests : IDisposable
{
    private readonly FreshDatabaseScope _scope = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-refile-{Guid.NewGuid():N}");

    public PostAnalysisRefileServiceTests()
    {
        Directory.CreateDirectory(_dir);
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private sealed class TestDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppDbContext());
    }

    private sealed class FakeEventBus : IEventBus
    {
        public IObservable<T> GetEvent<T>() => new Subject<T>();
        public void Publish<T>(T eventData) { }
    }

    private PostAnalysisRefileService MakeService(AppConfig config, FakeEventBus bus)
    {
        var pathProvider = new PathProviderService(config, new FileNameFormatter(), NullLogger<PathProviderService>.Instance);
        var relocation = new FileRelocationService(new TestDbFactory(), bus, NullLogger<FileRelocationService>.Instance);
        return new PostAnalysisRefileService(bus, config, new TestDbFactory(), pathProvider, relocation, NullLogger<PostAnalysisRefileService>.Instance);
    }

    private AppConfig MakeConfig(string nameFormat, bool refile) =>
        new() { NameFormat = nameFormat, DownloadDirectory = _dir, RefileAfterAnalysis = refile };

    [Fact]
    public async Task OnAnalysisCompleted_RefilesWhenTemplateUsesKey()
    {
        const string hash = "h-refile";
        var source = Path.Combine(_dir, "old name.flac");
        File.WriteAllText(source, "audio");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, Artist = "Artist", Title = "Title", FilePath = source });
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = hash, Bpm = 174, Key = "8A" });
            db.SaveChanges();
        }

        var bus = new FakeEventBus();
        var svc = MakeService(MakeConfig("{artist} - {title} - {key}", refile: true), bus);
        await svc.OnAnalysisCompletedAsync(new TrackAnalysisCompletedEvent(hash, Success: true));

        var expected = Path.Combine(_dir, "Artist - Title - 8A.flac");
        Assert.True(File.Exists(expected), "the track should be re-filed to the key-aware template path");
        using var verify = new AppDbContext();
        Assert.Equal(expected, verify.LibraryEntries.Single(e => e.UniqueHash == hash).FilePath);
    }

    [Fact]
    public async Task OnAnalysisCompleted_SkipsWhenToggleOffOrTemplateStatic()
    {
        const string hash = "h-skip";
        var source = Path.Combine(_dir, "Artist - Title.flac");
        File.WriteAllText(source, "audio");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, Artist = "Artist", Title = "Title", FilePath = source });
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = hash, Bpm = 174, Key = "8A" });
            db.SaveChanges();
        }

        // Toggle off → no re-file even though the template uses {key}.
        var bus = new FakeEventBus();
        var svc = MakeService(MakeConfig("{artist} - {title} - {key}", refile: false), bus);
        await svc.OnAnalysisCompletedAsync(new TrackAnalysisCompletedEvent(hash, Success: true));

        using var verify = new AppDbContext();
        Assert.Equal(source, verify.LibraryEntries.Single(e => e.UniqueHash == hash).FilePath);
    }
}
