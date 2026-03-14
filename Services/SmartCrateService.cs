using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Phase 23: Manages Smart Crates and their dynamic evaluation.
/// </summary>
public class SmartCrateService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<SmartCrateService> _logger;

    public SmartCrateService(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<SmartCrateService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
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

            using var context = await _dbContextFactory.CreateDbContextAsync();
            
            // Start with all tracks that have Analysis
            var query = context.LibraryEntries
                .Include(t => t.AudioFeatures)
                .Where(t => t.AudioFeatures != null);

            // 1. Mood
            if (!string.IsNullOrEmpty(rules.Mood))
            {
                query = query.Where(t => t.AudioFeatures!.MoodTag == rules.Mood);
            }

            // 2. SubGenre (Style)
            if (!string.IsNullOrEmpty(rules.SubGenre))
            {
                // Note: DetectedSubGenre is on LibraryEntryEntity directly in latest schema
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
