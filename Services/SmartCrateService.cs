using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;

namespace SLSKDONET.Services;

/// <summary>
/// Phase 23: Manages Smart Crates and their dynamic evaluation.
/// Extended in Epic #561 / #566 (GV5) to evaluate canonical <see cref="GenreKey"/> rules
/// against <see cref="LibraryGenreIndex"/> and migrate legacy exact <c>SubGenre</c> rules.
/// </summary>
public class SmartCrateService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<SmartCrateService> _logger;
    private readonly LibraryGenreIndex? _genreIndex;

    public SmartCrateService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<SmartCrateService> logger,
        LibraryGenreIndex? genreIndex = null)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _genreIndex = genreIndex;
    }

    /// <summary>
    /// Maps a legacy exact <see cref="SmartCrateRules.SubGenre"/> string through
    /// <see cref="GenreTaxonomy.MapFreeText"/> into a canonical <see cref="GenreKey"/>.
    /// Returns false if a <see cref="GenreKey"/> is already present or if the legacy string
    /// does not map to a known genre (unmapped values stay as-is).
    /// </summary>
    public static bool TryMigrateLegacySubGenre(SmartCrateRules rules, out GenreKey? migratedKey)
    {
        migratedKey = null;
        if (rules.GetGenreKey().HasValue)
            return false;
        if (string.IsNullOrWhiteSpace(rules.SubGenre))
            return false;

        var decision = GenreTaxonomy.MapFreeText(rules.SubGenre);
        if (decision.IsJunk || decision.Weight <= 0 || (decision.Bucket == GenreBucket.Other && string.IsNullOrWhiteSpace(decision.Subgenre)))
            return false;

        migratedKey = new GenreKey(decision.Bucket, decision.Subgenre);
        return true;
    }

    public async Task<List<SmartCrateDefinitionEntity>> GetAllCratesAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.SmartCrateDefinitions.OrderBy(C => C.SortOrder).ToListAsync();
    }

    public async Task<SmartCrateDefinitionEntity> CreateCrateAsync(string name, SmartCrateRules rules)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        var entity = new SmartCrateDefinitionEntity
        {
            Name = name,
            RulesJson = JsonSerializer.Serialize(rules),
            SortOrder = await context.SmartCrateDefinitions.CountAsync()
        };
        context.SmartCrateDefinitions.Add(entity);
        await context.SaveChangesAsync();
        return entity;
    }

    public async Task DeleteCrateAsync(Guid id)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        var entity = await context.SmartCrateDefinitions.FindAsync(id);
        if (entity != null)
        {
            context.SmartCrateDefinitions.Remove(entity);
            await context.SaveChangesAsync();
        }
    }

    public async Task UpdateCrateAsync(SmartCrateDefinitionEntity crate, SmartCrateRules rules)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        var entity = await context.SmartCrateDefinitions.FindAsync(crate.Id);
        if (entity != null)
        {
            entity.Name = crate.Name;
            entity.RulesJson = JsonSerializer.Serialize(rules);
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Evaluates a crate's rules against the database and returns matching Global IDs.
    /// </summary>
    public async Task<List<string>> GetMatchingTrackIdsAsync(SmartCrateDefinitionEntity crate)
    {
        try
        {
            var rules = JsonSerializer.Deserialize<SmartCrateRules>(crate.RulesJson);
            if (rules == null) return new List<string>();

            var effectiveGenreKey = rules.GetGenreKey();
            var usedMigratedLegacySubGenre = false;
            if (!effectiveGenreKey.HasValue && TryMigrateLegacySubGenre(rules, out var migrated))
            {
                effectiveGenreKey = migrated;
                usedMigratedLegacySubGenre = true;
            }

            IReadOnlySet<string>? genreHashes = null;
            if (effectiveGenreKey.HasValue && _genreIndex != null)
            {
                if (!_genreIndex.IsReady)
                {
                    await _genreIndex.BuildAsync().ConfigureAwait(false);
                }

                genreHashes = _genreIndex.GetHashes(effectiveGenreKey.Value);
                if (genreHashes.Count == 0)
                    return new List<string>();
            }

            using var context = await _dbContextFactory.CreateDbContextAsync();

            var requiresAudioFeatures = !effectiveGenreKey.HasValue
                || !string.IsNullOrEmpty(rules.Mood)
                || rules.MinEnergy.HasValue
                || rules.MaxEnergy.HasValue;

            IQueryable<LibraryEntryEntity> query = context.LibraryEntries.Include(t => t.AudioFeatures);
            if (requiresAudioFeatures)
            {
                query = query.Where(t => t.AudioFeatures != null);
            }

            if (genreHashes != null)
            {
                var hashList = genreHashes.ToList();
                query = query.Where(t => hashList.Contains(t.UniqueHash));
            }

            // 1. Mood
            if (!string.IsNullOrEmpty(rules.Mood))
            {
                query = query.Where(t => t.AudioFeatures!.MoodTag == rules.Mood);
            }

            // 2. Legacy SubGenre fallback (only when not evaluated via canonical GenreKey)
            if (!string.IsNullOrEmpty(rules.SubGenre) && genreHashes == null && !usedMigratedLegacySubGenre)
            {
                query = query.Where(t => t.DetectedSubGenre == rules.SubGenre);
            }

            // 3. BPM
            if (rules.MinBpm.HasValue)
                query = query.Where(t => t.BPM >= rules.MinBpm.Value);
            if (rules.MaxBpm.HasValue)
                query = query.Where(t => t.BPM <= rules.MaxBpm.Value);

            // 4. Energy
            if (rules.MinEnergy.HasValue)
                query = query.Where(t => t.AudioFeatures!.Energy >= rules.MinEnergy.Value);
            if (rules.MaxEnergy.HasValue)
                query = query.Where(t => t.AudioFeatures!.Energy <= rules.MaxEnergy.Value);

            // 5. Instrumental
            if (rules.OnlyInstrumental)
            {
                query = query.Where(t => t.InstrumentalProbability > 0.8);
            }
            
            // Execute and return UniqueHash
            return await query.Select(t => t.UniqueHash).ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate crate {Name}", crate.Name);
            return new List<string>();
        }
    }
}
