using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services.Audio
{
    public class StemPreferenceService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;

        public StemPreferenceService(IDbContextFactory<AppDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<StemPreference> GetPreferenceAsync(string trackId)
        {
            using var context = await _dbFactory.CreateDbContextAsync();
            var entry = await context.StemPreferences
                .FirstOrDefaultAsync(p => p.TrackUniqueHash == trackId);

            if (entry == null) return new StemPreference();

            return new StemPreference
            {
                AlwaysMuted = JsonSerializer.Deserialize<List<StemType>>(entry.AlwaysMutedJson) ?? new(),
                AlwaysSolo = JsonSerializer.Deserialize<List<StemType>>(entry.AlwaysSoloJson) ?? new()
            };
        }

        public async Task SavePreferenceAsync(string trackId, StemPreference pref)
        {
            using var context = await _dbFactory.CreateDbContextAsync();
            var entry = await context.StemPreferences
                .FirstOrDefaultAsync(p => p.TrackUniqueHash == trackId);

            if (entry == null)
            {
                entry = new StemPreferenceEntity
                {
                    Id = Guid.NewGuid(),
                    TrackUniqueHash = trackId
                };
                context.StemPreferences.Add(entry);
            }

            entry.AlwaysMutedJson = JsonSerializer.Serialize(pref.AlwaysMuted);
            entry.AlwaysSoloJson = JsonSerializer.Serialize(pref.AlwaysSolo);
            entry.LastModified = DateTime.Now;

            await context.SaveChangesAsync();
        }
    }

    public class StemPreference
    {
        public List<StemType> AlwaysMuted { get; set; } = new();
        public List<StemType> AlwaysSolo { get; set; } = new();
    }
}
