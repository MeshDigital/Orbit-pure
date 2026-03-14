---
description: How to use the Stem Separation Workspace
---

# Stem Workspace Workflow

Follow these steps to separate, mix, and manage your audio stems:

1. **Enter the Workspace**:
   - Navigate to the **Stems** tab in the main application sidebar (shortcut: `Alt+S`).

2. **Choose a Track**:
   - Browse your library in the **Track List** (left sidebar).
   - Click a track to load it into the workspace. The main view will display the original waveform.

3. **Separate into Stems**:
   - Click the **"Separate Track"** button in the top action bar.
   - The app will automatically use the **Native ONNX DirectML** engine for high-speed, 5-stem separation (Vocals, Drums, Bass, Piano, Other).
   - *Note: Separation typically takes 10-30 seconds depending on track length and GPU.*

4. **Preview Your Mix**:
   - Click the **"▶ Play"** button in the top bar to start real-time playback.
   - Adjust individual channel **Volume Sliders**, **Mute (M)**, or **Solo (S)** in real-time.
   - Changes are applied instantly to the audio output, allowing you to preview the result before committing.

5. **Save Your Project**:
   - Click **"Save Project"** in the top bar.
   - Enter a name for your edit (e.g., "Vocals Only Remix").
   - This saves your mixing choices (solo/mute/volume) and keeps the separated files for instant loading later.

6. **Load Saved Projects**:
   - Switch to the **"Saved Projects"** tab in the workspace sidebar to browse and resume your work.
