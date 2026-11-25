using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Library.Files;

/// <summary>
/// LF3 (#345): after analysis writes the BPM/key/energy for a track, re-file it to the template path
/// when the naming template uses {key}/{bpm}/{bpm_bracket}, and the track isn't locked/exported or a
/// linked folder. Runs on the <see cref="TrackAnalysisCompletedEvent"/> bus.
/// </summary>
public sealed class PostAnalysisRefileService : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly AppConfig _config;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly PathProviderService _pathProvider;
    private readonly FileRelocationService _relocation;
    private readonly ILogger<PostAnalysisRefileService> _logger;
    private readonly IDisposable _subscription;

    public PostAnalysisRefileService(
        IEventBus eventBus,
        AppConfig config,
        IDbContextFactory<AppDbContext> dbFactory,
        PathProviderService pathProvider,
        FileRelocationService relocation,
        ILogger<PostAnalysisRefileService> logger)
    {
        _eventBus = eventBus;
        _config = config;
        _dbFactory = dbFactory;
        _pathProvider = pathProvider;
        _relocation = relocation;
        _logger = logger;
        _subscription = _eventBus.GetEvent<TrackAnalysisCompletedEvent>()
            .Subscribe(evt => _ = OnAnalysisCompletedAsync(evt));
    }

    public async Task OnAnalysisCompletedAsync(TrackAnalysisCompletedEvent evt)
    {
        try
        {
            if (!evt.Success) return;
            if (!_config.RefileAfterAnalysis) return;
            if (!RefilePolicy.TemplateUsesAnalysisTokens(_config.NameFormat)) return;

            var hash = evt.TrackGlobalId;
            if (string.IsNullOrWhiteSpace(hash)) return;

            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == hash).ConfigureAwait(false);
            if (entry is null || string.IsNullOrEmpty(entry.FilePath)) return;
            if (entry.PathLockedAt is not null) return; // exported/protected
            if (string.Equals(entry.FileOrigin, FileOriginDeriver.LinkedFolder, StringComparison.OrdinalIgnoreCase)) return;

            var features = await db.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == hash).ConfigureAwait(false);
            var target = _pathProvider.GetTrackPath(BuildTrack(entry, features));
            if (string.Equals(target, entry.FilePath, StringComparison.OrdinalIgnoreCase)) return;

            var plan = await _relocation.PlanAsync(new[] { new RelocationRequest(hash, entry.FilePath, target) }).ConfigureAwait(false);
            if (plan.Moves.Count == 0) return;
            await _relocation.ExecuteAsync(plan).ConfigureAwait(false);

            _logger.LogInformation("Re-filed {Hash} after analysis: {Target}", hash, target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-analysis re-file failed for {Hash}; leaving the track in place.", evt.TrackGlobalId);
        }
    }

    private static Track BuildTrack(LibraryEntryEntity entry, AudioFeaturesEntity? features) => new()
    {
        Artist = entry.Artist,
        Title = entry.Title,
        Album = entry.Album,
        BPM = features is { Bpm: > 0 } ? features.Bpm : null,
        MusicalKey = features is { Key.Length: > 0 } ? features.Key : null,
        Energy = features is not null ? features.Energy : null,
        Filename = Path.GetFileName(entry.FilePath),
        Length = entry.DurationSeconds,
        Bitrate = entry.Bitrate,
    };

    public void Dispose() => _subscription.Dispose();
}
