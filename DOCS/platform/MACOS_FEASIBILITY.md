# ORBIT on macOS and Linux: feasibility (issue #237)

Spike, 2026-11-14. Decision document only: no production behaviour changes. Measured on
`master` @ `34e585e` (after GA3), Windows 11 dev box plus GitHub-hosted runners.

## Measured: does it build?

`dotnet publish SLSKDONET.csproj -c Release -r <rid> --self-contained true`:

| RID | Result | Size (with models) | Blocker |
|---|---|---|---|
| `win-x64` | builds (the shipped target) | — | — |
| `linux-x64` | **builds, 0 errors** | 1.45 GB, 415 files | — |
| `osx-x64` (Intel) | **builds, 0 errors** | 1.0 GB, 414 files | — |
| `osx-arm64` (Apple Silicon) | **fails** | — | `Microsoft.ML` 3.0.1's build targets refuse anything but x86/x64 (`Microsoft.ML.targets(16,5)`) |

So the codebase *compiles* cross-platform (TFM is plain `net9.0`, Avalonia UI). The Windows-only
parts are runtime dependencies, listed below. "Does it start?" is measured by the release workflow's
smoke jobs (`.github/workflows/release.yml`: xvfb on Ubuntu, a headless launch on macOS-13) — see
the result section at the end.

## Inventory: Windows-only at runtime

### Audio output (blocks playback — the core of the app)
| Where | What | Replacement | Effort | Behind an interface? |
|---|---|---|---|---|
| `Services/Audio/AudioOutputProvider.cs:43,62,114,138,155,186,201` | `MMDeviceEnumerator`, `WasapiOut`, `AsioOut`, `WaveOutEvent` | miniaudio (via a binding) or PortAudio / OpenAL; CoreAudio on mac, PipeWire/PulseAudio on Linux | L | Partly: `AudioOutputProvider` is the seam; proposed `IAudioOutputBackend` |
| `Services/AudioPlayerService.cs:893` | `WasapiOut` fallback chain | same backend | M | via the backend above |
| `Services/Audio/LibraryPreviewPlayer.cs:178`, `TransitionPreviewPlayer.cs:129`, `RealTimeStemEngine.cs:63`, `ViewModels/DeckViewModel.cs:468` | their own `WasapiOut`/`WaveOutEvent` | route through `IAudioOutputBackend` | M | no — each news up NAudio directly |
| `Services/AudioIntegrityService.cs:205`, NAudio `AudioFileReader` | Media Foundation decode for FLAC/M4A/AIFF | FFmpeg decode (already the analysis path) | M | no |
| `Services/Audio/ExactSeek` | assumes Media Foundation's seek behaviour | FFmpeg-backed reader | M | yes (`ExactSeek`) |

### ML / analysis
| Where | What | Replacement | Effort |
|---|---|---|---|
| `SLSKDONET.csproj` `Microsoft.ML.OnnxRuntime.DirectML` 1.23.0; `DiscogsEffnetEmbeddingExtractor.cs:263`, `CueDetrService.cs:162`, `Separation/DemucsOnnxSeparator.cs:89`, `OnnxStemSeparator.cs:94` | DirectML execution provider | `Microsoft.ML.OnnxRuntime` (CPU) per RID, optional CoreML EP on mac | S–M (package per RID + guard the `AppendExecutionProvider_DML` calls) |
| `SLSKDONET.csproj` `Microsoft.ML` / `Microsoft.ML.LightGbm` 3.0.1 | blocks osx-arm64 builds | upgrade to an ML.NET with arm64 support, or drop LightGBM if only used offline | M (check usages first) |
| `Services/AudioAnalysis/EssentiaRunner.cs:184` | `essentia_streaming_extractor_music.exe` sidecar | Linux/mac Essentia builds exist; or rely on the in-process engine (already the default path) | M |
| `AudioIngestionPipeline.cs:304,316` | bundled `ffmpeg.exe`; FFmpeg auto-installer pins a Windows build by SHA-256 | system FFmpeg (`brew`, distro package) or per-OS pinned builds | S |
| `Enrichment/Providers/AcoustIdProvider.cs:188` | `fpcalc.exe` | already OS-aware (`fpcalc` elsewhere) | — |
| `Discovery/BeatportCatalogClient.cs:102` | `System32\curl.exe` | `HttpClient` or system curl | S |

### Shell, window, OS services
| Where | What | Replacement | Effort |
|---|---|---|---|
| `Views/Avalonia/MainWindow.axaml:23` | `TransparencyLevelHint="Mica, AcrylicBlur"` | Avalonia falls back automatically; verify visuals | S |
| `Services/Shell/WindowsTaskbarIntegration.cs`, `TrayIconController.cs` | taskbar progress, tray | Avalonia `TrayIcon` works cross-platform; taskbar progress → no-op | S |
| `Services/WindowsToastService.cs:70-79` | `Shell_NotifyIcon` P/Invoke | Avalonia notifications / `notify-send` / `UNUserNotificationCenter` | S |
| `Services/ProcessJobTracker.cs:80-86` | kernel32 job objects (kill children with the app) | `prctl(PR_SET_PDEATHSIG)` on Linux, process groups on mac | S |
| `Services/ConnectionLifecycleService.cs:8,117,700` | `Microsoft.Win32` power events (guarded by `OperatingSystem.IsWindows`) | already guarded; no sleep/wake handling elsewhere | — |
| `Services/ProtectedDataService.cs`, `Enrichment/MetadataCredentialStore.cs` | DPAPI (guarded) | Keychain / libsecret | M |
| `FileInteractionService.cs:117`, `DownloadCenterViewModel.cs:897`, `FeedbackDialogViewModel.cs:250` | `explorer.exe /select,` | `open -R` (mac), `xdg-open` dir (Linux) | S |

