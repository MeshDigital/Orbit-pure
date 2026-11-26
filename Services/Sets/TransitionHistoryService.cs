using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Repositories;

namespace SLSKDONET.Services.Sets;

/// <summary>A transition saved for the same two tracks somewhere else, and where.</summary>
public sealed record TransitionFromHistory(PlaylistTrackTransition Transition, string SourceName, bool IsSet);

/// <summary>
/// Finds the most recently saved transition for a track pair across every playlist and set
/// (Epic #517 / FO1b #519), reading directly from the unified <c>Transitions</c> table.
/// </summary>
public interface ITransitionHistoryService
{
    /// <summary>Latest saved A→B transition by track hash, excluding the pair being edited; null when none.</summary>
    Task<TransitionFromHistory?> FindLatestAsync(string fromHash, string toHash, Guid excludeOutgoingId, Guid excludeIncomingId);
}

public sealed class TransitionHistoryService : ITransitionHistoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public TransitionHistoryService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<TransitionFromHistory?> FindLatestAsync(string fromHash, string toHash, Guid excludeOutgoingId, Guid excludeIncomingId)
    {
        if (string.IsNullOrEmpty(fromHash) || string.IsNullOrEmpty(toHash))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var candidates = await db.Transitions
            .AsNoTracking()
            .Where(t => t.UseAsPairDefault
                     && t.OutgoingTrackHash == fromHash
                     && t.IncomingTrackHash == toHash)
            .ToListAsync()
            .ConfigureAwait(false);

        var latest = candidates
            .Where(t => !(t.OutgoingItemId == excludeOutgoingId && t.IncomingItemId == excludeIncomingId))
            .OrderByDescending(t => t.UpdatedAtUtc)
            .FirstOrDefault();

        if (latest is null)
            return null;

        string sourceName;
        if (latest.ScopeKind == TransitionScopeKind.Set)
        {
            sourceName = await db.SetLists
                .AsNoTracking()
                .Where(s => s.Id == latest.ScopeId)
                .Select(s => s.Name)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false) ?? "a set";
        }
        else
        {
            sourceName = await db.Projects
                .AsNoTracking()
                .Where(p => p.Id == latest.ScopeId)
                .Select(p => p.SourceTitle)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false) ?? "a playlist";
        }

        return new TransitionFromHistory(
            TransitionRepository.ToDomain(latest),
            sourceName,
            IsSet: latest.ScopeKind == TransitionScopeKind.Set);
    }
}
