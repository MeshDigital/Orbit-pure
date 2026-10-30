using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Export;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): the Export Agent. On <see cref="SetExportRequestedEvent"/> it
/// resolves the SetProject slots to exportable tracks, annotates each slot's <c>Comments</c> with
/// the incoming transition's recScore/confidence/freshness, and delegates the physical
/// USB/Rekordbox write to <see cref="UsbExportOrchestrator"/>.
/// </summary>
public sealed class ExportAgent : IKnowledgeAgent, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<ExportAgent> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly UsbExportOrchestrator _usbExport;

    private IDisposable? _subscription;

    public string Name => nameof(ExportAgent);

    public ExportAgent(
        IEventBus eventBus,
        ILogger<ExportAgent> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        UsbExportOrchestrator usbExport)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;
        _usbExport = usbExport;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = _eventBus
            .GetEvent<SetExportRequestedEvent>()
            .Subscribe(evt => _ = OnExportRequestedAsync(evt));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    private async Task OnExportRequestedAsync(SetExportRequestedEvent evt)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

            var set = await db.SetLists.FirstOrDefaultAsync(s => s.Id == evt.SetProjectId).ConfigureAwait(false);
            if (set is null)
                return;

            var tracks = await KnowledgeLifecycleRepository.BuildAnnotatedSetExportAsync(db, evt.SetProjectId).ConfigureAwait(false);
            var mode = evt.FilesAndXml ? ExportMode.FilesAndXml : ExportMode.XmlOnly;

            await _usbExport.ExportAsync(set.Name, tracks, evt.DestinationRoot, mode).ConfigureAwait(false);

            _logger.LogInformation(
                "[ExportAgent] exported set {Set} ({Count} tracks) to {Dest} ({Mode})",
                evt.SetProjectId, tracks.Count, evt.DestinationRoot, mode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ExportAgent] failed to export set {Set}", evt.SetProjectId);
        }
    }
}
