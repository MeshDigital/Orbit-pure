using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Services.Enrichment.Providers;

namespace SLSKDONET.Services.Setup;

/// <summary>
/// The wizard's "Metadata & artwork" step on top of the real pipeline: provider on/off and
/// auto-run go to config.ini [Enrichment] (the same keys Settings → Metadata sources writes),
/// keys to the DPAPI <see cref="MetadataCredentialStore"/>, and Start / Pause / Resume drive the
/// same <see cref="MetadataEnrichmentOrchestrator"/> the Analysis page's card uses.
/// </summary>
public sealed class EnrichmentSetupGateway : ISetupEnrichmentGateway, IDisposable
{
    /// <summary>"Gives · needs · limit" for each source, one line each.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Summaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["tags"] = "Embedded cover, album, label, year, genre · reads your files, no network · always on",
        ["deezer"] = "Cover art, album, label, year, genre, ISRC · no key · about 7 requests a second",
        ["musicbrainz"] = "Label, catalogue number, year, Cover Art Archive covers · no key · 1 request a second",
        ["discogs"] = "Genre and style, label, year · needs a free personal token · 60 requests a minute",
        ["acoustid"] = "Names untagged files by their audio fingerprint · needs an API key and fpcalc · 3 requests a second",
        ["spotify"] = "Artist/title, ISRC, album, cover · needs Spotify connected (previous step)",
        ["beatport"] = "Experimental: label, genre, cover from public pages · 1 request every 3 seconds",
    };

    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly MetadataEnrichmentOrchestrator _orchestrator;
    private readonly MetadataCredentialStore _keys;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<EnrichmentSetupGateway> _logger;
    private readonly List<IDisposable> _subscriptions = new();

    public EnrichmentSetupGateway(
        AppConfig config,
        ConfigManager configManager,
        MetadataEnrichmentOrchestrator orchestrator,
        MetadataCredentialStore keys,
        IDbContextFactory<AppDbContext> dbFactory,
        IEventBus eventBus,
        ILogger<EnrichmentSetupGateway> logger)
    {
        _config = config;
        _configManager = configManager;
        _orchestrator = orchestrator;
        _keys = keys;
        _dbFactory = dbFactory;
        _logger = logger;
        try
        {
            _subscriptions.Add(eventBus.GetEvent<EnrichmentProgressEvent>().Subscribe(e => ProgressChanged?.Invoke(this, e.Progress)));
            _subscriptions.Add(eventBus.GetEvent<LibraryEntriesAddedEvent>().Subscribe(_ => LibraryChanged?.Invoke(this, EventArgs.Empty)));
            _subscriptions.Add(eventBus.GetEvent<LibraryImportCompletedEvent>().Subscribe(_ => LibraryChanged?.Invoke(this, EventArgs.Empty)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: could not follow enrichment progress; the step shows it when reopened");
        }
    }

    public event EventHandler<EnrichmentProgress>? ProgressChanged;
    public event EventHandler? LibraryChanged;

    public bool AutoRun => _config.EnrichmentAutoRun;
    public bool HasDiscogsToken => _keys.Has(MetadataCredentialStore.DiscogsToken);
    public bool HasAcoustIdKey => _keys.Has(MetadataCredentialStore.AcoustIdKey);
    public bool HasFpcalc => _orchestrator.Providers.OfType<AcoustIdProvider>().FirstOrDefault()?.FpcalcPath != null;

    public IReadOnlyList<SetupMetadataSource> GetSources()
    {
        var settings = EnrichmentSettings.From(_config);
        var ordered = settings.Order
            .Select(n => _orchestrator.Providers.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
            .Where(p => p != null)
            .Concat(_orchestrator.Providers)
            .Distinct()
            .ToList();
        return ordered.Select(p =>
        {
            ProviderAvailability availability;
            try { availability = p!.GetAvailability(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Setup: availability check for {Provider} failed", p!.Name);
                availability = ProviderAvailability.Blocked;
            }
            return new SetupMetadataSource(p.Name, p.DisplayName,
                Summaries.TryGetValue(p.Name, out var line) ? line : p.Description,
                settings.IsEnabled(p.Name),
                p.Name == MetadataMerger.TagsSource,
                p.Name == BeatportProvider.ProviderName,
                availability);
        }).ToList();
    }

    public string? Save(SetupEnrichmentChoice choice)
    {
        var enabled = new HashSet<string>(choice.EnabledSources, StringComparer.OrdinalIgnoreCase) { MetadataMerger.TagsSource };
        var order = EnrichmentSettings.Split(_config.EnrichmentProviderOrder);
        foreach (var p in _orchestrator.Providers)
            if (!order.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) order.Add(p.Name);
        _config.EnrichmentProviderOrder = string.Join(",", order);
        _config.EnrichmentEnabledProviders = string.Join(",", order.Where(enabled.Contains));
        _config.EnrichmentAutoRun = choice.AutoRun;
        _configManager.Save(_config);

        string? keyError = null;
        if (!string.IsNullOrWhiteSpace(choice.DiscogsToken) && !_keys.Set(MetadataCredentialStore.DiscogsToken, choice.DiscogsToken.Trim()))
            keyError = _keys.LastError ?? "The Discogs token couldn't be stored.";
        if (!string.IsNullOrWhiteSpace(choice.AcoustIdKey) && !_keys.Set(MetadataCredentialStore.AcoustIdKey, choice.AcoustIdKey.Trim()))
            keyError = _keys.LastError ?? "The AcoustID key couldn't be stored.";
        if (keyError != null) _logger.LogError("Setup: storing a metadata key failed: {Error}", keyError);
        _logger.LogInformation("Setup: metadata sources saved (enabled={Enabled}, autoRun={AutoRun})", _config.EnrichmentEnabledProviders, choice.AutoRun);
        return keyError;
    }

    public async Task<int> CountLibraryTracksAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await db.LibraryEntries.CountAsync().ConfigureAwait(false);
    }

    public Task<int> StartAsync() => Task.Run(() => _orchestrator.EnrichLibraryAsync());
    public void Pause() => _orchestrator.Pause();
    public void Resume() => _orchestrator.Resume();
    public EnrichmentProgress CurrentProgress() => _orchestrator.CurrentProgress();

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup: could not open {Url}", url);
        }
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }
}
