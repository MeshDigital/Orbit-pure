# Soulseek Login & Service Signals — Technical Reference

## Purpose
This document is the current implementation reference for ORBIT’s Soulseek connection/login subsystem.

It focuses on:
- Login orchestration and reconnect behavior.
- Service boundaries and ownership.
- EventBus signal contracts used by UI and orchestration.
- Operational log signatures for diagnosis.

This is intentionally implementation-first (code-accurate), not product-marketing documentation.

---

## Scope and Boundaries

### In scope
- Connection lifecycle state machine and transitions.
- Adapter connect/disconnect internals.
- Credential load/save path used by connection flows.
- UI-facing status mapping in `ConnectionViewModel`, `SettingsViewModel`, and `MainViewModel`.
- Core EventBus signals for connection state and reason propagation.

### Out of scope
- Download scoring and candidate ranking details.
- Full search policy architecture (covered elsewhere).
- Spotify auth and non-Soulseek external auth.

---

## Runtime Service Topology (Login Path)

### Dependency Injection Registration
Registered in `App.ConfigureSharedServices(...)`:
- `ISoulseekAdapter` -> singleton `SoulseekAdapter`
- `IConnectionLifecycleService` -> singleton `ConnectionLifecycleService`
- `ISoulseekCredentialService` -> singleton `SoulseekCredentialService`
- `IEventBus` -> singleton `EventBusService`
- `INetworkHealthService` -> singleton `NetworkHealthService`
- `ConnectionViewModel`, `SettingsViewModel`, `MainViewModel` -> singleton consumers

### Ownership model
- `ConnectionLifecycleService` is the **single authority** for connect/disconnect intent and auto-reconnect policy.
- `SoulseekAdapter` is the **transport + protocol integration** layer over Soulseek.NET.
- ViewModels must not implement their own reconnect loops; they consume lifecycle events.

---

## Core Services and Responsibilities

### `IConnectionLifecycleService` / `ConnectionLifecycleService`
Primary responsibilities:
- Maintains authoritative state (`Disconnected`, `Connecting`, `LoggingIn`, `LoggedIn`, `CoolingDown`, `Disconnecting`).
- Serializes commands with `_commandLock` (at-most-one active connect command).
- Owns reconnect policy:
  - quick retries (`MaxQuickRetries = 3`)
  - jittered delays (`JitterFraction = ±20%`)
  - kick cooldown (`KickCooldownSeconds = 60`)
- Consumes adapter-side event bus signals and emits lifecycle transitions.

Important implementation details:
- Tracks `_activeConnectCts`; cancels stale in-flight connect attempts when manual/unplanned disconnect arrives.
- Distinguishes caller cancellation (`connect cancelled`) from internal interruption (`connect interrupted`).
- Tracks `_lastDisconnectStatusReason` and composes reason-rich disconnect transitions.
- Prevents reconnect loop reentry with `_reconnectLoopActive` compare-exchange gate.

### `ISoulseekAdapter` / `SoulseekAdapter`
Primary responsibilities:
- Creates/configures Soulseek client and performs transport connect/disconnect.
- Emits transport-level connection signals.
- Provides readiness gate used by search (`WaitForReadyClientAsync`).
- Applies runtime network config patches (`ReconfigureOptionsAsync`) when safe.

Important implementation details:
- Connect timeout floor: `>= 60_000 ms`
- Message timeout floor: `>= 120_000 ms`
- Listen port clamped: `1024..65535`
- Guards against stale client events (`ReferenceEquals(sender, _client)`) to prevent old/disposed client state pollution.
- Tracks `_pendingDisconnectReason` and publishes reason-bearing status events:
  - `disconnecting`
  - `disconnected`
  - `kicked`
- Publishes connected status event on successful `ConnectAsync`.

### `ISoulseekCredentialService` / `SoulseekCredentialService`
Primary responsibilities:
- Stores/loads Soulseek credentials.
- On Windows, uses `ProtectedData` (`DataProtectionScope.CurrentUser`) to encrypt local credentials.

Storage location:
- `%LocalAppData%\ORBIT\slsk_creds.dat`

### `ConnectionViewModel`
Primary responsibilities:
- User-facing login overlay + status text.
- Calls lifecycle service (`RequestConnectAsync`, `NotifyManualDisconnect`, `RequestDisconnectAsync`).
- Subscribes to `ConnectionLifecycleStateChangedEvent` and maps states/reasons into UX-safe strings.

### `SettingsViewModel`
Primary responsibilities:
- Settings-panel connect/disconnect/reconnect actions.
- Uses stored credentials for connect action.
- Subscribes to lifecycle state changes and updates connection badges and button enablement.

### `MainViewModel`
Primary responsibilities:
- High-level app status indicator updates from lifecycle events (`Ready`, `Connecting...`, etc.).

### `INetworkHealthService`
Connection-adjacent responsibilities:
- Records connection state changes.
- Records kicks and connection failures.
- Receives search telemetry and filtering stats (used for operational diagnostics).

---

## Login and Reconnect Execution Flows

