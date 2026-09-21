using System;
using System.Collections.Generic;

namespace SLSKDONET.Utils;

/// <summary>
/// Fixed-capacity, thread-safe least-recently-used cache. Once at capacity, adding a new entry
/// evicts whichever key was touched longest ago (by <see cref="Set"/> or <see cref="TryGet"/>).
///
/// Built for <see cref="Services.ArtworkCacheService"/> to hold a bounded set of STRONG
/// references on top of its existing WeakReference dictionary — the WeakReference cache alone
/// lets the GC reclaim a scrolled-off-screen bitmap the instant nothing else references it, so
/// scrolling back even one row could force a full re-decode of artwork that was just on screen.
/// Pinning the most recently touched N entries here (regardless of what ViewModels currently
/// hold) absorbs that churn within a fixed, predictable memory budget.
/// </summary>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();

    public BoundedLruCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(capacity);
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    /// <summary>Inserts or updates a key, marking it most-recently-used. May evict the current
    /// least-recently-used entry if this pushes the cache over capacity.</summary>
    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }

            var node = new LinkedListNode<Entry>(new Entry(key, value));
            _order.AddFirst(node);
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var lru = _order.Last;
                if (lru is null) break;
                _order.RemoveLast();
                _map.Remove(lru.Value.Key);
            }
        }
    }

    /// <summary>Looks up a key, marking it most-recently-used on a hit.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
        }
    }

    private readonly record struct Entry(TKey Key, TValue Value);
}
