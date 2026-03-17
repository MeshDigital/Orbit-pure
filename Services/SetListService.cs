using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services
{
    public class SetListService
    {
        private readonly CrashRecoveryJournal _crashJournal;
        private readonly SearchOrchestrationService _searchOrchestrator;

        public SetListService(CrashRecoveryJournal crashJournal, SearchOrchestrationService searchOrchestrator)
        {
            _crashJournal = crashJournal;
            _searchOrchestrator = searchOrchestrator;
        }

        public async Task<SetListEntity> CreateSetListAsync(string name)
        {
            using var context = new AppDbContext();
            var setList = new SetListEntity { Name = name };
            context.SetLists.Add(setList);
            await context.SaveChangesAsync();
            return setList;
        }

        public async Task<List<SetListEntity>> GetAllSetListsAsync()
        {
            using var context = new AppDbContext();
            return await context.SetLists
                .Include(s => s.Tracks.OrderBy(t => t.Position))
                .OrderByDescending(s => s.LastModifiedAt)
                .ToListAsync();
        }

        public async Task<SetListEntity?> GetSetListAsync(Guid id)
        {
            using var context = new AppDbContext();
            return await context.SetLists
                .Include(s => s.Tracks.OrderBy(t => t.Position))
                .FirstOrDefaultAsync(s => s.Id == id);
        }

        public async Task UpdateSetListAsync(SetListEntity setList)
        {
            using var context = new AppDbContext();
            setList.LastModifiedAt = DateTime.UtcNow;
            context.SetLists.Update(setList);
            await context.SaveChangesAsync();
        }

        public async Task AddTrackToSetAsync(Guid setListId, string trackUniqueHash, int? position = null)
        {
            using var context = new AppDbContext();
            var setList = await context.SetLists.Include(s => s.Tracks).FirstOrDefaultAsync(s => s.Id == setListId);
            if (setList == null) return;

            int pos = position ?? (setList.Tracks.Any() ? setList.Tracks.Max(t => t.Position) + 1 : 0);

            var setTrack = new SetTrackEntity
            {
                SetListId = setListId,
                TrackUniqueHash = trackUniqueHash,
                Position = pos
            };

            context.SetTracks.Add(setTrack);
            setList.LastModifiedAt = DateTime.UtcNow;

            await context.SaveChangesAsync();
        }

        public async Task RemoveTrackFromSetAsync(Guid setTrackId)
        {
            using var context = new AppDbContext();
            var track = await context.SetTracks.FindAsync(setTrackId);
            if (track == null) return;

            context.SetTracks.Remove(track);
            
            var setList = await context.SetLists.Include(s => s.Tracks).FirstOrDefaultAsync(s => s.Id == track.SetListId);
            if (setList != null)
            {
                setList.LastModifiedAt = DateTime.UtcNow;
            }

            await context.SaveChangesAsync();
        }
    }
}
