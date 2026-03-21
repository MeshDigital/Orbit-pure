using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using Soulseek;

namespace SLSKDONET.Services;

/// <summary>
/// Phase B — deterministic connection lifecycle state machine.
///
/// Owns:
///   • The single authoritative <see cref="CurrentState"/> the rest of the
///     app should observe (via <see cref="ConnectionLifecycleStateChangedEvent"/>).
///   • The serialized command gate (one connect attempt at a time).
///   • The auto-reconnect loop with jitter, quick-retry cap, and
///     CoolingDown awareness.
///   • Kick-cooldown enforcement (60 s) before any reconnect is started.
///
/// Callers must use <see cref="RequestConnectAsync"/> /
/// <see cref="RequestDisconnectAsync"/> instead of calling
/// <see cref="ISoulseekAdapter"/> directly.
/// </summary>
public sealed class ConnectionLifecycleService : IConnectionLifecycleService, IDisposable
{
    // ── valid transition table ──────────────────────────────────────────
    private static readonly HashSet<(ConnectionLifecycleState, ConnectionLifecycleState)> ValidTransitions = new()
    {
        (ConnectionLifecycleState.Disconnected,  ConnectionLifecycleState.Connecting),
        (ConnectionLifecycleState.Connecting,    ConnectionLifecycleState.LoggingIn),
        (ConnectionLifecycleState.Connecting,    ConnectionLifecycleState.LoggedIn),   // fast path: state may already include LoggedIn
        (ConnectionLifecycleState.Connecting,    ConnectionLifecycleState.Disconnected),
        (ConnectionLifecycleState.Connecting,    ConnectionLifecycleState.CoolingDown),
        (ConnectionLifecycleState.LoggingIn,     ConnectionLifecycleState.LoggedIn),
        (ConnectionLifecycleState.LoggingIn,     ConnectionLifecycleState.Disconnected),
        (ConnectionLifecycleState.LoggingIn,     ConnectionLifecycleState.CoolingDown),
        (ConnectionLifecycleState.LoggedIn,      ConnectionLifecycleState.Disconnecting),
        (ConnectionLifecycleState.LoggedIn,      ConnectionLifecycleState.CoolingDown),
        (ConnectionLifecycleState.LoggedIn,      ConnectionLifecycleState.Disconnected),
        (ConnectionLifecycleState.Disconnecting, ConnectionLifecycleState.Disconnected),
        (ConnectionLifecycleState.CoolingDown,   ConnectionLifecycleState.Connecting),
        (ConnectionLifecycleState.CoolingDown,   ConnectionLifecycleState.Disconnected),
    };

    private const int MaxQuickRetries        = 3;
    private const int QuickRetryBaseMs       = 5_000;  // 5 s for each of the first 3 retries
    private const int KickCooldownSeconds    = 60;
    private const double JitterFraction      = 0.20;   // ±20 % jitter on all delays

    // ── dependencies ────────────────────────────────────────────────────
    private readonly ILogger<ConnectionLifecycleService> _logger;
    private readonly ISoulseekAdapter _soulseek;
    private readonly IEventBus _eventBus;

    // ── subscriptions ────────────────────────────────────────────────────
    private readonly IDisposable _stateChangedSub;
    private readonly IDisposable _kickedSub;

    // ── state ────────────────────────────────────────────────────────────
    private ConnectionLifecycleState _state = ConnectionLifecycleState.Disconnected;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private string? _lastPassword;
    private volatile bool _manualDisconnect;
    private int _reconnectRetryCount;
    private int _quickRetryCount;
    private DateTime _coolingUntilUtc = DateTime.MinValue;
    private int _reconnectLoopActive;
    private readonly Random _rng = new();
    private CancellationTokenSource? _activeConnectCts;
    private string? _lastDisconnectStatusReason;
    private bool _disposed;

    public ConnectionLifecycleState CurrentState => _state;
    public bool AutoReconnectEnabled { get; set; }

    // ── ctor ─────────────────────────────────────────────────────────────
    public ConnectionLifecycleService(
        ILogger<ConnectionLifecycleService> logger,
        ISoulseekAdapter soulseek,
        IEventBus eventBus)
    {
        _logger    = logger;
        _soulseek  = soulseek;
        _eventBus  = eventBus;

        _stateChangedSub = _eventBus
            .GetEvent<SoulseekStateChangedEvent>()
            .Subscribe(OnSoulseekStateChanged);

        _kickedSub = _eventBus
            .GetEvent<SoulseekConnectionStatusEvent>()
            .Subscribe(OnSoulseekConnectionStatus);
    }

