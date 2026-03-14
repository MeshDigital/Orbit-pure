# Spotify Integration - Technical Reference

**Last Updated**: December 26, 2025  
**Status**: Active with Developer Mode Restrictions

---

## Quick Reference: What Works vs What Doesn't

| Feature | Endpoint | Scopes Required | Status | Notes |
|---------|----------|----------------|--------|-------|
| **Album Art** | `Search.Item()` | None | ✅ Works | Basic track info, includes `Album.Images` |
| **Track Identification** | `Search.Item()` | None | ✅ Works | Artist/Title → Spotify ID |
| **Audio Features** | `Tracks.GetSeveralAudioFeatures()` | Varies | ❌ 403 in Dev Mode | BPM, Energy, Valence, Danceability |
| **Recommendations** | `Browse.GetRecommendations()` | `UserTopRead` | ⚠️ Requires Listening History | 404 if no top tracks |
| **Top Tracks** | `Personalization.GetTopTracks()` | `UserTopRead` | ⚠️ Requires Listening History | Used as seeds for recommendations |

---

## Authentication Flow (PKCE)

### Current Implementation
- **Flow**: OAuth 2.0 with PKCE (Proof Key for Code Exchange)
- **Service**: `SpotifyAuthService.cs`
- **Token Storage**: OS-level encryption (`ProtectedData` on Windows)
- **Refresh**: Automatic via `PKCEAuthenticator`

### Required Scopes (As of Dec 2025)
```csharp
Scopes.UserReadPrivate      // Basic profile
Scopes.UserReadEmail        // Email address
Scopes.PlaylistReadPrivate  // Private playlists
Scopes.PlaylistReadCollaborative // Collaborative playlists
Scopes.UserLibraryRead      // Saved tracks
Scopes.UserTopRead          // Top tracks & artists (for Recommendations)
Scopes.UserFollowRead       // Following/followers
```

### Token Lifecycle
1. **Initial Login**: Browser-based consent → Authorization Code
2. **Exchange**: Code + PKCE Verifier → Access Token + Refresh Token
3. **Refresh**: Automatic before expiry (5 min buffer)
4. **Storage**: Refresh token encrypted in OS keychain

---

## Endpoints & Use Cases

### 1. Search (Track Identification)
**Endpoint**: `client.Search.Item(searchRequest)`  
**Purpose**: Convert (Artist, Title) → Spotify ID + Album Art URL  
**Returns**:
```csharp
{
    Id: "3Qm86XLflmIXVm1wcwkgDK",
    Name: "Track Title",
    Artists: ["Artist Name"],
    Album: {
        Name: "Album Name",
        Images: [
            { Url: "https://...", Width: 640, Height: 640 }
        ]
    },
    ExternalIds: { "isrc": "..." }
}
```
**Restrictions**: None - works in Developer Mode  
**Circuit Breaker**: Not applied (stable endpoint)

### 2. Audio Features (Batch)
**Endpoint**: `client.Tracks.GetSeveralAudioFeatures(trackIds)`  
**Purpose**: Fetch BPM, Energy, Valence for up to 100 tracks  
**Returns**:
```csharp
{
    Tempo: 128.0,          // BPM
    Energy: 0.85,          // 0.0-1.0
    Valence: 0.65,         // 0.0-1.0 (mood)
    Danceability: 0.72,    // 0.0-1.0
    Key: 5,                // 0-11 (C=0, C#=1, etc.)
    Mode: 1                // 0=minor, 1=major
}
```
**Restrictions**: ⚠️ **403 Forbidden in Developer Mode**  
**Circuit Breaker**: 30-minute cooldown on first 403  
**Workaround**: Request Spotify App Review or whitelist user email

### 3. Recommendations
**Endpoint**: `client.Browse.GetRecommendations()`  
**Purpose**: Discover new tracks based on listening history  
**Requires**:
- `UserTopRead` scope ✅
- Active listening history (Spotify playback)
**Errors**:
- `404 NotFound`: No top tracks available (fresh account)
- `403 Forbidden`: Missing `UserTopRead` scope
**Circuit Breaker**: Skips silently if service degraded

---

## Configuration Flags

### `SpotifyUseApi` (AppConfig.cs)
**Default**: `true` (as of Dec 26, 2025)  
**Purpose**: Master toggle for all Spotify metadata enrichment  
**When Disabled**:
- ❌ No Album Art URLs fetched
- ❌ No BPM/Energy/Valence
- ❌ No Track Identification
- ❌ Recommendations disabled

