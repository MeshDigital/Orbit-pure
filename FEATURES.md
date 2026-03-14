# SLSKDONET Features

## 🎵 Audio Playback

### High-Fidelity Music Player
- **NAudio Integration**: High-performance audio engine with low-latency monitoring
- **Format Support**: MP3, FLAC, WAV, OGG, and more
- **Real-Time VU Meters**: Dual-channel peak monitoring for volume balancing
- **Waveform Seekbar**: Interactive waveform rendering (supports Rekordbox direct parsing)
- **Pitch/Tempo Control**: Adjust playback speed from 0.9x to 1.1x
- **Drag-and-Drop**: Drag tracks from Library to player sidebar
- **Double-Click Play**: Quick playback from track lists

### Stem Separation & Mixer
- **Real-Time Stem Engine**: ONNX and Spleeter backends for 2-5 stem splits
- **Stem Mixer**: Solo, mute, and gain per stem with synchronized transport
- **Waveform Overlays**: Per-stem waveforms and meter visualization
- **Live Routing**: Fast switching between hardware-friendly output profiles

### Visual Analysis
- **Automatic Rekordbox Probing**: Detects `.DAT/.EXT` companion files for instant waveforms
- **Beat Grid Support**: Displays original beat markers from analysis data
- **Hot Cue Visualization**: Renders Rekordbox hot cues on the waveform
- **Song Structure**: Phases (Intro/Chorus/Outro) visible in the player timeline

---

## 🎧 DJ Companion Workspace

*Professional mixing workspace with unified AI recommendations - inspired by MixinKey Pro*

### Unified Track Analysis
- **Real-Time Visualizations**: Album art, BPM/Key badge, Energy/Danceability bars
- **Waveform Display**: RMS envelope with color-coded cue points (Intro/Drop/Outro/Breakdown)
- **5 Stem Preview Buttons**: Isolate and preview individual instruments (Vocals, Drums, Bass, Keys, Other)
- **Live VU Meters**: Dual-channel peak monitoring during playback

### 4 Parallel Recommendation Engines
- **Harmonic Matches**: Key-based mixing compatibility using Camelot wheel (up to 12 matches)
  - Shows detected key, compatibility %, and relation type ("Perfect Match" / "Compatible")
- **Tempo Sync**: BPM ±6% beatmatching range (up to 12 matches)
  - Displays exact BPM and ±% difference for seamless beatmatching
- **Energy Flow**: Directional energy matching with visual indicators (↑ Rising / ↓ Dropping / → Stable)
  - Helps manage dancefloor energy arc (build momentum, maintain vibe, cool down)
- **Style Matches**: Genre-based track discovery (up to 8 matches)
  - Ready for ML-based classification via PersonalClassifierService

### Dynamic Mixing Advice
- **Context-Aware Tips**: 5+ auto-generated recommendations based on track characteristics
- **Tempo Strategy**: Recommended BPM range for smooth transitions
- **Harmonic Guidance**: Compatible key clusters and avoidance zones
- **Energy Insights**: Danceability assessment and energy management tips
- **Structural Tips**: Drop detection, phrase timing, and build-up opportunities

### Performance
- **Parallel Async Loading**: All 4 recommendation engines load concurrently (~200ms on 10k track library)
- **Responsive UI**: Background task orchestration prevents UI freezes
- **Large Library Support**: Optimized for 50,000+ track libraries

---

## 📚 Library Management

### Playlist Organization
- **Create Playlists**: Organize tracks into custom playlists
- **Drag-and-Drop**: Move tracks between playlists visually (Powered by Simple-Avalonia-DragnDrop-Service)
- **Track Reordering**: Drag to reorder tracks within playlists
- **Playlist Deletion**: Remove playlists with confirmation
- **Track Removal**: Remove individual tracks from playlists

### Library Views
- **All Tracks**: View all downloaded tracks across playlists
- **Per-Playlist**: View tracks in specific playlists
- **Filter Options**: Filter by status (All, Downloaded, Pending)
- **Search**: Search tracks within current view
- **Sort Options**: Sort by artist, title, status, date added
- **Column Customization**: Show/hide, resize, and reorder columns (Status, Artist, Title, Duration, BPM, Key, Bitrate, Album, etc.)
- **Persistent Layout**: Column configuration auto-saves to `%APPDATA%/ORBIT/column_config.json`

### Smart Playlists & Smart Crates
- **Rule Builder**: Create vibe-driven playlists with BPM, energy, mood, and integrity rules
- **Crate Templates**: Save reusable rule sets for rapid playlist generation
- **Live Preview**: See matching track counts and example results before saving
- **Auto-Refresh**: Crates update as new tracks meet criteria

### Library Sources & Bulk Operations
- **Folder Management**: Enable/disable library folders, track scan history, and counts
- **Bulk Actions**: Run multi-track operations with progress modal and cancellation
- **Virtualized Lists**: Optimized scrolling for 50k+ tracks with minimal memory use

### Persistence
- **SQLite Database**: All data persisted locally
- **Automatic Saves**: Changes saved immediately
- **Activity Logging**: Track additions/removals logged
- **Crash Recovery**: Library state survives app restarts

---

## 📥 Import System

### Spotify & Deep Enrichment
- **Playlist Import**: Import public Spotify playlists by URL
- **Track Extraction**: Automatic artist/title extraction
- **Background Enrichment**: Automatic BPM, Key, Energy, Valence, and Danceability tagging
- **Batch Processing**: Unified feature fetching (batches of 100) for API efficiency
- **Duration Validation**: Uses Spotify's canonical duration to verify file versions
- **Liked Songs Support**: Import your entire Spotify library

