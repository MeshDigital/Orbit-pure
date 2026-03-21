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
using System.Net;
using System.Reflection;
using Open.Nat;

namespace SLSKDONET.Services;

/// <summary>
/// Real Soulseek.NET adapter for network interactions.
/// </summary>
public class SoulseekAdapter : ISoulseekAdapter, IDisposable
{
    private sealed record RuntimeNetworkConfigSnapshot(int ConnectTimeout, int ListenPort);

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
    private DateTime _searchBucketLastRefillUtc = DateTime.UtcNow;
    private double _searchBucketTokens = 1d;
    private readonly SemaphoreSlim _upnpLock = new(1, 1);
    private bool _upnpPortMapped;
    private DateTime _lastUpnpAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan UpnpAttemptCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShareCountCacheTtl = TimeSpan.FromSeconds(45);
    private DateTime _lastShareCountComputedAtUtc = DateTime.MinValue;
    private int _lastShareFileCount;
    private string _lastShareFolderFingerprint = string.Empty;

    private readonly Network.ProtocolHardeningService _hardeningService;
    private readonly ConcurrentDictionary<string, byte> _excludedPhrases = new();
    private readonly INetworkHealthService _healthService;
    private RuntimeNetworkConfigSnapshot? _lastAppliedRuntimeNetworkConfig;
    private string? _pendingDisconnectReason;
    private string? _lastDiagnosticMessage;
    private int _outboundSearchInFlight;
    private static readonly string[] ClientEventNamesToClear =
    {
        "StateChanged",
        "DiagnosticGenerated",
        "KickedFromServer",
        "ExcludedSearchPhrasesReceived"
    };

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

    private static int GetEffectiveConnectTimeout(int configuredTimeout)
        => Math.Max(60_000, configuredTimeout);

    private static int GetEffectiveMessageTimeout(int effectiveConnectTimeout)
        => Math.Max(120_000, effectiveConnectTimeout);

    private static int GetEffectiveListenPort(int configuredListenPort)
        => Math.Clamp(configuredListenPort, 1_024, 65_535);

    private void RefillSearchTokens(int capacity, int refillIntervalMs)
    {
        var now = DateTime.UtcNow;
        var elapsedMs = (now - _searchBucketLastRefillUtc).TotalMilliseconds;
        if (elapsedMs <= 0)
            return;

        var refillTokens = elapsedMs / Math.Max(1, refillIntervalMs);
        if (refillTokens <= 0)
            return;

        _searchBucketTokens = Math.Min(capacity, _searchBucketTokens + refillTokens);
        _searchBucketLastRefillUtc = now;
    }

    private int MillisecondsUntilNextToken(int refillIntervalMs)
    {
        if (_searchBucketTokens >= 1d)
            return 0;

        var missing = 1d - _searchBucketTokens;
        return Math.Max(25, (int)Math.Ceiling(missing * Math.Max(1, refillIntervalMs)));
    }

