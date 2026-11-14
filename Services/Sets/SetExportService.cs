using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Services.Export;
using SLSKDONET.Services.KnowledgeLifecycle;

namespace SLSKDONET.Services.Sets;

/// <summary>What a Sets export did, for an honest notification.</summary>
/// <param name="Destination">The XML file, or the folder the audio and PIONEER/rekordbox.xml went to.</param>
/// <param name="MissingFiles">Slots whose file isn't on disk (left out of the export).</param>
public sealed record SetExportResult(string SetName, int TrackCount, int MissingFiles, string Destination);

/// <summary>
/// Exports a set (rebuild plan P5, #206) through the existing Rekordbox/USB export path
/// (<see cref="UsbExportOrchestrator"/>): the slots in order as one Rekordbox playlist, with the
/// library's metadata, cues and beat grid. Transition notes in the comment field are opt-in.
/// Called directly from the Sets page rather than through the fire-and-forget
/// <c>SetExportRequestedEvent</c>, so the DJ gets the real result or the real error.
/// </summary>
public sealed class SetExportService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly UsbExportOrchestrator _export;
    private readonly ILogger<SetExportService> _logger;

    public SetExportService(IDbContextFactory<AppDbContext> dbFactory, UsbExportOrchestrator export, ILogger<SetExportService> logger)
    {
        _dbFactory = dbFactory;
        _export = export;
        _logger = logger;
    }

    /// <param name="destination">XmlOnly: the .xml file to write (merged if it exists). FilesAndXml: the USB / folder root.</param>
    public async Task<SetExportResult> ExportAsync(Guid setId, string destination, ExportMode mode, bool includeTransitionNotes,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = await db.SetLists.AsNoTracking().FirstOrDefaultAsync(s => s.Id == setId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("The set no longer exists.");

        var tracks = await KnowledgeLifecycleRepository.BuildAnnotatedSetExportAsync(db, setId, includeTransitionNotes).ConfigureAwait(false);
        if (tracks.Count == 0) throw new InvalidOperationException("The set has no tracks to export.");

        var playable = tracks.Where(t => !string.IsNullOrWhiteSpace(t.ResolvedFilePath) && File.Exists(t.ResolvedFilePath)).ToList();
        var missing = tracks.Count - playable.Count;
        if (playable.Count == 0) throw new InvalidOperationException("None of the set's files are on disk.");

        _logger.LogInformation("[SetExport] {Set}: {Count} tracks ({Missing} missing) → {Dest} ({Mode}, notes {Notes})",
            set.Name, playable.Count, missing, destination, mode, includeTransitionNotes);
        await _export.ExportAsync(set.Name, playable, destination, mode, ct: ct).ConfigureAwait(false);

        var written = mode == ExportMode.FilesAndXml ? Path.Combine(destination, "PIONEER", "rekordbox.xml") : destination;
        return new SetExportResult(set.Name, playable.Count, missing, written);
    }
}
