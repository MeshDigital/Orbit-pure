using System.Diagnostics;
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
using System.Net.Sockets;
using System.Reflection;
using Open.Nat;

namespace SLSKDONET.Services;

public sealed class SearchLimitExceededException : Exception
{
    public int HardResultCap { get; }
    public int HardFileCap { get; }

    public SearchLimitExceededException(string message, int hardResultCap, int hardFileCap)
        : base(message)
    {
        HardResultCap = hardResultCap;
        HardFileCap = hardFileCap;
    }
}

/// <summary>
/// Real Soulseek.NET adapter for network interactions.
/// </summary>
public partial class SoulseekAdapter : ISoulseekAdapter, IDisposable
{
    private sealed record RuntimeNetworkConfigSnapshot(int ConnectTimeout, int ListenPort);

    private static readonly HashSet<string> NonMetadataPathTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop", "downloads", "download", "temp", "incoming", "new folder", "music", "audio"
    };

    private readonly ILogger<SoulseekAdapter> _logger;
    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;
    private readonly FrequentSourceService? _frequentSourceService;
    private readonly ShareIndexService _shareIndex;
    private readonly ChatAttachmentService _chatAttachments;
    private const int MaxConcurrentUploads = 10;
    private int _activeUploadCount;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    public bool IsConnected => _client?.State.HasFlag(SoulseekClientStates.Connected) == true && 
                              !_client.State.HasFlag(SoulseekClientStates.Disconnecting);
    public int SharedFileCount { get; private set; }
    
    public bool IsLoggedIn => _client?.State.HasFlag(SoulseekClientStates.LoggedIn) == true;
    
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;
    public event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;
    public event EventHandler<UserStatusChangedEventArgs>? UserStatusChanged;
    public event EventHandler<PrivateMessageReceivedEventArgs>? PrivateMessageReceived;
    public event EventHandler<RoomMessageReceivedEventArgs>? RoomMessageReceived;
    public event EventHandler<RoomMembershipChangedEventArgs>? RoomMembershipChanged;

    // Rate Limiting
    private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private DateTime _searchBucketLastRefillUtc = DateTime.UtcNow;
    private double _searchBucketTokens = 1d;
    // Separate bucket/lock for outgoing chat (private + room) messages — independent of search so
    // the two operation categories don't serialize against or starve each other. See
    // AppConfig.MessageTokenBucketCapacity/RefillMs.
    private readonly SemaphoreSlim _messageRateLimitLock = new(1, 1);
    private DateTime _messageBucketLastRefillUtc = DateTime.UtcNow;
    private double _messageBucketTokens = 1d;
    private readonly SemaphoreSlim _upnpLock = new(1, 1);
    private bool _upnpPortMapped;
    private DateTime _lastUpnpAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan UpnpAttemptCooldown = TimeSpan.FromMinutes(5);

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
        "ExcludedSearchPhrasesReceived",
        "GlobalMessageReceived",
        "UserStatusChanged",
        "PrivateMessageReceived",
        "RoomMessageReceived",
        "RoomJoined",
        "RoomLeft"
    };

    public SoulseekAdapter(ILogger<SoulseekAdapter> logger, AppConfig config, Network.ProtocolHardeningService hardeningService, IEventBus eventBus, INetworkHealthService healthService, ShareIndexService shareIndex, ChatAttachmentService chatAttachments, FrequentSourceService? frequentSourceService = null)
    {
        _logger = logger;
        _config = config;
        _hardeningService = hardeningService;
        _eventBus = eventBus;
        _healthService = healthService;
        _shareIndex = shareIndex;
        _shareIndex.CountsChanged += OnShareCountsChanged;
        _chatAttachments = chatAttachments;
        _frequentSourceService = frequentSourceService;
    }

    private SoulseekClient? _client;

    private readonly ResultFingerprinter _resultFingerprinter = new();

    private static int GetEffectiveConnectTimeout(int configuredTimeout)
        => Math.Max(60_000, configuredTimeout);

    private static int GetEffectiveMessageTimeout(int effectiveConnectTimeout)
        => Math.Max(120_000, effectiveConnectTimeout);

    private static int GetEffectiveListenPort(int configuredListenPort)
        => Math.Clamp(configuredListenPort, 1_024, 65_535);

    // Was hardcoded to Math.Clamp(_config.MaxConcurrentDownloads, 1, 10) — silently overriding
    // any user setting above 10 even though the Settings/Downloads-page slider allows up to 20
    // and DownloadManager's own app-level semaphore allows up to 50. Soulseek.NET has no inherent
    // limit here (library default is int.MaxValue) — 10 was an ORBIT-side ceiling that just never
    // got raised to match the UI. This value is fixed for the lifetime of the connection —
    // SoulseekClientOptionsPatch (used for live reconfiguration) does not include
    // MaximumConcurrentDownloads, so a change here only takes full effect after reconnecting.
    private int GetEffectiveMaxConcurrentDownloads()
        => Math.Clamp(_config.MaxConcurrentDownloads, 1, 50);

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

    /// <summary>
    /// Blocks (without holding the caller's own locks) until an outgoing-message token is
    /// available, per <see cref="AppConfig.MessageTokenBucketCapacity"/>/<c>RefillMs</c>. Same
    /// token-bucket shape as the search rate limiter above, kept independent so chat sends never
    /// wait on search dispatch or vice versa.
    /// </summary>
    private async Task WaitForMessageTokenAsync(CancellationToken ct)
    {
        var capacity = Math.Max(1, _config.MessageTokenBucketCapacity);
        var refillMs = Math.Max(100, _config.MessageTokenBucketRefillMs);

        while (true)
        {
            int waitMs;
            await _messageRateLimitLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                var elapsedMs = (now - _messageBucketLastRefillUtc).TotalMilliseconds;
                if (elapsedMs > 0)
                {
                    var refillTokens = elapsedMs / refillMs;
                    if (refillTokens > 0)
                    {
                        _messageBucketTokens = Math.Min(capacity, _messageBucketTokens + refillTokens);
                        _messageBucketLastRefillUtc = now;
                    }
                }

                if (_messageBucketTokens >= 1d)
                {
                    _messageBucketTokens -= 1d;
                    return;
                }

                var missing = 1d - _messageBucketTokens;
                waitMs = Math.Max(25, (int)Math.Ceiling(missing * refillMs));
            }
            finally
            {
                _messageRateLimitLock.Release();
            }

            await Task.Delay(waitMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Observational wrapper for a single Soulseek protocol call (search, message, download, browse,
    /// presence, room ops, connect). Every Soulseek operation after the initial connect reuses the
    /// same already-open TCP connection, so raw socket-level monitoring cannot distinguish one
    /// operation from another — this call-site wrapper is the only place each operation is
    /// individually observable. Publishes a <see cref="NetworkActivityEvent"/> for the Settings →
    /// Advanced → Network Activity feed; never changes control flow (exceptions propagate unchanged).
    /// </summary>
    private async Task<T> TrackNetworkCallAsync<T>(string kind, string detail, Func<Task<T>> action)
    {
        if (!_config.EnableNetworkActivityMonitor) return await action();

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await action();
            _eventBus.Publish(new NetworkActivityEvent(DateTime.UtcNow, "Soulseek", kind, detail, sw.ElapsedMilliseconds, true));
            return result;
        }
        catch
        {
            _eventBus.Publish(new NetworkActivityEvent(DateTime.UtcNow, "Soulseek", kind, detail, sw.ElapsedMilliseconds, false));
            throw;
        }
    }

    /// <summary>Non-generic overload for calls that return <see cref="Task"/> rather than <see cref="Task{T}"/>.</summary>
    private async Task TrackNetworkCallAsync(string kind, string detail, Func<Task> action)
    {
        if (!_config.EnableNetworkActivityMonitor) { await action(); return; }

        var sw = Stopwatch.StartNew();
        try
        {
            await action();
            _eventBus.Publish(new NetworkActivityEvent(DateTime.UtcNow, "Soulseek", kind, detail, sw.ElapsedMilliseconds, true));
        }
        catch
        {
            _eventBus.Publish(new NetworkActivityEvent(DateTime.UtcNow, "Soulseek", kind, detail, sw.ElapsedMilliseconds, false));
            throw;
        }
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

    // Configures OS-level TCP keepalives on the Soulseek server socket.
    // Without this, a silently-dropped NAT mapping or router reboot leaves the TCP socket
    // half-open indefinitely — the app thinks it's connected but no data ever arrives.
    // With keepalives: after 30 s of idle the OS sends probes every 5 s; after 3 failures
    // (~45 s total) the socket is closed and Soulseek.NET fires the disconnect event.
    private static void ConfigureServerSocket(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        // Reuse the listen address so a reconnect never blocks on a lingering TIME_WAIT socket.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (OperatingSystem.IsWindows())
        {
            // tcp_keepalive struct: [onoff(4)] [keepalivetime(4)] [keepaliveinterval(4)] — all little-endian uint32
            byte[] ka = new byte[12];
            BitConverter.GetBytes((uint)1).CopyTo(ka, 0);        // enable
            BitConverter.GetBytes((uint)30_000).CopyTo(ka, 4);   // idle before first probe: 30 s
            BitConverter.GetBytes((uint)5_000).CopyTo(ka, 8);    // interval between probes: 5 s
            socket.IOControl(IOControlCode.KeepAliveValues, ka, null);
        }
    }

    private SoulseekClientOptions CreateClientOptions()
    {
        var runtime = CreateRuntimeNetworkConfigSnapshot();
        var serverConnectionOptions = new ConnectionOptions(
            connectTimeout: runtime.ConnectTimeout,
            configureSocket: ConfigureServerSocket);
        var messageTimeout = GetEffectiveMessageTimeout(runtime.ConnectTimeout);

        return new SoulseekClientOptions(
            enableListener: true,
            listenIPAddress: IPAddress.Any,
            listenPort: runtime.ListenPort,
            serverConnectionOptions: serverConnectionOptions,
            messageTimeout: messageTimeout,
            // NOTE: Keep Soulseek search concurrency at 1 for correctness.
            // Under concurrent searches, callback payloads can be interleaved across active queries,
            // causing false "no results" decisions in discovery despite valid candidates existing.
            maximumConcurrentSearches: 1,
            maximumConcurrentDownloads: GetEffectiveMaxConcurrentDownloads(),
            maximumConcurrentUploads: MaxConcurrentUploads,
            searchResponseResolver: ResolveSearchResponseAsync,
            browseResponseResolver: ResolveBrowseResponseAsync,
            directoryContentsResolver: ResolveDirectoryContentsAsync,
            userInfoResolver: ResolveUserInfoAsync,
            enqueueDownload: HandleEnqueueDownloadAsync,
            placeInQueueResolver: ResolvePlaceInQueueAsync);
    }

    // ── Serving: answer incoming browse/search/download requests from peers ──────────────
    // Everything here answers from ShareIndexService only — never from a peer-supplied path
    // directly — so an incoming request can only ever resolve to a file we chose to share.

    private Task<SearchResponse> ResolveSearchResponseAsync(string username, int token, Soulseek.SearchQuery query)
    {
        var matches = _shareIndex.Search(query);
        if (matches.Count == 0)
            return Task.FromResult<SearchResponse>(null!);

        var hasFreeSlot = Volatile.Read(ref _activeUploadCount) < MaxConcurrentUploads;
        var files = matches.Select(m => ShareIndexService.BuildFile(m.VirtualPath, m.Entry, basenameOnly: false));
        var response = new SearchResponse(
            _client?.Username ?? username,
            token,
            hasFreeSlot,
            uploadSpeed: 0,
            queueLength: 0,
            files,
            Enumerable.Empty<Soulseek.File>());

        return Task.FromResult(response);
    }

    private Task<BrowseResponse> ResolveBrowseResponseAsync(string username, IPEndPoint endpoint)
    {
        var directories = _shareIndex.GetAllDirectories();
        return Task.FromResult(new BrowseResponse(directories, Enumerable.Empty<Soulseek.Directory>()));
    }

    private Task<IEnumerable<Soulseek.Directory>> ResolveDirectoryContentsAsync(string username, IPEndPoint endpoint, int token, string directoryName)
    {
        var directory = _shareIndex.GetDirectory(directoryName);
        IEnumerable<Soulseek.Directory> result = directory is not null ? new[] { directory } : null!;
        return Task.FromResult(result);
    }

    private Task<UserInfo> ResolveUserInfoAsync(string username, IPEndPoint endpoint)
    {
        var hasFreeSlot = Volatile.Read(ref _activeUploadCount) < MaxConcurrentUploads;
        return Task.FromResult(new UserInfo("ORBIT", MaxConcurrentUploads, 0, hasFreeSlot, Array.Empty<byte>()));
    }

    private Task<int?> ResolvePlaceInQueueAsync(string username, IPEndPoint endpoint, string filename)
        => Task.FromResult<int?>(0);

    private Task HandleEnqueueDownloadAsync(string username, IPEndPoint endpoint, string filename)
    {
        if (_shareIndex.TryGetEntry(filename, out var shareEntry) && shareEntry is not null)
        {
            _ = UploadSharedFileAsync(username, filename, shareEntry);
            return Task.CompletedTask;
        }

        if (_chatAttachments.TryAuthorize(filename, username, out var attachmentEntry) && attachmentEntry is not null)
        {
            _ = UploadSharedFileAsync(username, filename, attachmentEntry);
            return Task.CompletedTask;
        }

        throw new DownloadEnqueueException("File not shared.");
    }

    private async Task UploadSharedFileAsync(string username, string virtualFilename, ShareIndexEntry entry)
    {
        if (_client == null)
            return;

        Interlocked.Increment(ref _activeUploadCount);
        try
        {
            await _client.UploadAsync(username, virtualFilename, entry.LocalPath, cancellationToken: CancellationToken.None);
            _logger.LogInformation("Uploaded {File} to {Username}", virtualFilename, username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upload of {File} to {Username} failed", virtualFilename, username);
        }
        finally
        {
            Interlocked.Decrement(ref _activeUploadCount);
        }
    }

    private SoulseekClientOptionsPatch CreateRuntimeNetworkOptionsPatch(RuntimeNetworkConfigSnapshot runtime)
    {
        var serverConnectionOptions = new ConnectionOptions(
            connectTimeout: runtime.ConnectTimeout,
            configureSocket: ConfigureServerSocket);

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
                1,
                GetEffectiveMaxConcurrentDownloads());
            
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

            // Ban detection: server sends "You have been banned" as a GlobalMessage.
            // When detected, publish SearchBanDetectedEvent so search queues can pause for 30 minutes.
            client.GlobalMessageReceived += (sender, message) =>
            {
                if (!ReferenceEquals(sender, _client)) return;
                if (string.IsNullOrWhiteSpace(message)) return;

                var lower = message.ToLowerInvariant();
                if (lower.Contains("banned") || lower.Contains("ban"))
                {
                    var lockoutUntil = DateTime.UtcNow.AddMinutes(30);
                    _logger.LogWarning(
                        "🚨 [BAN DETECTED] Soulseek server issued ban. Pausing all searches until {Until:HH:mm:ss} UTC. Message: {Message}",
                        lockoutUntil, message);
                    _healthService.RecordConnectionKick("server-ban:" + message);
                    _eventBus.Publish(new SearchBanDetectedEvent(message, lockoutUntil));
                }
                else
                {
                    _logger.LogInformation("[Soulseek GlobalMessage] {Message}", message);
                }
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

            // Social: presence, 1:1 chat, room events — mirror the guard/dispatch pattern above.
            client.UserStatusChanged += (sender, args) =>
            {
                if (!ReferenceEquals(sender, _client)) return;

                QueueLibraryCallback("UserStatusChanged", () =>
                {
                    UserStatusChanged?.Invoke(this, new UserStatusChangedEventArgs(
                        args.Username, MapPresence(args.Presence), args.IsPrivileged));
                });
            };

            client.PrivateMessageReceived += (sender, args) =>
            {
                if (!ReferenceEquals(sender, _client)) return;

                QueueLibraryCallback("PrivateMessageReceived", () =>
                {
                    PrivateMessageReceived?.Invoke(this, new PrivateMessageReceivedEventArgs(
                        args.Id, args.Username, args.Message, args.Timestamp, args.Replayed));
                });
            };

            client.RoomMessageReceived += (sender, args) =>
            {
                if (!ReferenceEquals(sender, _client)) return;

                QueueLibraryCallback("RoomMessageReceived", () =>
                {
                    RoomMessageReceived?.Invoke(this, new RoomMessageReceivedEventArgs(
                        args.RoomName, args.Username, args.Message, DateTime.UtcNow));
                });
            };

            client.RoomJoined += (sender, args) =>
            {
                if (!ReferenceEquals(sender, _client)) return;

                QueueLibraryCallback("RoomJoined", () =>
                {
                    RoomMembershipChanged?.Invoke(this, new RoomMembershipChangedEventArgs(
                        args.RoomName, args.Username, joined: true));
                });
            };

            client.RoomLeft += (sender, args) =>
            {
                if (!ReferenceEquals(sender, _client)) return;

                QueueLibraryCallback("RoomLeft", () =>
                {
                    RoomMembershipChanged?.Invoke(this, new RoomMembershipChangedEventArgs(
                        args.RoomName, args.Username, joined: false));
                });
            };

            _logger.LogInformation("Connecting to Soulseek as {Username} on {Server}:{Port}...",
                _config.Username, _config.SoulseekServer, _config.SoulseekPort);
            
            await TrackNetworkCallAsync("Connect", $"{_config.SoulseekServer}:{_config.SoulseekPort}", () => client.ConnectAsync(
                _config.SoulseekServer ?? "server.slsknet.org",
                _config.SoulseekPort == 0 ? 2242 : _config.SoulseekPort,
                _config.Username,
                password,
                ct));

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

        // Announce what a peer actually gets when browsing: the index's music files and the folders
        // they sit in. This used to send the number of shared ROOTS as the folder count (5) and every
        // file of any type as the file count — leech-detection scripts read these server-side stats,
        // and "5 folders" reads as a tiny share.
        _shareIndex.EnsureFresh();
        var sharedFileCount = _shareIndex.FileCount;
        var sharedDirectoryCount = _shareIndex.DirectoryCount;
        SharedFileCount = sharedFileCount;
        _logger.LogInformation("Publishing shares: {Files} music file(s) in {Dirs} folder(s) from {Roots} root(s): {Folders}",
            sharedFileCount, sharedDirectoryCount, shareFolders.Length, string.Join(", ", shareFolders));

        try
        {
            await PublishSharedCountsStagedAsync(sharedDirectoryCount, sharedFileCount, ct);
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
            SharedFolderCount: sharedDirectoryCount,
            SharedFileCount: sharedFileCount,
            IsSharing: true));
    }

    /// <summary>
    /// The share index changed (downloads finished, a Library Source was added…): tell the server
    /// the new counts right away instead of only at the next login, which is all that happened before.
    /// </summary>
    private void OnShareCountsChanged(object? sender, (int Files, int Directories) counts)
    {
        var client = _client;
        if (client == null || !_config.EnableLibrarySharing) return;
        if (!client.State.HasFlag(SoulseekClientStates.Connected) || !client.State.HasFlag(SoulseekClientStates.LoggedIn)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await client.SetSharedCountsAsync(counts.Directories, counts.Files);
                SharedFileCount = counts.Files;
                _logger.LogInformation("Share counts updated on the server: {Files} file(s) in {Dirs} folder(s)", counts.Files, counts.Directories);
                _eventBus.Publish(new ShareHealthUpdatedEvent(
                    SharedFolderCount: counts.Directories,
                    SharedFileCount: counts.Files,
                    IsSharing: true));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not update share counts on the server");
            }
        });
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