    private async Task EnsureUpnpPortMappingAsync(CancellationToken ct)
    {
        if (!_config.UseUPnP || _upnpPortMapped)
            return;

        if (DateTime.UtcNow - _lastUpnpAttemptUtc < UpnpAttemptCooldown)
            return;

        await _upnpLock.WaitAsync(ct);
        try
        {
            if (!_config.UseUPnP || _upnpPortMapped)
                return;

            if (DateTime.UtcNow - _lastUpnpAttemptUtc < UpnpAttemptCooldown)
                return;

            _lastUpnpAttemptUtc = DateTime.UtcNow;
            var listenPort = GetEffectiveListenPort(_config.ListenPort);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            try
            {
                var discoverer = new NatDiscoverer();
                var natDevice = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, timeoutCts);
                var mapping = new Mapping(Open.Nat.Protocol.Tcp, listenPort, listenPort, 3600, "ORBIT Soulseek listener");
                await natDevice.CreatePortMapAsync(mapping);

                _upnpPortMapped = true;
                _logger.LogInformation("UPnP port mapping established for Soulseek listener on TCP {Port}", listenPort);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("UPnP discovery timed out for Soulseek listener mapping (TCP {Port})", listenPort);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "UPnP mapping skipped or failed non-fatally for TCP {Port}", listenPort);
            }
        }
        finally
        {
            _upnpLock.Release();
        }
    }

    private static int[] BuildStagedSharePublishPlan(int totalFileCount)
    {
        if (totalFileCount <= 0)
            return new[] { 0 };

        if (totalFileCount >= 200)
        {
            return new[]
            {
                Math.Min(50, totalFileCount),
                Math.Min((int)Math.Ceiling(totalFileCount * 0.35), totalFileCount),
                totalFileCount
            };
        }

        if (totalFileCount >= 50)
        {
            return new[]
            {
                Math.Min(25, totalFileCount),
                totalFileCount
            };
        }

        return new[] { totalFileCount };
    }

    private async Task PublishSharedCountsStagedAsync(int sharedFolderCount, int sharedFileCount, CancellationToken ct)
    {
        if (_client == null)
            return;

        var plan = BuildStagedSharePublishPlan(sharedFileCount);
        var lastPublishedCount = -1;

        foreach (var count in plan)
        {
            if (count <= lastPublishedCount)
                continue;

            await _client.SetSharedCountsAsync(sharedFolderCount, count, ct);
            lastPublishedCount = count;

            if (count < sharedFileCount)
            {
                await Task.Delay(300, ct);
            }
        }
    }

    private RuntimeNetworkConfigSnapshot CreateRuntimeNetworkConfigSnapshot()
        => new(
            ConnectTimeout: GetEffectiveConnectTimeout(_config.ConnectTimeout),
            ListenPort: GetEffectiveListenPort(_config.ListenPort));

    private void MarkPendingDisconnectReason(string reason)
    {
        Interlocked.Exchange(ref _pendingDisconnectReason, reason);
    }

    private string PeekPendingDisconnectReasonOrDefault(string fallback)
    {
        var pendingReason = Interlocked.CompareExchange(ref _pendingDisconnectReason, null, null);
        return string.IsNullOrWhiteSpace(pendingReason) ? fallback : pendingReason;
    }

    private string ConsumePendingDisconnectReasonOrDefault(string fallback)
    {
        var pendingReason = Interlocked.Exchange(ref _pendingDisconnectReason, null);
        return string.IsNullOrWhiteSpace(pendingReason) ? fallback : pendingReason;
    }

    private string ClassifyDisconnectBucket(string? contextMessage, SoulseekClientStates previousState)
    {
        var message = contextMessage?.ToLowerInvariant() ?? string.Empty;

        if (message.Contains("connection reset"))
            return "TRANSPORT_FAULT_CONNECTION_RESET";
        if (message.Contains("timed out") || message.Contains("timeout"))
            return "KEEP_ALIVE_TIMEOUT";
        if (message.Contains("unable to read") || message.Contains("ioexception") || message.Contains("stream"))
            return "STREAM_IO_FAULT";
        if (message.Contains("end of stream") || message.Contains("argumentoutofrange") || message.Contains("outofmemory") || message.Contains("message length") || message.Contains("buffer"))
            return "PROTOCOL_VIOLATION";
        if (message.Contains("disposed"))
            return "LOCAL_CLIENT_DISPOSED";
        if (message.Contains("login rejected") || message.Contains("invalid password") || message.Contains("invalid credentials"))
            return "SERVER_AUTH_REJECTED";
        if (message.Contains("refused") || message.Contains("econnrefused"))
            return "SERVER_REFUSED_TCP";

        if (previousState.HasFlag(SoulseekClientStates.LoggedIn))
            return "UNKNOWN_UNPLANNED_DROP_LOGGED_IN";

        return "UNKNOWN_UNPLANNED_DROP";
    }

    private string ClassifyConnectFailureBucket(Exception ex)
    {
        var message = ex.ToString().ToLowerInvariant();
        if (message.Contains("address already in use") || message.Contains("only one usage of each socket address"))
            return "LISTEN_PORT_BIND_IN_USE";
        if (message.Contains("end of stream") || message.Contains("argumentoutofrange") || message.Contains("outofmemory") || message.Contains("message length") || message.Contains("buffer"))
            return "PROTOCOL_VIOLATION";

        var failure = DiagnoseConnectionFailure(ex);
        return failure switch
        {
            ConnectionFailureStatus.LoginRejected => "SERVER_AUTH_REJECTED",
            ConnectionFailureStatus.ConnectionRefused => "SERVER_REFUSED_TCP",
            ConnectionFailureStatus.NetworkTimeout => "TRANSPORT_FAULT_NETWORK_TIMEOUT",
            ConnectionFailureStatus.AuthenticationTimeout => "KEEP_ALIVE_TIMEOUT",
            ConnectionFailureStatus.UnexpectedDisconnection => "TRANSPORT_FAULT_UNEXPECTED_DISCONNECT",
            _ => "UNKNOWN_CONNECT_FAILURE"
        };
    }

    private void QueueLibraryCallback(string callbackName, Action work)
    {
        _ = Task.Run(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unhandled exception in Soulseek callback {CallbackName}", callbackName);
            }
        });
    }

    private SoulseekClientOptions CreateClientOptions()
    {
        var runtime = CreateRuntimeNetworkConfigSnapshot();
        var serverConnectionOptions = new ConnectionOptions(connectTimeout: runtime.ConnectTimeout);
        var messageTimeout = GetEffectiveMessageTimeout(runtime.ConnectTimeout);

        return new SoulseekClientOptions(
            enableListener: true,
            listenIPAddress: IPAddress.Any,
            listenPort: runtime.ListenPort,
            serverConnectionOptions: serverConnectionOptions,
            messageTimeout: messageTimeout,
            maximumConcurrentSearches: Math.Clamp(_config.MaxConcurrentSearches, 1, 3),
            maximumConcurrentDownloads: Math.Clamp(_config.MaxConcurrentDownloads, 1, 10));
    }

    private SoulseekClientOptionsPatch CreateRuntimeNetworkOptionsPatch(RuntimeNetworkConfigSnapshot runtime)
    {
        var serverConnectionOptions = new ConnectionOptions(connectTimeout: runtime.ConnectTimeout);

        return new SoulseekClientOptionsPatch(
            enableListener: true,
            listenIPAddress: IPAddress.Any,
            listenPort: runtime.ListenPort,
            serverConnectionOptions: serverConnectionOptions);
    }

    private void SafeDisposeClient(SoulseekClient client, string reason)
    {
        ClearClientEventHandlers(client, reason);

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

    private void ClearClientEventHandlers(SoulseekClient client, string reason)
    {
        foreach (var eventName in ClientEventNamesToClear)
        {
            try
            {
                var field = typeof(SoulseekClient).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.FieldType != null && typeof(MulticastDelegate).IsAssignableFrom(field.FieldType))
                {
                    field.SetValue(client, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to clear Soulseek client event handler '{EventName}' during {Reason}", eventName, reason);
            }
        }
    }

    private static void PublishTracksInBatches(Action<IEnumerable<Track>> onTracksFound, List<Track> tracks, int batchSize)
    {
        if (tracks.Count <= 0)
            return;

        if (tracks.Count <= batchSize)
        {
            onTracksFound(tracks);
            return;
        }

        for (var offset = 0; offset < tracks.Count; offset += batchSize)
        {
            var take = Math.Min(batchSize, tracks.Count - offset);
            onTracksFound(tracks.GetRange(offset, take));
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
            Interlocked.Exchange(ref _pendingDisconnectReason, null);

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
                _client = null;
                SafeDisposeClient(oldClient, "connect swap");
            }

            var runtime = CreateRuntimeNetworkConfigSnapshot();
            var clientOptions = CreateClientOptions();
            var effectiveMessageTimeout = GetEffectiveMessageTimeout(runtime.ConnectTimeout);
            var client = new SoulseekClient(minorVersion: _config.SoulseekMinorVersion, options: clientOptions);
            _client = client;
            _lastAppliedRuntimeNetworkConfig = runtime;

            _logger.LogInformation(
                "Soulseek client configured: minorVersion={MinorVersion}, messageTimeout={MessageTimeout}ms, listenPort={ListenPort}, maxSearches={MaxSearches}, maxDownloads={MaxDownloads}",
                _config.SoulseekMinorVersion,
                effectiveMessageTimeout,
                runtime.ListenPort,
                Math.Clamp(_config.MaxConcurrentSearches, 1, 3),
                Math.Clamp(_config.MaxConcurrentDownloads, 1, 10));
            
            // Subscribe to state changes BEFORE connecting to catch early login states
            client.StateChanged += (sender, args) =>
            {
                if (!ReferenceEquals(sender, _client))
                {
                    _logger.LogDebug(
                        "Ignoring Soulseek state change from stale client instance: {State} (was {PreviousState})",
                        args.State,
                        args.PreviousState);
                    return;
                }

                var state = args.State;
                var previousState = args.PreviousState;
                QueueLibraryCallback("StateChanged", () =>
                {
                    _logger.LogInformation("Soulseek state change: {State} (was {PreviousState})",
                        state, previousState);

                    var disconnectBucket = ClassifyDisconnectBucket(
                        Interlocked.CompareExchange(ref _lastDiagnosticMessage, null, null),
                        previousState);

                    var disconnectingFallback = $"DROP:[{disconnectBucket}] unplanned disconnecting while previous={previousState}";
                    var disconnectedFallback = $"DROP:[{disconnectBucket}] unplanned disconnected while previous={previousState}";

                    if (state.HasFlag(SoulseekClientStates.Disconnecting))
                    {
                        var reason = PeekPendingDisconnectReasonOrDefault(disconnectingFallback);
                        _eventBus.Publish(new SoulseekConnectionStatusEvent("disconnecting", _config.Username ?? "Unknown", reason));
                    }

                    if (state.HasFlag(SoulseekClientStates.Disconnected))
                    {
                        var reason = ConsumePendingDisconnectReasonOrDefault(disconnectedFallback);
                        _eventBus.Publish(new SoulseekConnectionStatusEvent("disconnected", _config.Username ?? "Unknown", reason));
                    }

                    _healthService.RecordConnectionStateChange(state.ToString());

                    _eventBus.Publish(new SoulseekStateChangedEvent(
                        State: state.ToString(),
                        IsConnected: state.HasFlag(SoulseekClientStates.Connected) && !state.HasFlag(SoulseekClientStates.Disconnecting),
                        IsConnecting: state.HasFlag(SoulseekClientStates.Connecting),
                        IsLoggingIn: state.HasFlag(SoulseekClientStates.Connected)
                                     && !state.HasFlag(SoulseekClientStates.LoggedIn)
                                     && !state.HasFlag(SoulseekClientStates.Disconnecting)
                                     && !state.HasFlag(SoulseekClientStates.Disconnected),
                        IsLoggedIn: state.HasFlag(SoulseekClientStates.LoggedIn),
                        IsDisconnecting: state.HasFlag(SoulseekClientStates.Disconnecting),
                        IsDisconnected: state.HasFlag(SoulseekClientStates.Disconnected)));
                });
            };

            client.DiagnosticGenerated += (sender, args) =>
            {
                if (!ReferenceEquals(sender, _client))
                {
                    return;
                }

                Interlocked.Exchange(ref _lastDiagnosticMessage, args.Message);
                _logger.LogDebug("[SoulseekLib] {Level}: {Message}", args.Level, args.Message);
            };

            client.KickedFromServer += (sender, args) =>
            {
                if (!ReferenceEquals(sender, _client))
                {
                    return;
                }

                QueueLibraryCallback("KickedFromServer", () =>
                {
                    _logger.LogWarning("Soulseek server kicked this session. Enforcing reconnect cooldown.");
                    MarkPendingDisconnectReason("kicked from server");
                    _healthService.RecordConnectionKick("KickedFromServer event");
                    _eventBus.Publish(new SoulseekConnectionStatusEvent("kicked", _config.Username ?? "Unknown", "kicked from server"));
                });
            };

            // Phase 5/10: Adhere to new global exclusions from Soulseek Server
            client.ExcludedSearchPhrasesReceived += (sender, phrases) =>
            {
                if (!ReferenceEquals(sender, _client))
                {
                    return;
                }

                var phraseSnapshot = phrases?.ToArray() ?? Array.Empty<string>();
                QueueLibraryCallback("ExcludedSearchPhrasesReceived", () =>
                {
                    var phraseList = phraseSnapshot
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
                });
            };

            _logger.LogInformation("Connecting to Soulseek as {Username} on {Server}:{Port}...", 
                _config.Username, _config.SoulseekServer, _config.SoulseekPort);
            
            await client.ConnectAsync(
                _config.SoulseekServer ?? "server.slsknet.org", 
                _config.SoulseekPort == 0 ? 2242 : _config.SoulseekPort, 
                _config.Username, 
                password, 
                ct);

            await EnsureUpnpPortMappingAsync(ct);
            
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
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Soulseek connect attempt was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            var bucket = ClassifyConnectFailureBucket(ex);
            _logger.LogError(ex, "Failed to connect to Soulseek: {Message}. Bucket=[{Bucket}]", ex.Message, bucket);
            
            // Diagnose connection failure type
            var failureStatus = DiagnoseConnectionFailure(ex);
            _healthService.RecordConnectionFailure(failureStatus, $"DROP:[{bucket}] {ex.Message}");
            
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<bool> ApplyRuntimeNetworkConfigurationAsync(CancellationToken ct = default)
    {
        var client = _client;
        if (client == null)
        {
            _logger.LogDebug("Runtime network reconfigure skipped because Soulseek client has not been created yet.");
            return false;
        }

        var runtime = CreateRuntimeNetworkConfigSnapshot();
        if (_lastAppliedRuntimeNetworkConfig == runtime)
        {
            _logger.LogDebug(
                "Runtime network reconfigure skipped because connectTimeout={ConnectTimeout}ms and listenPort={ListenPort} are unchanged.",
                runtime.ConnectTimeout,
                runtime.ListenPort);
            return false;
        }

        var isOperational = client.State.HasFlag(SoulseekClientStates.Connected) &&
                            !client.State.HasFlag(SoulseekClientStates.Disconnecting) &&
                            !client.State.HasFlag(SoulseekClientStates.Disconnected);

        if (!isOperational)
        {
            _lastAppliedRuntimeNetworkConfig = runtime;
            _logger.LogInformation(
                "Deferred runtime network reconfigure until next Soulseek connect (connectTimeout={ConnectTimeout}ms, listenPort={ListenPort}, state={State}).",
                runtime.ConnectTimeout,
                runtime.ListenPort,
                client.State);
            return false;
        }

        var patch = CreateRuntimeNetworkOptionsPatch(runtime);
        var changed = await client.ReconfigureOptionsAsync(patch, ct);
        _lastAppliedRuntimeNetworkConfig = runtime;

        _logger.LogInformation(
            "Applied Soulseek runtime network reconfigure: changed={Changed}, connectTimeout={ConnectTimeout}ms, listenPort={ListenPort}",
            changed,
            runtime.ConnectTimeout,
            runtime.ListenPort);

        return changed;
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
            await PublishSharedCountsStagedAsync(shareFolders.Length, sharedFileCount, ct);
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
            MarkPendingDisconnectReason(reason);
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

        var maxOutboundSearches = Math.Clamp(_config.MaxConcurrentSearches, 1, 3);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var currentInFlight = Volatile.Read(ref _outboundSearchInFlight);
            if (currentInFlight < maxOutboundSearches
                && Interlocked.CompareExchange(ref _outboundSearchInFlight, currentInFlight + 1, currentInFlight) == currentInFlight)
            {
                break;
            }

            await Task.Delay(25, ct);
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
            while (true)
            {
                var extraDelay = Math.Max(0, executionProfile?.AdditionalThrottleDelayMs ?? 0);
                var tokenCapacity = Math.Max(1, executionProfile?.TokenBucketCapacity ?? _config.SearchTokenBucketCapacity);
                var tokenRefillMs = Math.Max(500, executionProfile?.TokenRefillIntervalMs ?? _config.SearchTokenBucketRefillMs);
                var waitMs = 0;

                await _rateLimitLock.WaitAsync(ct);
                try
                {
                    RefillSearchTokens(tokenCapacity, tokenRefillMs);
                    if (_searchBucketTokens >= 1d)
                    {
                        _searchBucketTokens -= 1d;
                        break;
                    }

                    waitMs = MillisecondsUntilNextToken(tokenRefillMs) + extraDelay;
                    _logger.LogDebug(
                        "Search token bucket empty. Waiting {WaitMs}ms before dispatch (capacity={Capacity}, refill={RefillMs}ms).",
                        waitMs,
                        tokenCapacity,
                        tokenRefillMs);
                }

                finally
                {
                    _rateLimitLock.Release();
                }

                await Task.Delay(waitMs, ct);
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
            if (executionProfile != null)
            {
                _eventBus.Publish(new SearchPressureStatusEvent(
                    executionProfile.PressureLevel.ToString(),
                    Math.Max(20, responseLimit),
                    Math.Max(20, fileLimit),
                    Math.Max(1, executionProfile.EffectiveVariationCap),
                    Math.Max(0, executionProfile.AdditionalThrottleDelayMs)));
            }
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
                        PublishTracksInBatches(onTracksFound, foundTracksInResponse, 50);
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
        finally
        {
            Interlocked.Decrement(ref _outboundSearchInFlight);
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
        const int retryDelayMs = 500;
        var maxWaitRetries = Math.Max(20, GetEffectiveConnectTimeout(_config.ConnectTimeout) / retryDelayMs);
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
        Action<TransferLifecycleUpdate>? lifecycleUpdate = null,
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
            bool transferStartedOrQueued = false;
            TransferLifecyclePhase? lastPhase = null;
            var connectFailFastSeconds = Math.Clamp(_config.PeerConnectFailFastSeconds, 5, 30);
            var stallTimeoutSeconds = Math.Clamp(_config.TransferStallTimeoutSeconds, 15, 180);

            var downloadOptions = new TransferOptions(
                stateChanged: (args) =>
                {
                    // Update queued status
                    if (args.Transfer.State.HasFlag(TransferStates.Queued))
                    {
                        isQueued = true;
                        transferStartedOrQueued = true;
                        if (lastPhase != TransferLifecyclePhase.RemoteQueued)
                        {
                            lastPhase = TransferLifecyclePhase.RemoteQueued;
                            lifecycleUpdate?.Invoke(new TransferLifecycleUpdate(
                                TransferLifecyclePhase.RemoteQueued,
                                "Queued remotely by peer"));
                        }
                    }
                    else if (args.Transfer.State.HasFlag(TransferStates.InProgress))
                    {
                        isQueued = false;
                        transferStartedOrQueued = true;
                        if (lastPhase != TransferLifecyclePhase.Transferring)
                        {
                            lastPhase = TransferLifecyclePhase.Transferring;
                            lifecycleUpdate?.Invoke(new TransferLifecycleUpdate(
                                TransferLifecyclePhase.Transferring,
                                startOffset > 0 ? "Transfer resumed" : "Transfer started"));
                        }
                        
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
                var idleSeconds = (DateTime.UtcNow - lastActivity).TotalSeconds;
                if (!transferStartedOrQueued && idleSeconds > connectFailFastSeconds)
                {
                    throw new TimeoutException($"Peer did not respond within {connectFailFastSeconds} seconds.");
                }

                // Check if we should time out
                // Modified: Only timeout if NOT queued and no activity for configured stall window
                if (!isQueued && idleSeconds > stallTimeoutSeconds)
                {
                    // STALLED: Not queued, but no bytes moved for configured timeout
                    throw new TimeoutException($"Transfer stalled for {stallTimeoutSeconds} seconds (0 bytes received).");
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

        if (ex is LoginRejectedException ||
            message.Contains("login rejected") ||
            message.Contains("incorrect password") ||
            message.Contains("invalid password") ||
            message.Contains("invalid credentials"))
        {
            return ConnectionFailureStatus.LoginRejected;
        }
        
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
