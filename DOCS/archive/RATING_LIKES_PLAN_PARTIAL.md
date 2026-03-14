# Track Rating & Likes Feature

## Overview
Add user engagement features: 5-star ratings and likes for tracks, with automatic "Liked Songs" smart playlist.

## Data Model Changes ✅

### PlaylistTrack.cs
```csharp
public int Rating { get; set; } = 0; // 1-5 stars, 0 = not rated
public bool IsLiked { get; set; } = false;
public int PlayCount { get; set; } = 0;
public DateTime? LastPlayedAt { get; set; }
```

### PlaylistTrackEntity.cs
```csharp
public int Rating { get; set; } = 0;
public bool IsLiked { get; set; } = false;
public int PlayCount { get; set; } = 0;
public DateTime? LastPlayedAt { get; set; }
```

## Implementation Plan

### Phase 1: Backend (1 hour)

#### 1.1 Database Migration
- [ ] Add columns to PlaylistTrackEntity
- [ ] Create migration script
- [ ] Test migration

#### 1.2 Service Methods
- [ ] `SetTrackRating(trackId, rating)` in LibraryService
- [ ] `ToggleTrackLike(trackId)` in LibraryService
- [ ] `IncrementPlayCount(trackId)` in PlayerViewModel
- [ ] Update database on changes

### Phase 2: UI Components (1.5 hours)

#### 2.1 Rating Control
- [ ] Create StarRatingControl.axaml
- [ ] 5 clickable stars
- [ ] Hover preview
- [ ] Current rating display
- [ ] Bind to PlaylistTrackViewModel.Rating

#### 2.2 Like Button
- [ ] Heart icon (♡/♥)
- [ ] Toggle on click
- [ ] Animate on like
- [ ] Bind to PlaylistTrackViewModel.IsLiked

#### 2.3 Integration Points
- [ ] Add to Now Playing page
- [ ] Add to Library track list (context menu or inline)
- [ ] Add to Search results
- [ ] Add to Queue panel

### Phase 3: Liked Songs Playlist (1 hour)

#### 3.1 Smart Playlist
- [ ] Create "Liked Songs" virtual playlist
- [ ] Auto-populate from IsLiked = true
- [ ] Add to Library sidebar
- [ ] Special icon (💚)
- [ ] Real-time updates

#### 3.2 Filtering
- [ ] Filter by rating (>= X stars)
- [ ] Sort by rating
- [ ] Sort by play count
- [ ] Sort by last played

## UI Design

### Star Rating Component
```
☆☆☆☆☆  (not rated)
★★★☆☆  (3 stars)
★★★★★  (5 stars)
```

### Like Button States
```
♡  (not liked)
♥  (liked - green #1DB954)
```

## Files to Create/Modify

### New Files
- `Views/Avalonia/Controls/StarRatingControl.axaml`
- `Views/Avalonia/Controls/StarRatingControl.axaml.cs`
- `ViewModels/LikedSongsViewModel.cs`

### Modified Files
- `Models/PlaylistTrack.cs` ✅
- `Data/TrackEntity.cs` ✅
- `Services/LibraryService.cs` (add rating/like methods)
- `ViewModels/PlaylistTrackViewModel.cs` (add Rating, IsLiked properties)
- `Views/Avalonia/NowPlayingPage.axaml` (add rating/like UI)
- `Views/Avalonia/LibraryPage.axaml` (add rating column)

## Database Migration

```csharp
// In AppDbContext.OnModelCreating or new migration
modelBuilder.Entity<PlaylistTrackEntity>()
    .Property(t => t.Rating)
    .HasDefaultValue(0);

modelBuilder.Entity<PlaylistTrackEntity>()
    .Property(t => t.IsLiked)
    .HasDefaultValue(false);

modelBuilder.Entity<PlaylistTrackEntity>()
    .Property(t => t.PlayCount)
    .HasDefaultValue(0);
```

## Total Effort

**Backend**: 1 hour  
**UI Components**: 1.5 hours  
**Liked Songs Playlist**: 1 hour  

**Total**: 3.5 hours

## Priority

**Add to TODO.md as Phase 2.8** (after Smart Playlists)

This feature enhances user engagement and provides foundation for:
- Personalized recommendations
- Most played tracks
- Rating-based filtering
- Quality tracking
