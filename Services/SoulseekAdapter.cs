using System.IO;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using Soulseek;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;

namespace SLSKDONET.Services;

/// <summary>
/// Real Soulseek.NET adapter for network interactions.
/// </summary>
public class SoulseekAdapter : ISoulseekAdapter, IDisposable
{
    private static readonly HashSet<string> NonMetadataPathTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop", "downloads", "download", "temp", "incoming", "new folder", "music", "audio"
    };

    private readonly ILogger<SoulseekAdapter> _logger;
    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    public bool IsConnected => _client?.State.HasFlag(SoulseekClientStates.Connected) == true && 
                              !_client.State.HasFlag(SoulseekClientStates.Disconnecting);
    public int SharedFileCount { get; private set; }
    
    public bool IsLoggedIn => _client?.State.HasFlag(SoulseekClientStates.LoggedIn) == true;
    
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;
    public event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;

    // Rate Limiting
    private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private DateTime _lastSearchTime = DateTime.MinValue;
    private static readonly TimeSpan ShareCountCacheTtl = TimeSpan.FromSeconds(45);
    private DateTime _lastShareCountComputedAtUtc = DateTime.MinValue;
    private int _lastShareFileCount;
    private string _lastShareFolderFingerprint = string.Empty;

    private readonly Network.ProtocolHardeningService _hardeningService;
    private readonly ConcurrentDictionary<string, byte> _excludedPhrases = new();
    private readonly INetworkHealthService _healthService;

    public SoulseekAdapter(ILogger<SoulseekAdapter> logger, AppConfig config, Network.ProtocolHardeningService hardeningService, IEventBus eventBus, INetworkHealthService healthService)
    {
        _logger = logger;
        _config = config;
        _hardeningService = hardeningService;
        _eventBus = eventBus;
        _healthService = healthService;
    }

    private SoulseekClient? _client;

    private readonly ResultFingerprinter _resultFingerprinter = new();

    private void SafeDisposeClient(SoulseekClient client, string reason)
    {
        try
        {
            if (!client.State.HasFlag(SoulseekClientStates.Disconnected) &&
                !client.State.HasFlag(SoulseekClientStates.Disconnecting))
            {
                client.Disconnect();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Soulseek disconnect during {Reason} failed non-fatally", reason);
        }

        try
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Soulseek dispose during {Reason} failed non-fatally", reason);
        }
    }

    private int GetSharedFileCountWithCache(string[] shareFolders)
    {
        var folderFingerprint = string.Join("|", shareFolders.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var cacheIsValid =
            string.Equals(folderFingerprint, _lastShareFolderFingerprint, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - _lastShareCountComputedAtUtc < ShareCountCacheTtl;

        if (cacheIsValid)
        {
            return _lastShareFileCount;
        }

        var sharedFileCount = shareFolders.Sum(folder => System.IO.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count());
        _lastShareFolderFingerprint = folderFingerprint;
        _lastShareFileCount = sharedFileCount;
        _lastShareCountComputedAtUtc = DateTime.UtcNow;
        return sharedFileCount;
    }

    public async Task ConnectAsync(string? password = null, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected && IsLoggedIn) 
            {
                _logger.LogInformation("Already connected and logged in as {Username}.", _config.Username);
                return;
            }

            var existingClient = _client;
            if (existingClient != null)
            {
                var existingState = existingClient.State;
                var isActiveConnectAttempt =
                    existingState.HasFlag(SoulseekClientStates.Connecting) ||
                    (existingState.HasFlag(SoulseekClientStates.Connected) &&
                     !existingState.HasFlag(SoulseekClientStates.Disconnecting) &&
                     !existingState.HasFlag(SoulseekClientStates.Disconnected) &&
                     !existingState.HasFlag(SoulseekClientStates.LoggedIn));

                if (isActiveConnectAttempt)
                {
                    _logger.LogInformation(
                        "Connect requested while Soulseek login is already in progress (State: {State}). Waiting for existing attempt.",
                        existingState);

                    var readyClient = await WaitForReadyClientAsync(ct);
                    if (readyClient != null)
                    {
                        _logger.LogInformation("Soulseek became ready via existing login attempt; skipping client recycle.");
                        return;
                    }

                    _logger.LogWarning("Existing Soulseek login attempt did not reach ready state. Recycling client for a fresh connect attempt.");
                }
            }

            var oldClient = _client;
            if (oldClient != null)
            {
                SafeDisposeClient(oldClient, "connect swap");
            }

            var serverConnectionOptions = new ConnectionOptions(connectTimeout: _config.ConnectTimeout);
            var clientOptions = new SoulseekClientOptions(
                serverConnectionOptions: serverConnectionOptions,
                messageTimeout: Math.Max(15_000, _config.ConnectTimeout),
                maximumConcurrentSearches: Math.Clamp(_config.MaxConcurrentSearches, 1, 3),
                maximumConcurrentDownloads: Math.Clamp(_config.MaxConcurrentDownloads, 1, 10));
            var client = new SoulseekClient(minorVersion: _config.SoulseekMinorVersion, options: clientOptions);
            _client = client;

            _logger.LogInformation(
                "Soulseek client configured: minorVersion={MinorVersion}, messageTimeout={MessageTimeout}ms, maxSearches={MaxSearches}, maxDownloads={MaxDownloads}",
                _config.SoulseekMinorVersion,
                Math.Max(15_000, _config.ConnectTimeout),
                Math.Clamp(_config.MaxConcurrentSearches, 1, 3),
                Math.Clamp(_config.MaxConcurrentDownloads, 1, 10));
            
            // Subscribe to state changes BEFORE connecting to catch early login states
            client.StateChanged += (sender, args) =>
            {
                _logger.LogInformation("Soulseek state change: {State} (was {PreviousState})", 
                    args.State, args.PreviousState);
                
                _healthService.RecordConnectionStateChange(args.State.ToString());
                
                _eventBus.Publish(new SoulseekStateChangedEvent(
                    args.State.ToString(), 
                    args.State.HasFlag(SoulseekClientStates.Connected) && !args.State.HasFlag(SoulseekClientStates.Disconnecting)));
            };

            client.DiagnosticGenerated += (sender, args) =>
            {
                _logger.LogDebug("[SoulseekLib] {Level}: {Message}", args.Level, args.Message);
            };

            client.KickedFromServer += (sender, args) =>
            {
                _logger.LogWarning("Soulseek server kicked this session. Enforcing reconnect cooldown.");
                _healthService.RecordConnectionKick("KickedFromServer event");
                _eventBus.Publish(new SoulseekConnectionStatusEvent("kicked", _config.Username ?? "Unknown"));
            };

            // Phase 5/10: Adhere to new global exclusions from Soulseek Server
            client.ExcludedSearchPhrasesReceived += (sender, phrases) =>
            {
                var phraseList = phrases
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int added = 0;
                foreach (var phrase in phraseList)
                {
                    if (_excludedPhrases.TryAdd(phrase.ToLowerInvariant(), 0))
                        added++;
                }

                if (phraseList.Count > 0)
                {
                    _hardeningService.UpdateExcludedPhrases(phraseList);
                    _eventBus.Publish(new ExcludedSearchPhrasesUpdatedEvent(phraseList, added, _excludedPhrases.Count));

                    if (added > 0)
                    {
                        _logger.LogInformation("Added {Added} new excluded search phrases. Total known exclusions: {Total}", added, _excludedPhrases.Count);
                    }
                }
            };

            _logger.LogInformation("Connecting to Soulseek as {Username} on {Server}:{Port}...", 
                _config.Username, _config.SoulseekServer, _config.SoulseekPort);
            
            await client.ConnectAsync(
                _config.SoulseekServer ?? "server.slsknet.org", 
                _config.SoulseekPort == 0 ? 2242 : _config.SoulseekPort, 
                _config.Username, 
                password, 
                ct);
            
            _logger.LogInformation("Successfully connected to Soulseek as {Username}", _config.Username);
            _eventBus.Publish(new SoulseekConnectionStatusEvent("connected", _config.Username ?? "Unknown"));

            // Phase 5: Protocol Mastery - Reciprocal Sharing
            if (_config.EnableLibrarySharing)
            {
                try
                {
                    await RefreshShareStateAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set shared folders: {Message}", ex.Message);
                }
            }
            else
            {
                // Phase 6: Sharing explicitly disabled — set Bad tier so user knows
                SharedFileCount = 0;
                _eventBus.Publish(new ShareHealthUpdatedEvent(
                    SharedFolderCount: 0,
                    SharedFileCount: 0,
                    IsSharing: false,
                    Note: "Sharing is disabled. Enable in Settings to contribute to the network."));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Soulseek: {Message}", ex.Message);
            
            // Diagnose connection failure type
            var failureStatus = DiagnoseConnectionFailure(ex);
            _healthService.RecordConnectionFailure(failureStatus, ex.Message);
            
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task RefreshShareStateAsync(CancellationToken ct = default)
    {
        if (_client == null || !_config.EnableLibrarySharing)
        {
            SharedFileCount = 0;
            return;
        }

        var state = _client.State;
        var canPublishShares = state.HasFlag(SoulseekClientStates.Connected) && state.HasFlag(SoulseekClientStates.LoggedIn);
        if (!canPublishShares)
        {
            _logger.LogInformation("Skipping reciprocal share refresh because Soulseek is not fully connected/logged in (State: {State})", state);
            _eventBus.Publish(new ShareHealthUpdatedEvent(
                SharedFolderCount: 0,
                SharedFileCount: SharedFileCount,
                IsSharing: false,
                Note: $"Waiting for Soulseek login before publishing shared counts (state: {state})."));
            return;
        }

        var shareFolders = ResolveShareFolders();
        if (shareFolders.Length <= 0)
        {
            SharedFileCount = 0;
            _eventBus.Publish(new ShareHealthUpdatedEvent(
                SharedFolderCount: 0,
                SharedFileCount: 0,
                IsSharing: false,
                Note: "Sharing enabled in config but no valid folder resolved."));
            return;
        }

        _logger.LogInformation("Refreshing reciprocal sharing for {Count} folder(s): {Folders}", shareFolders.Length, string.Join(", ", shareFolders));
        var sharedFileCount = GetSharedFileCountWithCache(shareFolders);
        SharedFileCount = sharedFileCount;

        try
        {
            await _client.SetSharedCountsAsync(shareFolders.Length, sharedFileCount, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Share refresh skipped because Soulseek disconnected during publish step (State: {State})", _client.State);
            _eventBus.Publish(new ShareHealthUpdatedEvent(
                SharedFolderCount: shareFolders.Length,
                SharedFileCount: sharedFileCount,
                IsSharing: false,
                Note: "Share publish skipped because connection dropped during update."));
            return;
        }

        _eventBus.Publish(new SharedFilesStatusEvent(shareFolders.Length, string.Join(";", shareFolders)));
        _eventBus.Publish(new ShareHealthUpdatedEvent(
            SharedFolderCount: shareFolders.Length,
            SharedFileCount: sharedFileCount,
            IsSharing: true));
    }

    public async Task DisconnectAsync()
    {
        TryDisconnectClient("manual async disconnect");
        await Task.CompletedTask;
    }

    public void Disconnect()
    {
        TryDisconnectClient("manual disconnect");
    }

    private bool TryDisconnectClient(string reason)
    {
        if (_client == null)
            return false;

        var state = _client.State;
        if (state.HasFlag(SoulseekClientStates.Disconnecting) || state.HasFlag(SoulseekClientStates.Disconnected))
        {
            _logger.LogDebug("Skipped Soulseek disconnect for {Reason} because client state is already {State}", reason, state);
            return false;
        }

        try
        {
            _logger.LogInformation("[DISCONNECT] Executing Soulseek disconnect for reason '{Reason}' (State: {State})", reason, state);
            _client.Disconnect();
            _logger.LogInformation("Disconnected from Soulseek ({Reason})", reason);
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Sequence contains no elements", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Soulseek disconnect hit library race during {Reason}; treating as already disconnected.", reason);
            return false;
        }
    }

    public async Task<int> SearchAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode, // Add DownloadMode parameter
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default)
    {
        return await SearchCoreAsync(query, formatFilter, bitrateFilter, mode, onTracksFound, null, ct);
    }

    private async Task<int> SearchCoreAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        Action<IEnumerable<Track>> onTracksFound,
        SearchExecutionProfile? executionProfile,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogDebug("Search skipped because query was empty.");
            return 0;
        }

        var client = await WaitForReadyClientAsync(ct);
        if (client == null)
        {
            _logger.LogInformation("Search skipped for query {SearchQuery} because Soulseek client is not ready.", query);
            return 0;
        }

        var directories = new ConcurrentDictionary<string, List<Soulseek.File>>();
        var resultCount = 0;
        var totalFilesReceived = 0;
        var filteredByFormat = 0;
        var filteredByBitrate = 0;
        var filteredBySampleRate = 0;
        var filteredByQueue = 0;
        var filteredByDedup = 0;
        var formatSet = formatFilter?.Select(f => f.ToLowerInvariant()).ToHashSet() ?? new HashSet<string>();
        var excludedPhraseSet = new ReadOnlyCollection<string>(_excludedPhrases.Keys.ToList());
        // Beta 2026: Result fingerprinting — deduplicate by (FileName + FileSize + Duration) within one search.
        // Reduces noise by up to 70% on popular tracks shared by many peers.
        var seenThisSearch = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var minBitrateStr = bitrateFilter.Min?.ToString() ?? "0";
            var maxBitrateStr = (bitrateFilter.Max == null || bitrateFilter.Max == 0) ? "∞" : bitrateFilter.Max.ToString()!;

            // Golden Rule: Rate Limiting (configurable global delay, default 200ms)
            await _rateLimitLock.WaitAsync(ct);
            try
            {
                var extraDelay = executionProfile?.AdditionalThrottleDelayMs ?? 0;
                var throttleMs = Math.Max(50, _config.SearchThrottleDelayMs + Math.Max(0, extraDelay));
                var timeSinceLast = DateTime.UtcNow - _lastSearchTime;
                if (timeSinceLast.TotalMilliseconds < throttleMs)
                {
                    var delay = throttleMs - (int)timeSinceLast.TotalMilliseconds;
                    _logger.LogDebug("Rate Limiting: Delaying search by {Ms}ms", delay);
                    await Task.Delay(delay, ct);
                }
                _lastSearchTime = DateTime.UtcNow;
            }
            finally
            {
                _rateLimitLock.Release();
            }

            _logger.LogInformation("Search started for query {SearchQuery} with mode {SearchMode}, format filter {FormatFilter}, bitrate range {MinBitrate}-{MaxBitrate}",
                query, mode, formatFilter == null ? "NONE" : string.Join(", ", formatFilter), minBitrateStr, maxBitrateStr);

            // NEW Phase 12.2: Proactive Network Safety - Prevent sending banned phrases
            var lowerQuery = query.ToLowerInvariant();
            if (_excludedPhrases.Count > 0)
            {
                 foreach (var phrase in _excludedPhrases.Keys)
                 {
                     if (lowerQuery.Contains(phrase))
                     {
                         _logger.LogWarning("🚨 [NETWORK SAFETY] Aborting search to prevent soft-ban: Query '{Query}' contains banned phrase '{Phrase}'", query, phrase);
                         _healthService.RecordExcludedPhraseQueryBlock();
                         return 0;
                     }
                 }
            }
            
            var searchQuery = Soulseek.SearchQuery.FromText(query);
            var responseLimit = executionProfile?.EffectiveResponseLimit ?? Math.Max(20, _config.SearchResponseLimit);
            var fileLimit = executionProfile?.EffectiveFileLimit ?? Math.Max(20, _config.SearchFileLimit);
            var options = new SearchOptions(
                searchTimeout: Math.Max(5000, _config.SearchTimeout),
                responseLimit: Math.Max(20, responseLimit),
                filterResponses: true,
                minimumResponseFileCount: 1,
                maximumPeerQueueLength: Math.Max(0, _config.MaxPeerQueueLength),
                fileLimit: Math.Max(20, fileLimit),
                removeSingleCharacterSearchTerms: true,
                fileFilter: file =>
                {
                    var decision = SearchFilterPolicy.EvaluateFile(
                        file,
                        formatSet,
                        bitrateFilter,
                        _config.PreferredMaxSampleRate,
                        excludedPhraseSet);
                    return decision.IsAccepted;
                }
            );

            // The SearchAsync method in the library (or wrapper) seems to handle the waiting internally 
            // based on the stack trace showing SearchToCallbackAsync waiting.
            // So we just await the search initialization/execution.
            await client.SearchAsync(
                searchQuery,
                (response) =>
                {
                    _logger.LogDebug("Received response from {User} with {Count} files", response.Username, response.Files.Count());

                    var foundTracksInResponse = new List<Track>();

                    // Process each search response
                    foreach (var file in response.Files)
                    {
                        if (mode == DownloadMode.Album)
                        {
                            var directoryName = Path.GetDirectoryName(file.Filename);
                            if (!string.IsNullOrEmpty(directoryName))
                            {
                                var key = $"{response.Username}@{directoryName}";
                                directories.AddOrUpdate(key, 
                                    _ => new List<Soulseek.File> { file }, 
                                    (_, list) => { list.Add(file); return list; });
                            }
                        }
                        else // Normal mode
                        {
                            totalFilesReceived++;
                            var extension = Path.GetExtension(file.Filename)?.TrimStart('.').ToLowerInvariant();
                            var fileDecision = SearchFilterPolicy.EvaluateFile(
                                file,
                                formatSet,
                                bitrateFilter,
                                _config.PreferredMaxSampleRate,
                                excludedPhraseSet,
                                Math.Max(0, _config.MaxPeerQueueLength),
                                response.QueueLength);

                            if (!fileDecision.IsAccepted)
                            {
                                switch (fileDecision.Reason)
                                {
                                    case SearchRejectionReason.Format:
                                        filteredByFormat++;
                                        if (filteredByFormat <= 3)
                                        {
                                            _logger.LogInformation("[FILTER] Rejected by format: {File} (extension: {Ext}, allowed: {Formats})", file.Filename, extension, string.Join(", ", formatSet));
                                        }
                                        break;
                                    case SearchRejectionReason.Bitrate:
                                        filteredByBitrate++;
                                        break;
                                    case SearchRejectionReason.SampleRate:
                                        filteredBySampleRate++;
                                        break;
                                    case SearchRejectionReason.Queue:
                                        filteredByQueue++;
                                        break;
                                }
                                continue;
                            }

                            var lengthAttr = file.Attributes?.FirstOrDefault(a => a.Type == Soulseek.FileAttributeType.Length);
                            var rawDurationSeconds = lengthAttr?.Value ?? 0;

                            // Beta 2026: Fingerprint dedup with peer-awareness.
                            // Keep duplicates only when they come from a better queue peer.
                            var fpKey = _resultFingerprinter.Create(file.Filename, file.Size, rawDurationSeconds);
                            var isDedupReplacement = false;
                            if (seenThisSearch.TryGetValue(fpKey, out var existingQueue))
                            {
                                if (response.QueueLength < existingQueue)
                                {
                                    seenThisSearch[fpKey] = response.QueueLength;
                                    isDedupReplacement = true;
                                }
                                else
                                {
                                    filteredByDedup++;
                                    continue;
                                }
                            }
                            else
                            {
                                seenThisSearch.TryAdd(fpKey, response.QueueLength);
                            }

                            // Memory Optimization: Only allocate Track object for files that survive the filters
                            // Use the helper method to parse metadata correctly
                            var track = ParseTrackFromFile(file, response);
                            track.Metadata ??= new Dictionary<string, object>();
                            track.Metadata["IsDedup"] = isDedupReplacement;

                            if (resultCount <= 3) // Log first 3 matches
                            {
                                _logger.LogInformation("[ACCEPT] Track passed filters: {Artist} - {Title} ({Bitrate} kbps, {Ext})", track.Artist, track.Title, track.Bitrate, extension);
                            }
                            foundTracksInResponse.Add(track);
                            resultCount++;
                        }
                    }

                    if (foundTracksInResponse.Any())
                    {
                        onTracksFound(foundTracksInResponse);
                    }
                },
                options: options,
                cancellationToken: ct
            );

            if (mode == DownloadMode.Album)
            {
                _logger.LogInformation("Found {Count} potential album directories.", directories.Count);
                // TODO: In a future step, we would rank these directories and create album download jobs.
                // For now, we will just log them.
                resultCount = directories.Count;
            }

            _logger.LogInformation(
                "Search completed: {ResultCount} results from {TotalFiles} files " +
                "(filtered: {FormatFiltered} format, {BitrateFiltered} bitrate, {SampleRateFiltered} sample-rate, " +
                "{QueueFiltered} queue, {DedupFiltered} dedup)",
                resultCount, totalFilesReceived, filteredByFormat, filteredByBitrate,
                filteredBySampleRate, filteredByQueue, filteredByDedup);

            _healthService.RecordSearchFiltering(
                filteredByFormat,
                filteredByBitrate,
                filteredBySampleRate,
                filteredByQueue,
                filteredByDedup,
                0);

            // Record search results for health diagnostics
            _healthService.RecordSearch(query, totalFilesReceived, resultCount, true);
            
            return resultCount;
        }
        catch (OperationCanceledException)
        {
             _logger.LogInformation("Search cancelled for query {SearchQuery}", query);
             return resultCount; // Return whatever we found before cancellation
        }
        catch (Exception ex)
        {
             // Check if we are shutting down or disconnected
             var state = _client?.State;
             if (ct.IsCancellationRequested ||
                 state.HasValue &&
                 (state.Value.HasFlag(SoulseekClientStates.Disconnected) || state.Value.HasFlag(SoulseekClientStates.Disconnecting)))
             {
                 _logger.LogWarning("Search aborted for query {SearchQuery} due to connection shutdown: {Message}", query, ex.Message);
                 _healthService.RecordSearchFiltering(
                     filteredByFormat,
                     filteredByBitrate,
                     filteredBySampleRate,
                     filteredByQueue,
                     filteredByDedup,
                     0);
                 _healthService.RecordSearch(query, totalFilesReceived, resultCount, false, "Connection shutdown");
                 return resultCount; 
             }
             
             _logger.LogError(ex, "Search failed for query {SearchQuery} with mode {SearchMode}", query, mode);
             _healthService.RecordSearchFiltering(
                 filteredByFormat,
                 filteredByBitrate,
                 filteredBySampleRate,
                 filteredByQueue,
                 filteredByDedup,
                 0);
             _healthService.RecordSearch(query, totalFilesReceived, resultCount, false, ex.Message);
             // Re-throw if it's not a shutdown scenario? 
             // Actually, returning 0 or partial results is safer than crashing the flow if the search fails.
             // But let's stick to previous logic: throw if it's a real error.
             throw; 
        }
    }

    private async Task<SoulseekClient?> WaitForReadyClientAsync(CancellationToken ct)
    {
        int initWait = 0;
        const int maxInitWait = 10;
        while (_client == null && initWait < maxInitWait)
        {
            _logger.LogDebug("Waiting for Soulseek client initialization (attempt {Attempt}/{Max})", initWait + 1, maxInitWait);
            await Task.Delay(200, ct);
            initWait++;
        }

        var client = _client;
        if (client == null)
            return null;

        if (client.State.HasFlag(SoulseekClientStates.Disconnecting) || client.State.HasFlag(SoulseekClientStates.Disconnected))
            return null;

        int waitRetries = 0;
        const int maxWaitRetries = 20;
        const int retryDelayMs = 500;
        var waitStartUtc = DateTime.UtcNow;
        var nextProgressLogAtSeconds = 2 + Random.Shared.Next(0, 2);

        while (!client.State.HasFlag(SoulseekClientStates.LoggedIn) && waitRetries < maxWaitRetries)
        {
            await Task.Delay(retryDelayMs, ct);
            waitRetries++;

            var elapsedSeconds = (int)(DateTime.UtcNow - waitStartUtc).TotalSeconds;
            if (elapsedSeconds >= nextProgressLogAtSeconds)
            {
                _logger.LogDebug(
                    "Waiting for Soulseek login... (State: {State}, Elapsed: {Elapsed}s, Attempt {Attempt}/{Max})",
                    client.State,
                    elapsedSeconds,
                    waitRetries,
                    maxWaitRetries);
                nextProgressLogAtSeconds += 2 + Random.Shared.Next(0, 2);
            }

            client = _client;
            if (client == null)
                return null;
            if (client.State.HasFlag(SoulseekClientStates.Disconnecting) || client.State.HasFlag(SoulseekClientStates.Disconnected))
                return null;
        }

        if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
        {
            _logger.LogInformation("Soulseek not logged in yet after readiness wait (State: {State})", client.State);
            return null;
        }

        return client;
    }

    public async IAsyncEnumerable<Track> StreamResultsAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        SearchExecutionProfile? executionProfile = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<Track>();
        var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Run the existing search logic in a background task
        // We use the existing SearchAsync but redirect its "onTracksFound" callback to write to the channel
        var searchTask = Task.Run(async () =>
        {
            try
            {
                await SearchCoreAsync(query, formatFilter, bitrateFilter, mode, (tracks) =>
                {
                    foreach (var track in tracks)
                    {
                        channel.Writer.TryWrite(track);
                    }
                }, executionProfile, searchCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation, ignore
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || !(_client?.State.HasFlag(SoulseekClientStates.LoggedIn) ?? false))
                {
                    _logger.LogWarning("Background stream search stopped: {Message}", ex.Message);
                }
                else
                {
                    _logger.LogWarning(ex, "Error in background streaming search for {Query}", query);
                }
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct); // Use outer CT for Task scheduling.

        // Yield results from the channel
        while (await channel.Reader.WaitToReadAsync(ct))
        {
            while (channel.Reader.TryRead(out var track))
            {
                yield return track;
            }
        }

        // Await the task to ensure we catch any exceptions or ensure clean exit
        // (Though we swallowed exceptions above to ensure channel closes, checking here is good hygiene)
        // await searchTask; 
    }

    /// <summary>
    /// Progressive search strategy: Tries multiple search queries with increasing leniency.
    /// 1. Strict: "Artist - Title" (exact match expected)
    /// 2. Relaxed: "Artist Title" (keyword-based)
    /// 3. Album: Album-based search (fallback)
    /// Returns results from the first successful strategy.
    /// </summary>
    public async Task<int> ProgressiveSearchAsync(
        string artist,
        string title,
        string? album,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default)
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Not connected to Soulseek");
        }

        var maxAttempts = _config.MaxSearchAttempts;
        _logger.LogInformation("Starting progressive search: {Artist} - {Title} (album: {Album})", artist, title, album ?? "unknown");

        // Strategy 1: Strict search "Artist - Title"
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return 0;

            try
            {
                var strictQuery = $"{artist} - {title}";
                _logger.LogInformation("Attempt {Attempt}/{Max}: Strict search: {Query}", attempt, maxAttempts, strictQuery);
                
                var resultCount = await SearchAsync(
                    strictQuery,
                    formatFilter,
                    bitrateFilter,
                    DownloadMode.Normal,
                    onTracksFound,
                    ct);
                
                if (resultCount > 0)
                {
                    _logger.LogInformation("Progressive search succeeded with strict query after {Attempt} attempt(s)", attempt);
                    return resultCount;
                }

                if (attempt < maxAttempts)
                    await Task.Delay(Math.Max(50, _config.SearchThrottleDelayMs), ct); // Brief delay before retry
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Strict search attempt {Attempt} failed", attempt);
            }
        }

        // Strategy 2: Relaxed search "Artist Title"
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return 0;

            try
            {
                var relaxedQuery = $"{artist} {title}";
                _logger.LogInformation("Attempt {Attempt}/{Max}: Relaxed search: {Query}", attempt, maxAttempts, relaxedQuery);
                
                var resultCount = await SearchAsync(
                    relaxedQuery,
                    formatFilter,
                    bitrateFilter,
                    DownloadMode.Normal,
                    onTracksFound,
                    ct);
                
                if (resultCount > 0)
                {
                    _logger.LogInformation("Progressive search succeeded with relaxed query after {Attempt} attempt(s)", attempt);
                    return resultCount;
                }

                if (attempt < maxAttempts)
                    await Task.Delay(Math.Max(50, _config.SearchThrottleDelayMs), ct); // Brief delay before retry
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Relaxed search attempt {Attempt} failed", attempt);
            }
        }

        // Strategy 3: Album search (fallback)
        if (!string.IsNullOrEmpty(album))
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested)
                    return 0;

                try
                {
                    _logger.LogInformation("Attempt {Attempt}/{Max}: Album search: {Query}", attempt, maxAttempts, album);
                    
                    var resultCount = await SearchAsync(
                        album,
                        formatFilter,
                        bitrateFilter,
                        DownloadMode.Album,
                        onTracksFound,
                        ct);
                    
                    if (resultCount > 0)
                    {
                        _logger.LogInformation("Progressive search succeeded with album search after {Attempt} attempt(s)", attempt);
                        return resultCount;
                    }

                    if (attempt < maxAttempts)
                        await Task.Delay(Math.Max(50, _config.SearchThrottleDelayMs), ct); // Brief delay before retry
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Album search attempt {Attempt} failed", attempt);
                }
            }
        }

        _logger.LogWarning("Progressive search exhausted all strategies for {Artist} - {Title}", artist, title);
        return 0;
    }

    private Track ParseTrackFromFile(Soulseek.File file, Soulseek.SearchResponse response)
    {
        // Extract bitrate and length from file attributes
        var bitrateAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.BitRate);
        var bitrate = bitrateAttr?.Value ?? 0;
        var lengthAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.Length);
        var length = lengthAttr?.Value ?? 0;
        
        var sampleRateAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.SampleRate);
        var sampleRate = sampleRateAttr?.Value;
        
        var bitDepthAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.BitDepth);
        var bitDepth = bitDepthAttr?.Value;

        var pathSegments = file.Filename
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();

        var rawFilename = Path.GetFileNameWithoutExtension(file.Filename);
        var cleanFilename = CleanTrackToken(rawFilename);

        string artist = "Unknown Artist";
        string title = cleanFilename;
        string album = string.Empty;

        // Path-first intelligence: treat directory chain as primary metadata source.
        if (pathSegments.Count >= 2)
        {
            var parentAlbum = CleanTrackToken(pathSegments[^2]);
            if (IsLikelyMetadataSegment(parentAlbum))
            {
                album = parentAlbum;
            }
        }

        if (pathSegments.Count >= 3)
        {
            var parentArtist = CleanTrackToken(pathSegments[^3]);
            if (IsLikelyMetadataSegment(parentArtist))
            {
                artist = parentArtist;
            }
        }

        // Safe filename fallback: only split when explicit artist-title delimiter exists.
        var filenameParts = Regex.Split(cleanFilename, @"\s[-–—]\s", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (filenameParts.Length >= 2)
        {
            var filenameArtist = CleanTrackToken(filenameParts[0]);
            var filenameTitle = CleanTrackToken(string.Join(" - ", filenameParts.Skip(1)));

            if (!string.IsNullOrWhiteSpace(filenameTitle))
            {
                // If path artist is unavailable or generic, trust filename artist.
                if (artist == "Unknown Artist" || !IsLikelyMetadataSegment(artist))
                {
                    artist = string.IsNullOrWhiteSpace(filenameArtist) ? artist : filenameArtist;
                }

                title = filenameTitle;
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = rawFilename;
        }

        if (string.IsNullOrWhiteSpace(album) && pathSegments.Count >= 2)
        {
            album = CleanTrackToken(pathSegments[^2]);
        }

        var track = new Track
        {
            Artist = artist,
            Title = title,
            Album = album,
            PathSegments = pathSegments, // Phase 1.1: Context for the Brain
            Filename = file.Filename,
            Directory = Path.GetDirectoryName(file.Filename),
            Username = response.Username,
            Format = Path.GetExtension(file.Filename)?.TrimStart('.').ToLowerInvariant(),
            Bitrate = bitrate,
            SampleRate = sampleRate,
            BitDepth = bitDepth,
            Size = file.Size,
            Length = length,
            SoulseekFile = file,
            
            HasFreeUploadSlot = response.HasFreeUploadSlot,
            QueueLength = response.QueueLength,
            UploadSpeed = response.UploadSpeed
        };

        return track;
    }

    private static string CleanTrackToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var cleaned = Regex.Replace(value, @"^\d{1,3}[\s\-_.]+", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        cleaned = Regex.Replace(cleaned, @"\[[^\]]*\]|\([^\)]*\)", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return cleaned.Trim();
    }

    private static bool IsLikelyMetadataSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return false;

        var normalized = segment.Trim();
        if (NonMetadataPathTokens.Contains(normalized))
            return false;

        return normalized.Length >= 2;
    }

    private string[] ResolveShareFolders()
    {
        var folders = new List<string>();

        if (!string.IsNullOrWhiteSpace(_config.SharedFolderPath) && System.IO.Directory.Exists(_config.SharedFolderPath))
        {
            folders.Add(_config.SharedFolderPath);
        }

        if (!string.IsNullOrWhiteSpace(_config.DownloadDirectory) && System.IO.Directory.Exists(_config.DownloadDirectory))
        {
            folders.Add(_config.DownloadDirectory);
        }

        return folders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<bool> DownloadAsync(
        string username,
        string filename,
        string outputPath,
        long? size = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        long startOffset = 0)  // Phase 2.5: Add resume support
    {
        if (this._client == null)
        {
            throw new InvalidOperationException("Not connected to Soulseek");
        }

        try
        {
            this._logger.LogInformation("Downloading {Filename} from {Username} to {OutputPath} (offset: {Offset})", 
                filename, username, outputPath, startOffset);
            
            // Check if already cancelled
            ct.ThrowIfCancellationRequested();

            var directory = Path.GetDirectoryName(outputPath);
            if (directory != null)
                System.IO.Directory.CreateDirectory(directory);

            // Track state for timeout logic
            DateTime lastActivity = DateTime.UtcNow;
            long lastBytes = startOffset;  // Start from existing bytes
            bool isQueued = false;

            var downloadOptions = new TransferOptions(
                stateChanged: (args) =>
                {
                    // Update queued status
                    if (args.Transfer.State.HasFlag(TransferStates.Queued))
                    {
                        isQueued = true;
                    }
                    else if (args.Transfer.State.HasFlag(TransferStates.InProgress))
                    {
                        isQueued = false;
                        
                        // Check for progress activity
                        if (args.Transfer.BytesTransferred > lastBytes)
                        {
                            lastBytes = args.Transfer.BytesTransferred;
                            lastActivity = DateTime.UtcNow;
                        }

                        if (size.HasValue && size.Value > 0)
                        {
                            double percentage = (double)args.Transfer.BytesTransferred / size.Value;
                            progress?.Report(percentage);
                            
                            DownloadProgressChanged?.Invoke(this, new DownloadProgressEventArgs(
                                filename, username, percentage, args.Transfer.BytesTransferred, size.Value));
                        }
                    }
                });

            // Phase 2.5: Use Append mode if resuming, Create if starting fresh
            var fileMode = startOffset > 0 ? FileMode.Append : FileMode.Create;
            using var fileStream = new FileStream(outputPath, fileMode, FileAccess.Write, FileShare.None, 8192, useAsync: true);
            
            // We wrap the Soulseek DownloadAsync in our own task to enforce our custom timeout logic
            // The underlying client has some timeout logic, but we want granular control over "Stalled vs Queued"
            var downloadTask = this._client.DownloadAsync(
                username,
                filename,
                () => Task.FromResult((Stream)fileStream),
                size,
                startOffset: startOffset,  // Pass the offset to Soulseek client
                options: downloadOptions,
                cancellationToken: ct);

            // Monitoring Loop
            while (!downloadTask.IsCompleted)
            {
                // Check if we should time out
                // Modified: Only timeout if NOT queued and no activity for 60s
                if (!isQueued && (DateTime.UtcNow - lastActivity).TotalSeconds > 60)
                {
                    // STALLED: Not queued, but no bytes moved for 60s
                    throw new TimeoutException("Transfer stalled for 60 seconds (0 bytes received).");
                }
                
                // If we are queued, we WAIT INDEFINITELY (or until user cancels)
                // This is the key fix: Don't timeout if we are just waiting in line.

                await Task.WhenAny(downloadTask, Task.Delay(1000, ct));
            }

            await downloadTask; // Propagate exceptions/completion

            this._logger.LogInformation("Download completed: {Filename}", filename);
            progress?.Report(1.0);
            _eventBus.Publish(new TransferFinishedEvent(filename, username));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, true));
            
            return true;
        }
        catch (OperationCanceledException)
        {
            this._logger.LogWarning("Download cancelled: {Filename}", filename);
            _eventBus.Publish(new TransferCancelledEvent(filename, username));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Cancelled"));
            
            throw; 
        }
        catch (TimeoutException ex)
        {
            this._logger.LogWarning("Download timeout: {Filename} from {Username} - {Message}", filename, username, ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "Connection timeout"));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Timeout"));
            
            return false;
        }
        catch (IOException ex)
        {
            this._logger.LogError(ex, "I/O error during download: {Filename} from {Username}", filename, username);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "I/O error: " + ex.Message));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "I/O Error: " + ex.Message));
            
            return false;
        }
        catch (Exception ex) when (ex.Message.Contains("refused") || ex.Message.Contains("aborted") || ex.Message.Contains("Unable to read"))
        {
            this._logger.LogWarning("Network error during download: {Filename} from {Username} - {Message}", filename, username, ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "Connection failed"));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Connection Failed"));
            
            return false;
        }
        catch (Soulseek.TransferRejectedException ex)
        {
             // RETHROW: "Too many files" or "Banned" 
             // This allows DownloadManager to catch it and trigger Exponential Backoff / Retry
             DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Rejected: " + ex.Message));
             throw; 
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Download failed: {Message}", ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, ex.Message));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, ex.Message));
            
            return false;
        }
    }

    public async Task<IEnumerable<Track>> GetUserSharesAsync(string username, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        try
        {
            _logger.LogInformation("Browsing shares for user: {Username}", username);
            
            var response = await _client.BrowseAsync(username, cancellationToken: ct);
            
            var tracks = new List<Track>();
            var allFiles = response.Directories
                .Concat(response.LockedDirectories)
                .SelectMany(directory => directory.Files.Select(file => new SearchResponse(
                    username,
                    0,
                    false,
                    0,
                    0,
                    new[]
                    {
                        new Soulseek.File(
                            file.Code,
                            $"{directory.Name.TrimEnd('\\')}\\{file.Filename}",
                            file.Size,
                            file.Extension,
                            file.Attributes)
                    })));

            foreach (var responseItem in allFiles)
            {
                var file = responseItem.Files.First();
                var track = ParseTrackFromFile(file, responseItem);
                if (track != null) tracks.Add(track);
            }
            
            _logger.LogInformation("Found {Count} files in {Username}'s shares", tracks.Count, username);
            return tracks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse user shares for {Username}: {Message}", username, ex.Message);
            return Enumerable.Empty<Track>();
        }
    }

    /// <summary>
    /// Diagnose the type of connection failure from an exception
    /// </summary>
    private ConnectionFailureStatus DiagnoseConnectionFailure(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        
        if (ex.InnerException != null)
            message += " " + ex.InnerException.Message.ToLowerInvariant();
        
        // Timeout patterns
        if (message.Contains("timeout") || message.Contains("timed out"))
            return ConnectionFailureStatus.AuthenticationTimeout;
        
        // Connection refused patterns
        if (message.Contains("refused") || message.Contains("no connection could be made") || 
            message.Contains("econnrefused"))
            return ConnectionFailureStatus.ConnectionRefused;
        
        // Network timeout patterns
        if (message.Contains("network unreachable") || message.Contains("no route to host") ||
            message.Contains("ehostunreach"))
            return ConnectionFailureStatus.NetworkTimeout;
        
        // Unexpected disconnection
        if (message.Contains("disconnected") || message.Contains("connection closed"))
            return ConnectionFailureStatus.UnexpectedDisconnection;
        
        // Default to other
        return ConnectionFailureStatus.Other;
    }

    public void Dispose()
    {
        var client = _client;
        _client = null;

        if (client != null)
        {
            SafeDisposeClient(client, "adapter dispose");
        }

        _connectLock.Dispose();
        _rateLimitLock.Dispose();
    }

}
