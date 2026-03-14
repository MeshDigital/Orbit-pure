# SLSKDONET Troubleshooting Guide

## 🎵 Audio Playback Issues

### Player Initialization Failed

**Symptoms**:
- Player sidebar shows "Player Initialization Failed"
- Console shows: `[AudioPlayerService] Initialization Failed`
- No audio playback when clicking tracks

**Causes**:
- LibVLC native libraries missing from output directory

**Solutions**:

1. **Check LibVLC folder**:
   ```powershell
   cd bin\Debug\net8.0-windows
   dir libvlc
   ```
   Should contain: `libvlc.dll`, `libvlccore.dll`, and `plugins/` folder

2. **Rebuild the project**:
   ```powershell
   dotnet clean
   dotnet build --configuration Debug
   ```

3. **Verify NuGet package**:
   - Check `SLSKDONET.csproj` includes `VideoLAN.LibVLC.Windows`
   - Run `dotnet restore` to re-download packages

### No Sound When Playing

**Symptoms**:
- Player shows track info but no audio
- Progress bar moves but no sound

**Solutions**:

1. **Check Windows volume mixer**:
   - Ensure SLSKDONET.exe is not muted
   - Check master volume level

2. **Verify file path**:
   - Console should show: `ResolvedFilePath: C:\path\to\file.mp3`
   - If empty, file wasn't downloaded or path not saved

3. **Check file format**:
   - Ensure file is a valid audio file
   - Try playing in Windows Media Player to verify

---

## 🖱️ Drag-and-Drop Issues

### Visual Cue Not Showing

**Symptoms**:
- Drag works but no visual feedback
- Console shows: `[DRAG] WARNING: Could not get AdornerLayer!`

**Causes**:
- AdornerLayer not available in visual tree

**Solutions**:

1. **Check console output**:
   ```
   [DRAG] MouseDown at X;Y
   [DRAG] Starting drag for: Artist - Title
   [DRAG] WARNING: Could not get AdornerLayer!
   ```

2. **Functionality still works**:
   - Drag-and-drop will still function
   - Only visual cue is missing
   - Track will be added to playlist

3. **Restart application**:
   - Sometimes resolves AdornerLayer issues
   - Check if issue persists

### Tracks Don't Appear After Drop

**Symptoms**:
- Track count increases but track list doesn't update
- Console shows successful add but UI doesn't refresh

**Solutions**:

1. **Check console for errors**:
   ```
   Successfully added track to playlist <name>
   Loading tracks for project: <name>
   Loaded X tracks for project <name>
   ```

2. **Click on another playlist and back**:
   - Forces UI refresh
   - Should show the added track

3. **Update to latest version**:
   - This issue was fixed in recent updates
   - Rebuild from latest code

---

## 💾 Database Issues

### Database Concurrency Exception

**Symptoms**:
- Console shows: `DbUpdateConcurrencyException`
- Error when adding tracks to playlists

**Solutions**:

1. **Update to latest version**:
   - This was fixed in recent updates
   - `SavePlaylistTrackAsync` now checks entity existence

2. **Clear database** (last resort):
   ```powershell
   del %AppData%\SLSKDONET\library.db
   ```
   **Warning**: This deletes all library data!

### Tracks Not Persisting

**Symptoms**:
- Tracks disappear after app restart
- Playlists empty on relaunch

**Solutions**:

1. **Check database file exists**:
   ```powershell
   dir %AppData%\SLSKDONET\library.db
   ```

2. **Check console for save errors**:
   - Look for `fail:` messages during operations

3. **Verify write permissions**:
   - Ensure app can write to `%AppData%\SLSKDONET\`

---

## 🌐 Connection Issues

### Cannot Connect to Soulseek

**Symptoms**:
- Login fails
- Status bar shows "Disconnected"
- Console shows connection errors

**Solutions**:

1. **Verify credentials**:
   - Check username/password in Settings
   - Test login on official Soulseek client

2. **Check server settings**:
   - Default: `server.slsknet.org:2242`
   - Verify in `config.ini`

3. **Firewall/Antivirus**:
   - Add exception for SLSKDONET.exe
   - Check Windows Firewall settings

4. **Network connectivity**:
   - Ensure internet connection is active
   - Try pinging `server.slsknet.org`

---

## ⬇️ Download Issues

### Downloads Stuck in "Searching"

**Symptoms**:
- Tracks stay in Searching state
- No progress after long time

**Solutions**:

1. **Check Soulseek connection**:
   - Ensure connected to network
   - Status bar should show "Connected"

2. **Verify search query**:
   - Check artist/title are correct
   - Try manual search to verify results exist

3. **Cancel and retry**:
   - Right-click → Cancel
   - Right-click → Hard Retry

### Downloads Fail Immediately

**Symptoms**:
- Track goes from Searching → Failed quickly
- Console shows error messages

**Solutions**:

1. **Check download directory**:
   - Ensure path exists and is writable
   - Check disk space available

2. **Review error message**:
   - Console shows specific failure reason
   - May be user offline, file removed, etc.

3. **Try different result**:
   - Search again and select different user
   - Some users may have restricted access

---

## 🖥️ Console Diagnostics

### Console Window Not Showing

**Symptoms**:
- No console window in Debug mode
- Can't see diagnostic output

**Solutions**:

1. **Verify Debug build**:
   ```powershell
   dotnet build --configuration Debug
   ```

2. **Check project file**:
   - `SLSKDONET.csproj` should have:
   ```xml
   <PropertyGroup Condition="'$(Configuration)'=='Debug'">
     <OutputType>Exe</OutputType>
   </PropertyGroup>
   ```

3. **Run from command line**:
   ```powershell
   cd bin\Debug\net8.0-windows
   .\SLSKDONET.exe
   ```

### Redirect Console to File

**To save console output**:
```powershell
.\SLSKDONET.exe > diagnostic_log.txt 2>&1
```

---

## 🔧 General Issues

### Application Won't Start

**Solutions**:

1. **Check .NET Runtime**:
   ```powershell
   dotnet --version
   ```
   Should be 8.0 or higher

2. **Rebuild from scratch**:
   ```powershell
   dotnet clean
   dotnet restore
   dotnet build
   ```

3. **Check for errors**:
   - Look for build errors in output
   - Ensure all NuGet packages restored

### UI Freezing

**Solutions**:

1. **Check for large operations**:
   - Importing huge playlists
   - Loading libraries with 10k+ tracks

2. **Reduce concurrent downloads**:
   - Settings → Max Concurrent Downloads → Lower value

3. **Restart application**:
   - Close and reopen
   - Check if issue persists

---

## 📝 Getting Help

### Collect Diagnostic Information

1. **Run in Debug mode** and capture console output
2. **Note exact error messages** from console
3. **Check database file** exists and size
4. **Note application version** from status bar

### Report Issues

Include in bug reports:
- Application version
- Console output (if applicable)
- Steps to reproduce
- Expected vs actual behavior
- Operating system version

---

**Last Updated**: December 13, 2024
