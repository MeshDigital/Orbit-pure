# SQLite NOT NULL Constraint Fix on Audio Features Save

Date: 2026-06-16  
Status: Completed  

## Executive Summary

During track re-analysis, a SQLite error (`SQLite Error 19: 'NOT NULL constraint failed: PlaylistTracks.TrackUniqueHash'`) was encountered inside [SaveAudioFeaturesAsync](file:///C:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DatabaseService.cs#L668) when saving analyzed audio features. 

This error was caused by a delete-and-add pattern in `SaveAudioFeaturesAsync` combined with EF Core's relationship tracking behavior. When the existing `AudioFeaturesEntity` was removed, EF Core's change tracker attempted to nullify the optional relationship link (`TrackUniqueHash`) on any referencing [PlaylistTrackEntity](file:///C:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Database/Entities/TrackEntity.cs#L232) rows. Because `TrackUniqueHash` is configured as a `NOT NULL` column in the database, this nullification triggered a constraint violation during `SaveChangesAsync`.

Both the save method and the EF Core model configuration were updated to resolve this.

---

## Deliverables

### 1. In-Place Updates in Database Service
* **Modify Save Pattern**:
  - Updated [SaveAudioFeaturesAsync](file:///C:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DatabaseService.cs#L668) in [DatabaseService.cs](file:///C:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DatabaseService.cs).
  - Instead of removing the existing [AudioFeaturesEntity](file:///C:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Data/Entities/AudioFeaturesEntity.cs) and adding a new instance, it now updates the existing record in-place via `context.Entry(existing).CurrentValues.SetValues(features)` while preserving the database identity (`Guid Id`).
  - This matches the update pattern already successfully used in `UpdateAudioFeaturesAsync` and prevents EF Core's change tracker from flagging the related records as orphaned or modified.

### 2. Fluent API Configuration Hardening
* **Delete Behavior Configuration**:
  - Updated [AppDbContext.cs](file:///C:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Data/AppDbContext.cs).
  - Explicitly configured the relationship between `PlaylistTrackEntity` and `AudioFeaturesEntity` to use `DeleteBehavior.NoAction`:
    ```csharp
    modelBuilder.Entity<PlaylistTrackEntity>()
        .HasOne(pt => pt.AudioFeatures)
        .WithMany()
        .HasForeignKey(pt => pt.TrackUniqueHash)
        .HasPrincipalKey(af => af.TrackUniqueHash)
        .IsRequired(false)
        .OnDelete(DeleteBehavior.NoAction);
    ```
  - This prevents EF Core from attempting to set `TrackUniqueHash` to null when the corresponding `AudioFeatures` record is deleted or modified.
