# Learning from slsk-batchdl

## Key Features to Implement

### 1. Multiple Download Modes
- **Normal Mode**: Single file per input (currently have basic version)
- **Album Mode**: Download entire folder/album (-a flag)
- **Aggregate Mode**: Find all distinct songs/albums by artist (-g flag)
- **Album Aggregate**: Combine both modes for artists' albums

**Implementation Plan**:
- Add `DownloadMode` enum to Models
- Extend `DownloadManager` to handle folder-based operations
- Create `AlbumSearchService` for grouping results

### 2. Multiple Input Sources
- CSV files with track metadata
- Spotify playlists (needs OAuth)
- YouTube playlists (yt-dlp integration)
- Bandcamp URLs
- Direct search strings
- List files with custom conditions

**Implementation Plan**:
- Create `IInputSource` interface
- Implement parsers: `CsvInputSource`, `SpotifyInputSource`, `YoutubeInputSource`
- Add input type detection
- Create `InputParser` service

### 3. Advanced File Filtering
- **Required Conditions**: Strict filters (format, bitrate, length)
- **Preferred Conditions**: Ranked preferences (default: mp3, 200-2500kbps)
- Smart ranking that prefers files meeting more conditions
- Strict title/artist/album matching in paths

**Implementation Plan**:
- Extend `AppConfig` with condition classes
- Create `FileConditionEvaluator` service
- Implement ranking algorithm
- Add `IFileCondition` interface for extensibility

### 4. Flexible Name Formatting
- Variables: {artist}, {title}, {album}, {filename}, {bitrate}, etc.
- Conditional expressions: {artist|filename} (fallback if null)
- Subdirectory support: {artist}/{album}/{title}
- Metadata from tags vs. source

**Implementation Plan**:
- Create `FileNameFormatter` service
- Parse template expressions
- Support tag reading via TagLibSharp
- Handle special characters sanitization

### 5. Configuration Profiles
- Multiple profiles in config file
- Profile conditions based on input type/mode
- Auto-selection based on context
- Override individual settings

**Implementation Plan**:
- Extend `ConfigManager` to load profiles
- Create profile evaluation engine
- Add profile selector in UI
- Store active profile in runtime config

### 6. On-Complete Actions
- Run commands after download succeeds/fails
- Command chaining with + prefix
- Variable substitution in commands
- Output capture and processing

**Implementation Plan**:
- Create `OnCompleteActionExecutor` service
- Parse action prefixes (1:, 2:, a:, s:, h:, r:, u:)
- Implement process execution with environment variables
- Handle output capture for index updates

### 7. Search Rate Limiting
- Prevent 30-minute Soulseek bans
- Configurable search quota (default: 34/220s)
- Queue-based search throttling
- Wait status feedback

**Implementation Plan**:
- Create `SearchRateLimiter` service
- Implement token bucket algorithm
- Queue pending searches
- Update UI with "Waiting" status

### 8. Index File Management
- Track downloaded items with state
- Skip already-downloaded tracks
- Resume on failure
- Version tracking

**Implementation Plan**:
- Create `DownloadIndex` model (JSON-based)
- Implement `IndexManager` service
- Serialize/deserialize download history
- Check before searching

### 9. Interactive Album Mode
- Display search results ranked by availability
- Keyboard shortcuts for navigation/selection
- Download specific files from results
- Filter results in real-time

**Implementation Plan**:
- Create `InteractiveDownloadView` (new WPF window)
- Implement keyboard event handling
- Show availability count for each result
- Add file-level selection

### 10. Advanced Search Options
- Remove "feat." artists
- Apply regex patterns to titles
- "Artist maybe wrong" fallback search
- Desperate mode (slower, more thorough)
- Length tolerance configuration

**Implementation Plan**:
- Create `SearchQueryBuilder` service
- Add regex support in search preparation
- Implement fallback search logic
- Add search profiling/timing

---

## Architecture Patterns from slsk-batchdl

### 1. Service-Oriented Design
- Each major feature is a standalone service
- Services are composable and testable
- DI container manages service lifecycle

### 2. Configuration-First Development
- Rich configuration system drives behavior
- Profile-based configuration switching
- Default values with override capability

### 3. Filter/Ranking Pipeline
- Layered filtering (required → preferred)
- Scoring system for result ranking
- Extensible condition framework

### 4. Event-Driven Architecture
- Download events propagate to UI
- Status updates via observer pattern
- Progress reported at granular level

### 5. Plugin-Style Extensibility
- Input sources are pluggable
- Conditions are composable
- Actions are chainable

---

## Priority Implementation Order

**Phase 1 (Core)**:
1. CSV input support
2. Album download mode
3. File condition filtering
4. Name formatting

**Phase 2 (Enhancement)**:
5. Configuration profiles
6. Interactive album mode
7. Search rate limiting
8. Index file management

**Phase 3 (Advanced)**:
9. Multiple input sources (Spotify, YouTube)
10. On-complete actions
11. Advanced search options
12. Aggregate modes

---

## Code Reuse from slsk-batchdl

### Direct Inspirations

**1. Name Format Parser**
- Regex-based template parsing
- Null-coalescing with | operator
- Parent expressions with () for ignoring in null checks

**2. Search Query Builder**
- Property parsing: "artist=X, title=Y, length=Z"
- Shorthand: "Artist - Title"
- Artist fallback logic

**3. File Condition System**
- Condition classes for each property
- Composition pattern (AND/OR logic)
- Ranking by specificity

**4. Download State Machine**
- Clear state transitions
- Event emission at each transition
- Error capture and reporting

**5. Configuration Format**
- INI-style with sections
- Comments with #
- Tilde expansion for paths

---

## Not in Scope (Keep WPF GUI Focus)

- CLI argument parsing (we use GUI)
- Help generator system (less relevant for GUI)
- Docker support (focus on Windows native)
- Automated testing harness (but add unit tests)

---

## Implementation Files to Create

```
Services/
  ├─ InputParsers/
  │  ├─ IInputSource.cs
  │  ├─ CsvInputSource.cs
  │  ├─ SpotifyInputSource.cs
  │  └─ YoutubeInputSource.cs
  ├─ SearchService.cs (enhanced)
  ├─ FileConditionEvaluator.cs
  ├─ FileNameFormatter.cs
  ├─ SearchRateLimiter.cs
  ├─ OnCompleteActionExecutor.cs
  └─ DownloadIndexManager.cs

Models/
  ├─ DownloadMode.cs
  ├─ FileCondition.cs
  ├─ SearchQuery.cs
  ├─ DownloadIndex.cs
  └─ ConfigProfile.cs

Views/
  ├─ InteractiveDownloadWindow.xaml
  └─ InteractiveDownloadViewModel.cs
```

