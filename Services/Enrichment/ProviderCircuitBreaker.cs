using System;

namespace SLSKDONET.Services.Enrichment;

/// <summary>
/// Per-provider circuit breaker. 429 (or a provider quota error) opens it until Retry-After (or an
/// exponential back-off from 30 s up to 15 min when the provider gives none). Three 5xx/network
/// failures in a row open it for 60 s. 401/403 three times in a row switch the provider off for
/// the rest of the session (<see cref="IsDisabledForSession"/>), which the UI surfaces.
/// Pure state with an injectable clock; thread-safe.
/// </summary>
public sealed class ProviderCircuitBreaker
{
    public const int ForbiddenLimit = 3;
    public const int FailureLimit = 3;
    public static readonly TimeSpan DefaultRateLimitPause = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxRateLimitPause = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FailurePause = TimeSpan.FromSeconds(60);

    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private DateTime _openUntil = DateTime.MinValue;
    private int _consecutiveRateLimits;
    private int _consecutiveForbidden;
    private int _consecutiveFailures;

    public ProviderCircuitBreaker(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public bool IsDisabledForSession { get; private set; }
    public string? DisabledReason { get; private set; }
    public int RateLimitPauses { get; private set; }

    /// <summary>When the breaker closes again, or null when it is closed.</summary>
    public DateTime? OpenUntil
    {
        get { lock (_lock) return _openUntil > _clock() ? _openUntil : null; }
    }

    public bool IsOpen => IsDisabledForSession || OpenUntil.HasValue;

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveRateLimits = 0;
            _consecutiveForbidden = 0;
            _consecutiveFailures = 0;
        }
    }

    /// <summary>A 429 / quota answer. Returns when requests may resume.</summary>
    public DateTime RecordRateLimited(TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            _consecutiveRateLimits++;
            RateLimitPauses++;
            TimeSpan pause;
            if (retryAfter is { } ra && ra > TimeSpan.Zero)
                pause = ra;
            else
            {
                var factor = Math.Pow(2, Math.Min(_consecutiveRateLimits - 1, 5));
                pause = TimeSpan.FromTicks(Math.Min(MaxRateLimitPause.Ticks, (long)(DefaultRateLimitPause.Ticks * factor)));
            }
            var until = _clock() + pause;
            if (until > _openUntil) _openUntil = until;
            return _openUntil;
        }
    }

    /// <summary>A 401/403. The third in a row disables the provider for this session.</summary>
    public void RecordForbidden(string reason)
    {
        lock (_lock)
        {
            _consecutiveForbidden++;
            if (_consecutiveForbidden >= ForbiddenLimit)
            {
                IsDisabledForSession = true;
                DisabledReason = reason;
            }
        }
    }

    /// <summary>5xx, timeout or network error.</summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= FailureLimit)
            {
                var until = _clock() + FailurePause;
                if (until > _openUntil) _openUntil = until;
                _consecutiveFailures = 0;
            }
        }
    }
}