    // ── public API ───────────────────────────────────────────────────────

    public async Task RequestConnectAsync(
        string? password,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        // Reject immediately while in cooldown — caller must wait
        if (_state == ConnectionLifecycleState.CoolingDown)
        {
            var remaining = _coolingUntilUtc - DateTime.UtcNow;
            _logger.LogWarning(
                "Lifecycle: connect request rejected — CoolingDown ({Remaining:F0}s remaining). corr={Corr}",
                Math.Max(0, remaining.TotalSeconds), correlationId ?? "-");
            return;
        }

        await _commandLock.WaitAsync(ct);
        try
        {
            if (_state is ConnectionLifecycleState.LoggedIn)
            {
                _logger.LogDebug("Lifecycle: already LoggedIn — ignoring connect request. corr={Corr}", correlationId ?? "-");
                return;
            }

            if (_state is ConnectionLifecycleState.Connecting or ConnectionLifecycleState.LoggingIn)
            {
                _logger.LogDebug(
                    "Lifecycle: connect already in progress ({State}) — ignoring. corr={Corr}",
                    _state, correlationId ?? "-");
                return;
            }

            if (!string.IsNullOrEmpty(password))
                _lastPassword = password;

            _manualDisconnect = false;
            TryTransition(ConnectionLifecycleState.Connecting, "connect requested", correlationId);

            try
            {
                using var linkedConnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var previousConnectCts = Interlocked.Exchange(ref _activeConnectCts, linkedConnectCts);
                previousConnectCts?.Cancel();

                await _soulseek.ConnectAsync(_lastPassword, linkedConnectCts.Token);

                if (ReferenceEquals(_activeConnectCts, linkedConnectCts))
                {
                    Interlocked.Exchange(ref _activeConnectCts, null);
                }
                // Successful connection — state is advanced by the SoulseekStateChangedEvent
                // handler; no explicit transition needed here.
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _activeConnectCts, null);
                var cancelledByCaller = ct.IsCancellationRequested;
                TryTransition(
                    ConnectionLifecycleState.Disconnected,
                    cancelledByCaller ? "connect cancelled" : "connect interrupted",
                    correlationId);

                if (cancelledByCaller)
                    throw;

                _logger.LogInformation(
                    "Lifecycle: connect attempt interrupted by connection state change. corr={Corr}",
                    correlationId ?? "-");
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _activeConnectCts, null);
                var isLoginRejected = ex is LoginRejectedException;
                if (isLoginRejected)
                {
                    // Invalid credentials are not a transient network failure.
                    // Suppress reconnect looping until the user explicitly retries.
                    _manualDisconnect = true;
                }

                _logger.LogError(
                    ex,
                    "Lifecycle: ConnectAsync failed. reason={Reason} correlationId={CorrelationId}",
                    ex.Message,
                    correlationId ?? "-");
                // Guard: only transition if we haven't already moved to LoggedIn/CoolingDown
                if (_state is ConnectionLifecycleState.Connecting or ConnectionLifecycleState.LoggingIn)
                    TryTransition(
                        ConnectionLifecycleState.Disconnected,
                        isLoginRejected ? $"login rejected: {ex.Message}" : $"connect failed: {ex.Message}",
                        correlationId);
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task RequestDisconnectAsync(string reason, string? correlationId = null)
    {
        NotifyManualDisconnect();

        if (_state is not (ConnectionLifecycleState.LoggedIn
                        or ConnectionLifecycleState.Connecting
                        or ConnectionLifecycleState.LoggingIn
                        or ConnectionLifecycleState.CoolingDown))
        {
            // Already disconnecting/disconnected
            return;
        }

        TryTransition(ConnectionLifecycleState.Disconnecting, reason, correlationId);
        await _soulseek.DisconnectAsync();
    }

    public void NotifyManualDisconnect()
    {
        _manualDisconnect = true;
        CancelActiveConnectAttempt("manual disconnect");
    }

    // ── Soulseek event callbacks ──────────────────────────────────────────

    private void OnSoulseekStateChanged(SoulseekStateChangedEvent evt)
    {
        var raw = evt.State;

        // Map Soulseek raw flags string → lifecycle state
        if (raw.Contains("LoggedIn", StringComparison.OrdinalIgnoreCase))
        {
            var wasLoggedIn = _state == ConnectionLifecycleState.LoggedIn;
            TryTransition(ConnectionLifecycleState.LoggedIn, "soulseek reported LoggedIn");

            if (!wasLoggedIn)
            {
                // Successful (re)connect — reset counters
                _reconnectRetryCount = 0;
                _quickRetryCount     = 0;
            }
        }
        else if (raw.Contains("Disconnecting", StringComparison.OrdinalIgnoreCase))
        {
            TryTransition(
                ConnectionLifecycleState.Disconnecting,
                ComposeDisconnectReason("soulseek reported Disconnecting", consume: false));
        }
        else if (raw.Contains("Disconnected", StringComparison.OrdinalIgnoreCase))
        {
            var wasActive = _state is ConnectionLifecycleState.LoggedIn
                                   or ConnectionLifecycleState.LoggingIn
                                   or ConnectionLifecycleState.Connecting;

            TryTransition(
                ConnectionLifecycleState.Disconnected,
                ComposeDisconnectReason("soulseek reported Disconnected", consume: true));

            if (wasActive && AutoReconnectEnabled && !_manualDisconnect)
            {
                CancelActiveConnectAttempt("soulseek reported disconnected");
                StartAutoReconnectLoop(correlationId: null);
            }
        }
        else if (raw.Contains("Connecting", StringComparison.OrdinalIgnoreCase))
        {
            TryTransition(ConnectionLifecycleState.Connecting, "soulseek reported Connecting");
        }
        else if (raw.Contains("LoggingIn", StringComparison.OrdinalIgnoreCase)
              || (raw.Contains("Connected", StringComparison.OrdinalIgnoreCase)
                  && !raw.Contains("Disconnecting", StringComparison.OrdinalIgnoreCase)))
        {
            TryTransition(ConnectionLifecycleState.LoggingIn, "soulseek reported Connected/LoggingIn");
        }
        else if (!evt.IsConnected)
        {
            var wasActive = _state is ConnectionLifecycleState.LoggedIn
                                   or ConnectionLifecycleState.LoggingIn
                                   or ConnectionLifecycleState.Connecting;

            TryTransition(
                ConnectionLifecycleState.Disconnected,
                ComposeDisconnectReason("soulseek reported not connected", consume: true));

            if (wasActive && AutoReconnectEnabled && !_manualDisconnect)
            {
                CancelActiveConnectAttempt("soulseek reported not connected");
                StartAutoReconnectLoop(correlationId: null);
            }
        }
    }

    private void CancelActiveConnectAttempt(string reason)
    {
        var activeCts = Interlocked.Exchange(ref _activeConnectCts, null);
        if (activeCts == null)
            return;

        if (!activeCts.IsCancellationRequested)
        {
            _logger.LogInformation("Lifecycle: cancelling in-flight connect attempt. reason={Reason}", reason);
            activeCts.Cancel();
        }
    }

    private void OnSoulseekConnectionStatus(SoulseekConnectionStatusEvent evt)
    {
        if (string.Equals(evt.Status, "connected", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _lastDisconnectStatusReason, null);
            return;
        }

        if (string.Equals(evt.Status, "disconnecting", StringComparison.OrdinalIgnoreCase)
         || string.Equals(evt.Status, "disconnected", StringComparison.OrdinalIgnoreCase)
         || string.Equals(evt.Status, "kicked", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(evt.Reason))
            {
                Interlocked.Exchange(ref _lastDisconnectStatusReason, evt.Reason);
            }
        }

        if (!string.Equals(evt.Status, "kicked", StringComparison.OrdinalIgnoreCase))
            return;

        _coolingUntilUtc = DateTime.UtcNow.AddSeconds(KickCooldownSeconds);
        TryTransition(ConnectionLifecycleState.CoolingDown, "kicked from server");

        // Wake up after cooldown and restart reconnect loop if eligible
        _ = Task.Run(async () =>
        {
            var wait = _coolingUntilUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);

            if (_state == ConnectionLifecycleState.CoolingDown && !_manualDisconnect && AutoReconnectEnabled)
            {
                TryTransition(ConnectionLifecycleState.Disconnected, "cooldown expired");
                StartAutoReconnectLoop(correlationId: null);
            }
        });
    }

