using System;

namespace SLSKDONET.Models;

/// <summary>
/// A single outbound network call observed by the network activity monitor.
/// Protocol: "Soulseek" | "HTTP" | "Socket" | "DNS".
/// Kind: operation name (e.g. "Search", "SendMessage", "Download", "Connect", "GET").
/// Detail: human-readable target (query text, URL host+path, remote endpoint, hostname).
/// </summary>
public record NetworkActivityEvent(
    DateTime TimestampUtc,
    string Protocol,
    string Kind,
    string Detail,
    long? DurationMs,
    bool Success);