### Flow A — Manual login (overlay)
1. User enters credentials and triggers `ConnectionViewModel.LoginAsync(...)`.
2. ViewModel persists user config fields and calls `RequestConnectAsync(password)`.
3. Lifecycle transitions to `Connecting` and invokes adapter `ConnectAsync(...)`.
4. Adapter creates/recycles Soulseek client, subscribes handlers, and calls `client.ConnectAsync(...)`.
5. Adapter publishes:
   - `SoulseekStateChangedEvent` as state flags evolve (`Connecting`, `Connected, LoggingIn`, `Connected, LoggedIn`).
   - `SoulseekConnectionStatusEvent("connected", username)` when connect call completes.
6. Lifecycle consumes state events and transitions toward `LoggedIn`.
7. UI consumes `ConnectionLifecycleStateChangedEvent`; overlay closes on `LoggedIn`.

### Flow B — Auto-connect on startup
1. `ConnectionViewModel` checks `AutoConnectEnabled` and loads stored credentials.
2. If password exists, calls same lifecycle path as manual login.
3. If missing credentials, login overlay remains visible.

### Flow C — Manual disconnect
1. UI calls `NotifyManualDisconnect()` then `RequestDisconnectAsync("manual ...")`.
2. Lifecycle sets manual flag and cancels active connect CTS.
3. Lifecycle transitions to `Disconnecting` and invokes adapter disconnect.
4. Adapter marks pending reason and triggers client disconnect.
5. Adapter emits `disconnecting`/`disconnected` status; lifecycle consumes and transitions to `Disconnected`.
6. Auto-reconnect is suppressed by manual disconnect flag.

### Flow D — Unplanned disconnect
1. Adapter receives state change to `Disconnecting`/`Disconnected`.
2. Adapter publishes reason-bearing status event with fallback text if no explicit reason exists.
3. Lifecycle composes reason into transition (`soulseek reported Disconnected: ...`).
4. If prior state was active and `AutoReconnectEnabled && !_manualDisconnect`, lifecycle cancels active connect and starts reconnect loop.

### Flow E — Server kick path
1. Adapter receives `KickedFromServer` event.
2. Adapter publishes `SoulseekConnectionStatusEvent("kicked", ..., "kicked from server")`.
3. Lifecycle transitions to `CoolingDown`, waits 60s, then transitions to `Disconnected` and starts reconnect loop (if eligible).

---

## Lifecycle State Machine

Valid transitions enforced in `ConnectionLifecycleService.ValidTransitions`:
- `Disconnected -> Connecting`
- `Connecting -> LoggingIn | LoggedIn | Disconnected | CoolingDown`
- `LoggingIn -> LoggedIn | Disconnected | CoolingDown`
- `LoggedIn -> Disconnecting | CoolingDown | Disconnected`
- `Disconnecting -> Disconnected`
- `CoolingDown -> Connecting | Disconnected`

Invalid transitions are ignored and logged at debug level.

### Transition reason strategy
Lifecycle transitions always carry a `Reason` string and optional `CorrelationId`.

Reason examples:
- `connect requested`
- `connect failed: ...`
- `login rejected: ...`
- `soulseek reported LoggedIn`
- `soulseek reported Disconnected: unplanned disconnected while previous=...`
- `kicked from server`

---

## Signal Contracts (EventBus)

### 1) `SoulseekStateChangedEvent`
Contract:
- `State` (raw Soulseek flags as string)
- `IsConnected` (bool)

Publisher:
- `SoulseekAdapter` (inside `client.StateChanged` handler)

Primary consumers:
- `ConnectionLifecycleService` (`OnSoulseekStateChanged`)

Semantics:
- High-frequency transport state feed.
- Drives lifecycle progression to `Connecting`, `LoggingIn`, `LoggedIn`, `Disconnecting`, `Disconnected`.

### 2) `SoulseekConnectionStatusEvent`
Contract:
- `Status` (string): expected values include `connected`, `disconnecting`, `disconnected`, `kicked`
- `Username` (string)
- `Reason` (optional string)

Publishers:
- `SoulseekAdapter` on connect success, disconnect transitions, and server kick.

Primary consumer:
- `ConnectionLifecycleService` (`OnSoulseekConnectionStatus`)

Semantics:
- Lower-frequency, semantic status signal.
- Carries disconnect/kick context used to enrich lifecycle reason strings.

### 3) `ConnectionLifecycleStateChangedEvent`
Contract:
- `Previous`
- `Current`
- `Reason`
- `CorrelationId` (optional)

Publisher:
- `ConnectionLifecycleService` (`TryTransition`)

Consumers:
- `ConnectionViewModel`
- `SettingsViewModel`
- `MainViewModel`
- Any additional subscribers needing authoritative connection state.

Semantics:
- Canonical app-level connection state signal.
- UI should prefer this over raw adapter state/status.

### 4) Related operational signals
- `ShareHealthUpdatedEvent` (sharing readiness and status)
- `SharedFilesStatusEvent` (published share folders/count context)
- `SearchPressureStatusEvent` (load-shedding state, not login-specific but operationally relevant)

---

## UI Signal Mapping