**UI Location**: Settings → "Quality Guard & The Brain" → "Spotify Metadata Enrichment"

---

## Circuit Breaker Logic

### Purpose
Prevent log spam and API ban when encountering persistent errors (403, 429).

### Trigger Conditions
| Error | Cooldown | Applies To |
|-------|----------|-----------|
| `403 Forbidden` | 30 minutes | All Spotify calls |
| `429 Too Many Requests` | `RetryAfter` header value | All Spotify calls |

### State Management
```csharp
private static bool _isServiceDegraded = false;
private static DateTime _retryAfter = DateTime.MinValue;
```

### Affected Services
- `SpotifyEnrichmentService` (Identification, Audio Features, Recommendations)
- `LibraryEnrichmentWorker` (pauses enrichment loop)

---

## Developer Mode Restrictions

### What Is Developer Mode?
All Spotify apps start in "Developer Mode" with restricted access until approved via [Spotify App Review](https://developer.spotify.com/documentation/web-api/concepts/quota-modes).

### Current Limitations
1. ❌ **No Audio Features API** (403 Forbidden)
2. ⚠️ **User Whitelisting Required**: Only emails added to the app dashboard can authenticate
3. ⚠️ **Rate Limits**: Lower quota than production apps

### How to Exit Developer Mode
1. Submit app for Spotify Extended Quota Mode
2. OR: Add user emails to dashboard whitelist (Settings → Users and Access)

---

## Error Handling Patterns

### 1. Graceful Degradation (Album Art Only)
If Audio Features fail (403), app continues with:
- ✅ Album Art from Search
- ✅ Basic metadata (Artist, Title, Album)
- ❌ No BPM/Energy (enrichment incomplete)

### 2. Circuit Breaker Activation
```
[11:46:55 ERR] Spotify API error in GetAudioFeaturesBatchAsync. Status: Forbidden
[11:46:55 WRN] Spotify API 403 Forbidden - Disabling Audio Features for this session.
[11:54:00 WRN] Spotify Service Degraded (Circuit Breaker Active). Worker pausing for 1 minute.
```

### 3. Fresh Account (No History)
```
[11:45:57 ERR] Spotify API error in GetRecommendationsAsync. Status: NotFound
```
**Meaning**: No top tracks to use as seeds. Not critical - Recommendations are optional.

---

## Troubleshooting

### "403 Forbidden on Audio Features"
**Cause**: Developer Mode restriction  
**Fix**: Add your Spotify email to app dashboard whitelist OR submit for Extended Quota

### "Album Art Not Showing"
**Check**:
1. `SpotifyUseApi` enabled in Settings?
2. Database: `SELECT AlbumArtUrl FROM LibraryEntry WHERE AlbumArtUrl IS NOT NULL`
3. Artwork cache: `%AppData%/SLSKDONET/artwork/` should have images

### "Circuit Breaker Won't Reset"
**Manual Reset**: Restart the application  
**Auto Reset**: Wait 30 minutes for cooldown

### "Need to Re-Login After Update"
**Why**: New scopes added (e.g., `UserTopRead` for Recommendations)  
**Fix**: Settings → Spotify → Disconnect → Reconnect

---

## Future Roadmap

### Planned Enhancements
1. **Spotify App Review Submission** → Exit Developer Mode
2. **Fallback Metadata Sources** → If Spotify fails, use MusicBrainz/AcousticBrainz
3. **Offline Mode** → Cache enrichment data for 30 days

### API Wishlist (Requires Extended Quota)
- Playlist creation/editing
- User's Liked Songs sync
- Recently Played tracking

---

## Related Documentation
- [`DOCS/SPOTIFY_AUTH.md`](SPOTIFY_AUTH.md) - PKCE flow details
- [`DOCS/SPOTIFY_ENRICHMENT_PIPELINE.md`](SPOTIFY_ENRICHMENT_PIPELINE.md) - 4-stage enrichment architecture
- [`DOCS/BUG_REPORT_20241222_SPOTIFY_RATE_LIMIT.md`](BUG_REPORT_20241222_SPOTIFY_RATE_LIMIT.md) - Circuit breaker implementation
- [Spotify Web API Documentation](https://developer.spotify.com/documentation/web-api/)

---

**Maintainer Notes**: This document reflects the state as of the Dec 26, 2025 session where we fixed the `SpotifyUseApi` default and added enhanced 403 diagnostics.
