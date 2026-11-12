using System;
using System.Collections.Concurrent;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services.Downloads;

/// <summary>
/// Tracks active downloads keyed by (peer, remote path) so that two tracks coming from the
/// same peer never share progress/stall bookkeeping (issue #233).
/// </summary>
public sealed class ActiveTransferRegistry
{
    public readonly record struct TransferKey(string Username, string RemotePath)
    {
        public bool Equals(TransferKey other) =>
            string.Equals(Username, other.Username, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(RemotePath, other.RemotePath, StringComparison.Ordinal);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Username),
                             StringComparer.Ordinal.GetHashCode(RemotePath));
    }

    private readonly ConcurrentDictionary<TransferKey, DownloadContext> _contexts = new();
    private readonly ConcurrentDictionary<TransferKey, long> _lastBytes = new();

    public void Register(TransferKey key, DownloadContext ctx) => _contexts[key] = ctx;

    public bool TryGet(TransferKey key, out DownloadContext? ctx) => _contexts.TryGetValue(key, out ctx);

    public void Remove(TransferKey key)
    {
        _contexts.TryRemove(key, out _);
        _lastBytes.TryRemove(key, out _);
    }

    /// <summary>
    /// Records the latest byte count for <paramref name="key"/> and returns the delta since the
    /// previous call (0 when the count did not advance).
    /// </summary>
    /// <summary>Sets the resume baseline so the first progress sample doesn't count bytes already on disk.</summary>
    public void Seed(TransferKey key, long initialBytes) => _lastBytes[key] = initialBytes;

    public long RecordBytes(TransferKey key, long bytesReceived)
    {
        var previous = _lastBytes.TryGetValue(key, out var old) ? old : 0;
        _lastBytes[key] = bytesReceived;
        var delta = bytesReceived - previous;
        return delta > 0 ? delta : 0;
    }

    public bool HasAnyForUser(string username)
    {
        if (string.IsNullOrEmpty(username)) return false;
        foreach (var key in _contexts.Keys)
        {
            if (string.Equals(key.Username, username, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}