using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Catch-all visibility layer for outbound network traffic that ORBIT's own Soulseek call-site
/// instrumentation (see <c>SoulseekAdapter.TrackNetworkCallAsync</c>) can't see: HTTP requests
/// (Spotify/MusicBrainz/tracklist scraping — including SpotifyAPI.Web's opaque internal client,
/// since <c>System.Net.Http</c>'s EventSource fires per logical request regardless of which
/// HttpClient instance made it) and raw socket connects/DNS lookups.
///
/// Every event observed here, plus every <see cref="NetworkActivityEvent"/> published elsewhere
/// (Soulseek operations), is recorded into a single bounded history via a self-subscription to the
/// event bus — giving one unified feed for Settings → Advanced → Network Activity.
/// </summary>
public sealed class NetworkActivityMonitor : EventListener
{
    private const int MaxHistoryEntries = 500;

    private static readonly HashSet<string> TrackedSourceNames = new(StringComparer.Ordinal)
    {
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.NameResolution",
    };

    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;
    private readonly ILogger<NetworkActivityMonitor> _logger;
    private readonly ConcurrentQueue<NetworkActivityEvent> _history = new();
    private readonly object _pendingLock = new();
    private List<EventSource>? _pendingSources = new();
    private bool _initialized;
    private IDisposable? _historySubscription;

    public NetworkActivityMonitor(AppConfig config, IEventBus eventBus, ILogger<NetworkActivityMonitor> logger)
    {
        _config = config;
        _eventBus = eventBus;
        _logger = logger;

        // EventListener's base constructor can invoke OnEventSourceCreated (for EventSources that
        // already exist at construction time) before this constructor body runs — at that point
        // _config/_eventBus above would still be null. Anything queued during that window is
        // flushed here, now that fields are guaranteed assigned.
        List<EventSource>? pending;
        lock (_pendingLock)
        {
            _initialized = true;
            pending = _pendingSources;
            _pendingSources = null;
        }
        if (pending != null)
        {
            foreach (var source in pending)
                EnableEvents(source, EventLevel.Informational);
        }

        _historySubscription = _eventBus.GetEvent<NetworkActivityEvent>().Subscribe(RecordHistory);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource?.Name == null || !TrackedSourceNames.Contains(eventSource.Name))
            return;

        lock (_pendingLock)
        {
            if (!_initialized)
            {
                _pendingSources!.Add(eventSource);
                return;
            }
        }

        EnableEvents(eventSource, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        try
        {
            if (!_config.EnableNetworkActivityMonitor) return;
            if (eventData.EventSource?.Name == null) return;

            var protocol = eventData.EventSource.Name switch
            {
                "System.Net.Http" => "HTTP",
                "System.Net.Sockets" => "Socket",
                "System.Net.NameResolution" => "DNS",
                _ => (string?)null
            };
            if (protocol == null) return;

            NetworkActivityEvent? activity = eventData.EventName switch
            {
                "RequestStart" => new NetworkActivityEvent(DateTime.UtcNow, protocol, "Request", DescribeHttpRequestStart(eventData), null, true),
                "ConnectStart" => new NetworkActivityEvent(DateTime.UtcNow, protocol, "Connect", GetPayloadString(eventData, "address") ?? "unknown", null, true),
                "ConnectFailed" => new NetworkActivityEvent(DateTime.UtcNow, protocol, "Connect", GetPayloadString(eventData, "exceptionMessage") ?? "failed", null, false),
                "ResolutionStart" => new NetworkActivityEvent(DateTime.UtcNow, protocol, "Resolve", GetPayloadString(eventData, "hostNameOrAddress") ?? "unknown", null, true),
                _ => null
            };
            if (activity == null) return;

            _eventBus.Publish(activity);
        }
        catch (Exception ex)
        {
            // EventListener callbacks running on ETW/runtime threads must never throw — swallow and
            // log at Debug so a malformed/unexpected payload can't destabilize the process.
            _logger.LogDebug(ex, "NetworkActivityMonitor failed to process a runtime network event; ignoring.");
        }
    }

    private void RecordHistory(NetworkActivityEvent evt)
    {
        _history.Enqueue(evt);
        while (_history.Count > MaxHistoryEntries && _history.TryDequeue(out _)) { }
    }

    /// <summary>Newest-first snapshot of recent activity, for seeding a UI panel on open.</summary>
    public IReadOnlyList<NetworkActivityEvent> GetRecentActivity(int maxEntries = 200)
    {
        return _history.Reverse().Take(maxEntries).ToList();
    }

    /// <summary>Number of recorded calls within <paramref name="window"/> of now, optionally filtered by protocol.</summary>
    public int CountSince(TimeSpan window, string? protocolFilter = null)
    {
        var cutoff = DateTime.UtcNow - window;
        return _history.Count(e => e.TimestampUtc >= cutoff && (protocolFilter == null || e.Protocol == protocolFilter));
    }

    private static string? GetPayloadString(EventWrittenEventArgs eventData, string name)
    {
        var names = eventData.PayloadNames;
        var payload = eventData.Payload;
        if (names == null || payload == null) return null;

        var index = names.IndexOf(name);
        return index >= 0 && index < payload.Count ? payload[index]?.ToString() : null;
    }

    private static string DescribeHttpRequestStart(EventWrittenEventArgs eventData)
    {
        var scheme = GetPayloadString(eventData, "scheme");
        var host = GetPayloadString(eventData, "host");
        var port = GetPayloadString(eventData, "port");
        var path = GetPayloadString(eventData, "pathAndQuery");

        if (string.IsNullOrEmpty(host)) return "unknown";

        var portSuffix = string.IsNullOrEmpty(port) || port is "80" or "443" ? "" : $":{port}";
        return $"{scheme}://{host}{portSuffix}{path}";
    }

    public override void Dispose()
    {
        _historySubscription?.Dispose();
        _historySubscription = null;
        base.Dispose();
    }
}