    private string ComposeDisconnectReason(string fallback, bool consume)
    {
        string? detail = consume
            ? Interlocked.Exchange(ref _lastDisconnectStatusReason, null)
            : Interlocked.CompareExchange(ref _lastDisconnectStatusReason, null, null);

        return string.IsNullOrWhiteSpace(detail) ? fallback : $"{fallback}: {detail}";
    }

    // ── auto-reconnect loop ───────────────────────────────────────────────

    private void StartAutoReconnectLoop(string? correlationId)
    {
        if (Interlocked.CompareExchange(ref _reconnectLoopActive, 1, 0) != 0)
            return; // loop already running

        _ = Task.Run(async () =>
        {
            try
            {
                while (_state != ConnectionLifecycleState.LoggedIn
                    && AutoReconnectEnabled
                    && !_manualDisconnect)
                {
                    // Wait out any active cooldown
                    if (_state == ConnectionLifecycleState.CoolingDown)
                    {
                        var remaining = _coolingUntilUtc - DateTime.UtcNow;
                        if (remaining > TimeSpan.Zero)
                        {
                            _logger.LogInformation(
                                "Lifecycle: cooldown active, waiting {Secs:F0}s before retry.",
                                remaining.TotalSeconds);
                            await Task.Delay(remaining);
                        }
                        continue;
                    }

                    _reconnectRetryCount++;
                    bool isQuick = _quickRetryCount < MaxQuickRetries;
                    int baseMs   = isQuick ? QuickRetryBaseMs : CalculateBackoffMs(_reconnectRetryCount);
                    int jitterMs = (int)(baseMs * JitterFraction * (_rng.NextDouble() * 2.0 - 1.0));
                    int delayMs  = Math.Max(1_000, baseMs + jitterMs);

                    if (isQuick) _quickRetryCount++;

                    _logger.LogInformation(
                        "Lifecycle: auto-reconnect #{Attempt} (quick={Quick}) scheduled in {Delay}ms. corr={Corr}",
                        _reconnectRetryCount, isQuick, delayMs, correlationId ?? "-");

                    await Task.Delay(delayMs);

                    if (_state == ConnectionLifecycleState.LoggedIn || !AutoReconnectEnabled || _manualDisconnect)
                        break;

                    await RequestConnectAsync(
                        _lastPassword,
                        correlationId ?? $"auto-reconnect-{_reconnectRetryCount}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Lifecycle: auto-reconnect loop encountered an error. reason={Reason} correlationId={CorrelationId}",
                    ex.Message,
                    correlationId ?? "-");
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectLoopActive, 0);
            }
        });
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private void TryTransition(
        ConnectionLifecycleState to,
        string reason,
        string? correlationId = null)
    {
        var from = _state;

        if (!ValidTransitions.Contains((from, to)))
        {
            _logger.LogDebug(
                "Lifecycle: skipping invalid transition {From} → {To} (reason={Reason}). corr={Corr}",
                from, to, reason, correlationId ?? "-");
            return;
        }

        _state = to;
        _logger.LogInformation(
            "Lifecycle: {From} → {To} | reason={Reason} corr={Corr}",
            from, to, reason, correlationId ?? "-");

        _eventBus.Publish(new ConnectionLifecycleStateChangedEvent(
            Previous:      from.ToString(),
            Current:       to.ToString(),
            Reason:        reason,
            CorrelationId: correlationId));
    }

    /// <summary>
    /// Exponential backoff kicking in after the quick-retry budget is exhausted.
    /// 5 s × 3^(effective_attempt−1), capped at 60 s.
    /// </summary>
    private static int CalculateBackoffMs(int attempt)
    {
        var effectiveAttempt = Math.Max(0, attempt - MaxQuickRetries - 1);
        var seconds = 5.0 * Math.Pow(3.0, effectiveAttempt);
        return (int)Math.Min(seconds, 60.0) * 1_000;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stateChangedSub.Dispose();
        _kickedSub.Dispose();
        CancelActiveConnectAttempt("lifecycle dispose");
        _commandLock.Dispose();
    }
}
