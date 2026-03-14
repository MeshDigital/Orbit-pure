# 🏗️ ORBIT VS Code Workspace Setup Guide

## 📋 Quick Start

### 1. Open the Multi-Root Workspace
```bash
code ORBIT.code-workspace
```

This will load the optimized 6-panel forensic layout with dedicated roots for:
- 🚀 **ORBIT Root** - Top-level project files
- 🧠 **Core: Models & Entities** - Data layer
- ⚙️ **Intelligence: Services & Analysis** - Business logic
- 🎨 **UI: ViewModels** - Presentation layer
- 🖥️ **Views: Avalonia Controls** - XAML/AXAML files
- 📊 **Forensics: Build Logs** - Error tracking timeline

---

## 🛠️ Essential Extensions

The workspace will prompt you to install recommended extensions:

### Core Development
- **C# Dev Kit** (`ms-dotnettools.csdevkit`) - Solution management, IntelliSense
- **C# Extension** (`ms-dotnettools.csharp`) - Roslyn analyzer integration
- **Avalonia for VS Code** (`avaloniateam.vscode-avalonia`) - XAML previewer

### Version Control
- **GitLens** (`eamodio.gitlens`) - Advanced Git visualization
- **GitHub Actions** (`github.vscode-github-actions`) - CI/CD integration

### Quality & Productivity
- **Code Spell Checker** (`streetsidesoftware.code-spell-checker`)
- **EditorConfig** (`editorconfig.editorconfig`)
- **TODO Tree** (`gruntfuggly.todo-tree`) - Task tracking

---

## 🎨 The "Cockpit" Layout

### Terminal Configuration (3 Persistent Lanes)

Open 3 terminal panels side-by-side:

1. **Build Lane** (Left)
   ```powershell
   dotnet watch run
   ```
   Continuous hot-reload feedback

2. **Git Lane** (Center)
   ```powershell
   git status --short
   ```
   Forensic commit logging

3. **Log Lane** (Right)
   ```powershell
   Get-Content logs\log*.json -Wait -Tail 20
   ```
   Real-time AI diagnostics

### Keyboard Shortcuts
- `Ctrl+Shift+B` - Build (default task)
- `F5` - Launch ORBIT (Debug)
- `Ctrl+F5` - Launch ORBIT (No Debug)
- `Ctrl+Shift+P` → "Tasks: Run Task" - Access custom tasks

---

## 🚀 Build & Run Tasks

### Available Tasks (Access via `Ctrl+Shift+P` → "Tasks: Run Task")

#### Build Tasks
- **build** - Standard debug build (default: `Ctrl+Shift+B`)
- **build-release** - Optimized release build
- **clean** - Remove bin/obj folders
- **rebuild** - Clean + Build sequence

#### Run Tasks
- **run** - Execute ORBIT
- **watch** - Hot-reload development mode

#### Forensic Tasks
- **📊 Document Recent Changes** - Extract last 100 build log lines
- **🧹 Clean All Build Artifacts** - Deep clean + dotnet clean
- **🔍 Analyze Solution** - Run Roslyn analyzers
- **📝 Generate Build Report** - Timestamped build output

#### Diagnostic Tasks
- **🩺 Check Avalonia Version** - Verify UI framework version
- **🩺 Verify .NET Runtime** - Display installed runtimes

---

## 🔬 Debug Configurations

### Available Launch Configurations (F5 Menu)

1. **🚀 Launch ORBIT (Debug)**
   - Full debugging with JIT suppression
   - Breakpoints, watches, call stack

2. **🚀 Launch ORBIT (Release)**
   - Production-mode testing
   - Performance profiling

3. **🔬 Attach to ORBIT Process**
   - Live debugging of running instance

4. **🎵 Launch DJ Companion (Isolated)**
   - Direct page navigation for testing
   - Env: `ORBIT_START_PAGE=DJCompanion`

5. **🧪 Launch with Hot Reload**
   - Avalonia XAML hot-reload enabled

---

## 📊 Forensic Workflow

### Build Error Timeline Management

1. **Generate Build Report**
   ```powershell
   # Via Task: 📝 Generate Build Report
   # Output: build_report_20260206_143022.txt
   ```

2. **Document Recent Changes**
   ```powershell
   # Via Task: 📊 Document Recent Changes
   # Aggregates: build_*.txt, build_*.md → RECENT_CHANGES.txt
   ```

3. **Search Forensic Keywords**
   - `Ctrl+Shift+F` (Global Search)
   - Search for: "WorkspaceTrackSelectedEvent", "VocalPocketSegment", etc.
   - Scope: All folders or specific root

---

## 🎯 Verification Checklist

### Post-Setup Validation

- [ ] **Workspace loads with 6 roots visible**
  - Check Explorer sidebar shows emoji-prefixed folders

- [ ] **IntelliSense works for C#**
  - Open `ViewModels/UserWorkspaceViewModel.cs`
  - Type `_eventBus.` → Should show autocomplete

- [ ] **Avalonia XAML previewer functional**
  - Open `Views/Avalonia/Controls/WaveformControl.axaml`
  - Right-click → "Avalonia Preview"

- [ ] **Debug launch works**
  - Press `F5` → ORBIT window should appear

- [ ] **Hot reload functional**
  - Run task: **watch**
  - Edit `.axaml` file → Should auto-refresh

- [ ] **GlobalTrackSelection event visible in debug console**
  - Launch with debugger
  - Navigate to Library → Select track
  - Check Debug Console for: "WorkspaceTrackSelectedEvent"

---

## ⚙️ Customization

### Sidebar Position
Default: Right-side (prevents code jumping)
```json
"workbench.sideBar.location": "right"
```

### Font Ligatures
```json
"editor.fontFamily": "'Cascadia Code', 'Fira Code', Consolas",
"editor.fontLigatures": true
```

### Semantic Highlighting
```json
"editor.semanticHighlighting.enabled": true
```

---

## 🛰️ Global Event Bus Testing

### Verify Event Propagation

1. Set breakpoint in `UserWorkspaceViewModel.cs` → `OnWorkspaceTrackSelected()`
2. Launch with `F5`
3. Click track in Library
4. Debugger should break at method entry
5. Inspect `evt.Track` parameter

---

## 📝 Next Steps

1. **Review Health Bar Styling**
   - File: `Views/Avalonia/Controls/SetlistHealthBar.axaml`
   - Customize colors for Critical/Warning/Healthy segments

2. **Configure Waveform Control**
   - File: `Views/Avalonia/Controls/WaveformControl.cs`
   - Subscribe to `WaveformDataLoadedEvent`

3. **Test Stress-Test Flow**
   - Debug → DJ Companion page
   - Load setlist
   - Run stress-test
   - Verify Inspector updates automatically

---

## 🚨 Troubleshooting

### "No .NET SDK Found"
```bash
dotnet --info
# Install .NET 8 SDK if missing
```

### "Avalonia Previewer Not Working"
```bash
dotnet tool restore
dotnet build
# Restart VS Code
```

### "GitLens Not Showing Blame"
```json
// settings.json
"gitlens.currentLine.enabled": true
```

---

**Built for DJs who code** | **February 2026**