### Integrations, install and CI
| What | Notes | Effort |
|---|---|---|
| `Companions/RekordboxBridge` (SQLCipher helper) | Rekordbox exists on mac; master.db path differs (`~/Library/Pioneer/rekordbox`) | M |
| Installer: Inno Setup (`Tools/ORBIT.iss`) | mac: `.app` bundle + `.dmg`, notarization; Linux: AppImage or `.tar.gz` | M |
| Updater (`Services/Update/GitHubUpdateService.cs:36`) | installs the first `.exe` asset | per-OS asset selection | S |
| CI (`.github/workflows/ci.yml`) | `windows-latest` only | a `macos`/`ubuntu` build job | S |

### Signing and distribution (macOS)
- Apple Developer Program: USD 99/year, needed for a Developer ID certificate.
- Every native binary in the bundle (ONNX Runtime, SkiaSharp, FFmpeg) must be signed with the
  hardened runtime, and the `.app` must be notarised (`notarytool`) and stapled, or Gatekeeper
  blocks it ("damaged / can't be opened"). An unsigned `.tar.gz` runs only after
  `xattr -dr com.apple.quarantine`.
- Linux has no signing gate; AppImage is the friendliest single-file format.

## Proposed seams (not implemented)
- `IAudioOutputBackend`: open device, latency, start/stop, an `ISampleProvider` in. NAudio WASAPI/ASIO
  on Windows; miniaudio elsewhere. Every `new WasapiOut`/`WaveOutEvent` above goes through it.
- `IAudioDecoder`: FFmpeg-backed reader + `ExactSeek` on non-Windows (Media Foundation is Windows-only).
- `IPlatformShell`: reveal-in-folder, toasts, taskbar progress, child-process lifetime, secure storage.
- `IToolLocator`: FFmpeg / Essentia / fpcalc per OS (already half there via `OperatingSystem.IsWindows()`).

## Recommendation
**Defer a real port; ship the experimental builds as-is.**
- Total for a usable macOS/Linux ORBIT (playback, analysis, signing): roughly **6–9 weeks** of focused work,
  dominated by the audio backend (L) and the decode path (M); osx-arm64 additionally needs the ML.NET upgrade.
- Cheap now: the release workflow publishes `linux-x64` and `osx-x64` archives marked *experimental*
  whenever they start up on the runners. Nothing beyond start-up is tested there; **playback,
  previews and the GPU path can't work** until the seams below exist.

Suggested phase order if it becomes a "go":
1. CI build jobs for linux-x64/osx-x64 (keep them compiling).
2. `IPlatformShell` + `IToolLocator` + CPU ONNX per RID → library browser, search, downloads, analysis on CPU.
3. `IAudioOutputBackend` + `IAudioDecoder` → playback and previews.
4. ML.NET upgrade → osx-arm64.
5. `.app`/`.dmg` + notarisation; AppImage.

Follow-up issues (drafted here, **not created** until the owner approves): one per phase above.

## Result: does it start? (release workflow smoke jobs)
Measured on the `v0.4.0-beta.1` release runs (2026-11-14), published at
https://github.com/MeshDigital/Orbit-pure/releases/tag/v0.4.0-beta.1:

| Build | Runner | Result |
|---|---|---|
| `win-x64` (supported) | `windows-latest` | Smoke test passed (window up, stays up); Inno Setup installer + zip built |
| `linux-x64` (experimental) | `ubuntu-latest`, `xvfb-run` | **Starts and stays up** for the 60 s window: library, download center and the update check all initialise; 202 MB `.tar.gz` (no AI models) |
| `osx-x64` (experimental) | `macos-latest` (Apple Silicon) under **Rosetta 2** | **Starts and stays up**, same start-up path; 93 MB `.tar.gz` (no AI models, not notarised) |
| `osx-arm64` | — | not built (`Microsoft.ML` 3.0.1, see above) |

Found by the smoke logs, for the port backlog:
- The Intel `macos-13` runner image no longer gets runners; the job now publishes `osx-x64` on
  `macos-latest` and smoke-tests it under Rosetta.
- On macOS the update check reads the app's own version as `0.0.0.0` (it comes from the Windows file
  version), so the in-app updater would misjudge releases there. Read the assembly's informational
  version instead (S).
- Only start-up is tested. Playback can't work off Windows until `IAudioOutputBackend` exists, and
  the experimental archives ship without the AI models (the LFS bandwidth goes to the Windows build).