### CSV Import
- **File Support**: Import from CSV files
- **Auto-Detection**: Automatic column detection
- **Flexible Format**: Supports various CSV structures
- **Preview**: Preview tracks before import

### Manual Import
- **Direct Entry**: Add tracks manually via search
- **Quick Add**: Simple artist - title format
- **Bulk Entry**: Add multiple tracks at once

---

## 🧪 The Style Lab (Sonic Taxonomy)
*Phase 15.5 Feature*

### Personalized AI
- **Trainable Styles**: Define a genre (e.g., "Liquid DnB") by dragging example tracks into a bucket.
- **Local Learning**: The AI trains on *your* computer using ML.NET/LightGBM. No cloud data.
- **Auto-Classification**: New tracks are automatically scanned and assigned a style if they match high confidence.

### Visualizations
- **Spectrogram Stack**: Visual breakdown of frequency content.
- **Prediction Radar**: See exactly how "sure" the AI is about a track's style (e.g., "80% Techno, 20% House").

## 🧠 Intelligence & Discovery

### Intelligence Center
- **Sonic Match**: AI similarity engine using TensorFlow and Essentia embeddings
- **Confidence Telemetry**: Radar and cards showing match strength and mood
- **Diagnostics Mode**: Inspect model inputs, outputs, and hardware capabilities

### Search Diagnostics
- **Search Rejection UI**: Clear rejection reasons with confidence and match score
- **Forensic Tooltips**: Inline insights for why a result was accepted or rejected

---

## ⬇️ Download Management

### Queue System
- **Concurrent Downloads**: Multiple simultaneous downloads (configurable)
- **Progress Tracking**: Real-time progress for each track
- **Speed Display**: Current download speed shown
- **State Management**: Pending → Searching → Downloading → Completed

### Download Controls
- **Start/Pause**: Control individual downloads
- **Cancel**: Cancel downloads with cleanup
- **Hard Retry**: Delete partial files and retry
- **Resume**: Resume paused downloads

### Smart Features
- **Auto-Retry**: Automatic retry on failure
- **Timeout Handling**: Intelligent timeout detection
- **File Validation**: Check file integrity after download
- **Duplicate Detection**: Avoid downloading duplicates

## 📤 Export & DJ Hardware
- **Rekordbox/USB Export**: Hardware-ready exports with cue and tag mapping
- **Cue Templates**: Genre-aware cue layouts (Drops, Builds, Outros)
- **Serato/Universal Cues**: Writes Serato markers and universal cue formats

---

## 🎨 User Interface

### Modern Design
- **Dark Theme**: Easy on the eyes, Windows 11 style
- **Clean Layout**: Intuitive navigation
- **Responsive**: No UI freezes during operations
- **Animations**: Smooth transitions and feedback

### Navigation
- **Search Page**: Find and queue tracks
- **Library Page**: Manage playlists and play music
- **Downloads Page**: Monitor active downloads
- **Settings Page**: Configure application
- **History Page**: View import history

### Visual Feedback
- **Drag Adorners**: Visual feedback during drag operations
- **Progress Bars**: Download and playback progress
- **Status Icons**: Track state indicators
- **Tooltips**: Helpful hover information

---

## 🔧 Configuration

### Soulseek Settings
- Username and password (encrypted storage)
- Server and port configuration
- Connection timeout settings

### Download Settings
- Download directory selection
- Max concurrent downloads (1-10)
- Filename format template
- Preferred audio formats

### UI Settings
- Player sidebar visibility
- Active downloads panel toggle
- Filter preferences
- View modes (grid/list)

---

## 🐛 Diagnostics

### Console Output (Debug Mode)
- **Detailed Logging**: All operations logged to console
- **Drag Events**: `[DRAG]` prefixed messages
- **Playback Events**: `[PLAYBACK]` prefixed messages
- **Service Logs**: `info:`, `warn:`, `fail:` messages
- **No Visual Studio Required**: Works standalone

### UI Diagnostics
- **Version Display**: Application version in status bar
- **Connection Status**: Real-time connection state
- **Initialization Checks**: Player and service status
- **Error Messages**: Clear user-facing error messages

### Troubleshooting Tools
- Console log redirection to file
- Database diagnostic queries
- LibVLC initialization checks
- Drag-and-drop event tracing

---

## 🔒 Security & Privacy

### Data Protection
- **Password Encryption**: Windows DPAPI for credentials
- **Local Storage**: All data stored locally
- **No Telemetry**: No data sent to external servers
- **Secure Connections**: SSL/TLS for Soulseek network

### File Safety
- **Sandboxed Downloads**: Downloads to configured directory only
- **Filename Sanitization**: Prevents path traversal attacks
- **Virus Scanning**: Compatible with Windows Defender
- **Metadata Privacy**: No personal data in database

---

## 🚀 Performance

### Optimizations
- **Async Operations**: Non-blocking UI
- **Database Indexing**: Fast queries on large libraries
- **Lazy Loading**: Load data on demand
- **Memory Management**: Efficient collection handling

### Scalability
- Handles 10,000+ track libraries
- Supports hundreds of playlists
- Efficient search with large result sets
- Fast drag-and-drop even with many tracks

---

## 🔌 Extensibility

### Plugin Points
- **Import Providers**: Add new import sources
- **Metadata Services**: Custom metadata fetching
- **Audio Backends**: Alternative player implementations
- **UI Themes**: Custom styling support

### Developer Features
- Dependency injection container
- MVVM architecture
- Event-driven design
- Comprehensive logging

---

**Version**: 1.0.0
