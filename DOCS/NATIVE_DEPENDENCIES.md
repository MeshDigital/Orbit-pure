# Native Dependencies Guide
**Updated**: January 21, 2026

This guide lists the native dependencies required for ORBIT (analysis, AI, and stem separation) and how to verify them.

---

## Required Components

1) **FFmpeg**
- Used for waveform extraction and technical analysis
- Minimum version: 6.x with `-hwaccel auto` support

2) **Essentia Models** (bundled in `Tools/Essentia/models/`)
- MusicNN / EffNet models for energy, mood, genre
- Arousal/Valence model for vibe telemetry
- Voice/Instrumental classifier

3) **ONNX Runtime**
- Used by ONNX stem separator and future DL models
- Windows: `onnxruntime.dll` and `onnxruntime.lib`

4) **TensorFlow Runtime**
- Used by `TensorFlowModelPool` and Sonic Match embeddings
- Native binaries provided via runtime packs (net9.0, platform specific)

---

## Locations
- **FFmpeg**: Expected on PATH (`ffmpeg.exe`). Optionally set `FFMPEG_PATH` in config.
- **Essentia Models**: `Tools/Essentia/models/` (also duplicated under `bin/Debug/.../Tools/Essentia/models/`).
- **Profiles**: `Tools/Essentia/profile.yaml` and tiered profiles for performance tuning.
- **ONNX Models**: `Tools/Essentia/models/spleeter-5stems.onnx` and supporting JSON files.

---

## Health Checks
Use the built-in service to verify native dependencies:
- `NativeDependencyHealthService` performs checks for FFmpeg, ONNX runtime, and TensorFlow runtime availability.
- Logs surface missing binaries, version mismatches, and GPU capability detection.

Manual verification:
```powershell
ffmpeg -version
```

---

## Installation (Windows)
1. Install FFmpeg:
   - Download static build from https://ffmpeg.org/download.html
   - Add `ffmpeg\bin` to your PATH
2. Restore NuGet packages to pull platform runtimes (done automatically by `dotnet restore`).
3. Ensure models are present:
   - `Tools/Essentia/models/` should contain `.pb`, `.json`, `.onnx` files

---

## Performance Tips
- Use GPU drivers up to date; ONNX Runtime can leverage DirectML where available.
- Keep `Tools/Essentia/profile_tier*.yaml` aligned with machine capabilities (Tier1 = fast, Tier3 = thorough).
- For low-spec machines, prefer Spleeter CLI separation over ONNX to reduce GPU load.

---

## Troubleshooting
- **FFmpeg not found**: Add to PATH or set full path in `appsettings.json`.
- **ONNX/TensorFlow load errors**: Ensure matching CPU/GPU runtime binaries for your architecture (win-x64 recommended).
- **Model missing**: Re-copy `Tools/Essentia/models/` from repository or build artifacts.
- **Slow analysis**: Switch to lower tier profile (Tier1), reduce concurrent workers.
