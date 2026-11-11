using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Services.Discovery;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>
/// Builds the <see cref="LibraryIdentityIndex"/> for one maintenance scan: artists that appear on
/// two or more library tracks (excluding placeholders) and every distinct non-empty label. Normalised
/// with <see cref="PlaylistDiscoveryService.NormalizeArtist"/>, case-insensitive.
/// </summary>
public sealed class LibraryIdentityIndexLoader
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<LibraryIdentityIndexLoader> _logger;

    public LibraryIdentityIndexLoader(IDbContextFactory<AppDbContext> dbFactory, ILogger<LibraryIdentityIndexLoader> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<LibraryIdentityIndex> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var artists = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => e.Artist != null && e.Artist != string.Empty)
            .Select(e => e.Artist!)
            .ToListAsync(ct).ConfigureAwait(false);

        var knownArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in artists
                     .Where(a => !IsPlaceholder(a))
                     .GroupBy(NormalizeName, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() >= 2 && !string.IsNullOrWhiteSpace(group.Key))
                knownArtists.Add(group.Key);
        }

        var labels = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => e.Label != null && e.Label != string.Empty)
            .Select(e => e.Label!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var knownLabels = new HashSet<string>(
            labels.Select(NormalizeName).Where(l => !string.IsNullOrWhiteSpace(l)),
            StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("Library identity index built: {Artists} known artists, {Labels} known labels",
            knownArtists.Count, knownLabels.Count);

        return new LibraryIdentityIndex(knownArtists, knownLabels);
    }

    private static bool IsPlaceholder(string artist) =>
        string.Equals(artist, "Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artist, "Unknown", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string? s) => PlaylistDiscoveryService.NormalizeArtist(s);
}
