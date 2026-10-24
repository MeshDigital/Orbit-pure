using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.Enrichment;

/// <summary>
/// Paces one provider's requests by <see cref="RatePolicy"/>. Each caller reserves the next free
/// slot (a timestamp) and waits until it arrives, so callers are served in order and the provider
/// never sees more than the policy allows, however many workers share it.
/// <para>
/// Slot rules: at least MinSpacing (+ random jitter) after the previous slot, and never more than
/// PermitsPerWindow slots inside any Window. <see cref="Reserve"/> is the pure part (tested with a
/// synthetic clock); <see cref="WaitAsync"/> adds the real delay.
/// </para>
/// </summary>
public sealed class ProviderRateLimiter
{
    private readonly RatePolicy _policy;
    private readonly Func<DateTime> _clock;
    private readonly Random _random;
    private readonly object _lock = new();
    private readonly Queue<DateTime> _granted = new();
    private DateTime _last = DateTime.MinValue;

    public ProviderRateLimiter(RatePolicy policy, Func<DateTime>? clock = null, Random? random = null)
    {
        _policy = policy;
        _clock = clock ?? (() => DateTime.UtcNow);
        _random = random ?? new Random();
    }

    public RatePolicy Policy => _policy;

    /// <summary>Reserves the next slot and returns when it is (UTC). Does not wait.</summary>
    public DateTime Reserve()
    {
        lock (_lock)
        {
            var now = _clock();
            var slot = now < _last ? _last : now; // slots never go backwards

            if (_last != DateTime.MinValue && _policy.MinSpacing > TimeSpan.Zero)
            {
                var jitter = _policy.Jitter > TimeSpan.Zero
                    ? TimeSpan.FromTicks((long)(_random.NextDouble() * _policy.Jitter.Ticks))
                    : TimeSpan.Zero;
                var earliest = _last + _policy.MinSpacing + jitter;
                if (earliest > slot) slot = earliest;
            }

            if (_policy.PermitsPerWindow != int.MaxValue)
            {
                // Forget slots that are out of the window relative to the candidate slot.
                while (_granted.Count > 0 && _granted.Peek() <= slot - _policy.Window) _granted.Dequeue();
                if (_granted.Count >= _policy.PermitsPerWindow)
                {
                    // The oldest slot in the window must drop out first.
                    var oldestInWindow = _granted.ToArray()[_granted.Count - _policy.PermitsPerWindow];
                    var free = oldestInWindow + _policy.Window;
                    if (free > slot) slot = free;
                    while (_granted.Count > 0 && _granted.Peek() <= slot - _policy.Window) _granted.Dequeue();
                }
                _granted.Enqueue(slot);
            }

            _last = slot;
            return slot;
        }
    }

    /// <summary>Waits for this caller's slot.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        var slot = Reserve();
        var delay = slot - _clock();
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}