### `ConnectionViewModel` mapping
- `LoggedIn` -> `IsConnected=true`, `StatusText="Connected as ..."`, hide overlay.
- `Connecting` -> `StatusText="Connecting..."`, spinner on.
- `LoggingIn` -> `StatusText="Logging in..."`.
- `CoolingDown` -> `StatusText="Disconnected by server — cooling down before reconnect..."`.
- `Disconnecting` -> `StatusText="Disconnecting..."`.
- `Disconnected` -> friendly reason mapping:
  - `login rejected: ...` -> `Sign-in failed: ...` (overlay shown)
  - `connect failed: ...` -> `Connection failed: ...`
  - default -> `Disconnected`

### `SettingsViewModel` mapping
- Similar lifecycle-to-text mapping for settings panel connection card.
- Uses stored credentials for connect actions; does not own reconnect policy.

### `MainViewModel` mapping
- Updates top-level status text (`Ready`, `Connecting...`, `Logging in...`, cooldown messaging, `Connection failed`).

---

## Log Signatures and What They Mean

Common lifecycle signatures:
- `Lifecycle: {From} → {To} | reason=... corr=...`
  - Canonical transition trace.
- `Lifecycle: auto-reconnect #... scheduled in ...ms`
  - Reconnect cadence confirmation.
- `Lifecycle: connect request rejected — CoolingDown (...s remaining)`
  - Connect suppressed by cooldown policy.

Common adapter signatures:
- `Soulseek client configured: minorVersion=..., messageTimeout=...`
  - Effective runtime options at connect attempt.
- `Soulseek state change: ... (was ...)`
  - Raw transport progression.
- `Ignoring Soulseek state change from stale client instance: ...`
  - Old disposed client event safely dropped.
- `[DISCONNECT] Executing Soulseek disconnect for reason '...'`
  - Disconnect intent reaching adapter.

Readiness/search gating signatures:
- `Waiting for Soulseek login... (State: ..., Elapsed: ...s...)`
- `Soulseek not logged in yet after readiness wait...`
- `Search skipped ... because Soulseek client is not ready.`

---

## Reliability and Safety Behaviors

- At-most-one active connect command via lifecycle `_commandLock`.
- In-flight connect cancellation on manual/unplanned disconnect to avoid stale wait lockouts.
- Reconnect loop single-instance guard (`_reconnectLoopActive`).
- Kick cooldown hard-stop before any reconnect attempt.
- Stale client event suppression in adapter handlers.
- Disconnect reason propagation from adapter status to lifecycle transition reason.

---

## Troubleshooting Runbook

### Symptom: Repeated `LoggingIn -> Disconnected`
Check:
1. Adapter logs for timeout and state progression.
2. Lifecycle transition reasons (`Disconnected` reason text should now include detail).
3. Whether disconnect reason contains server kick, transport close, or generic unplanned fallback.

### Symptom: Reconnect appears delayed
Check:
1. Cooldown active (`CoolingDown`).
2. Reconnect schedule logs (`auto-reconnect ... scheduled in ...ms`).
3. Whether an in-flight connect was cancelled (expected informational log).

### Symptom: UI says disconnected but no clear reason
Check:
1. Presence of `SoulseekConnectionStatusEvent` reason in logs.
2. Lifecycle transition reason composition.
3. If fallback reason appears (`unplanned ... while previous=...`), classify by surrounding adapter/network logs.

### Symptom: Settings connect button does nothing
Check:
1. Stored credentials exist (`ISoulseekCredentialService.LoadCredentialsAsync`).
2. Status text in Settings (`No stored credentials — use the Sign In overlay...`).

---

## Known Current Limitations

- Random/unplanned disconnects can still occur; current hardening improves recovery speed and diagnostic attribution, not complete elimination.
- Status values in `SoulseekConnectionStatusEvent.Status` are string-based; typo-safe enum normalization is a future improvement opportunity.
- Correlation IDs are available in lifecycle events but are not yet propagated uniformly to every adapter-originated signal.

---

## Change Checklist (When Modifying Login Stack)

If you change lifecycle or adapter behavior, update all of:
1. `ConnectionLifecycleService` transition/retry/cooldown logic.
2. Event contracts in `Models/Events.cs` if payloads change.
3. ViewModel mapping (`ConnectionViewModel`, `SettingsViewModel`, `MainViewModel`).
4. Tests:
   - `Tests/SLSKDONET.Tests/Services/ConnectionLifecycleServiceTests.cs`
   - `Tests/SLSKDONET.Tests/ViewModels/ConnectionViewModelTests.cs`
5. Changelog and this document.

---

## Reference Files

- `Services/IConnectionLifecycleService.cs`
- `Services/ConnectionLifecycleService.cs`
- `Services/ISoulseekAdapter.cs`
- `Services/SoulseekAdapter.cs`
- `Services/SoulseekCredentialService.cs`
- `Models/Events.cs`
- `ViewModels/ConnectionViewModel.cs`
- `ViewModels/SettingsViewModel.cs`
- `Views/MainViewModel.cs`
- `App.axaml.cs`
