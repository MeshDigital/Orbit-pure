using Microsoft.EntityFrameworkCore;
using System.IO;
using SLSKDONET.Models;
using Microsoft.Data.Sqlite; // Phase 1B
using SLSKDONET.Data.Entities; // Added for TrackTechnicalEntity

namespace SLSKDONET.Data;

public class AppDbContext : DbContext
{
    public AppDbContext()
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<TrackTechnicalEntity> TechnicalDetails { get; set; }
    public DbSet<TrackEntity> Tracks { get; set; }
    public DbSet<LibraryEntryEntity> LibraryEntries { get; set; }
    public DbSet<PlaylistJobEntity> Projects { get; set; }
    public DbSet<PlaylistTrackEntity> PlaylistTracks { get; set; }
    public DbSet<PlaylistActivityLogEntity> ActivityLogs { get; set; }
    public DbSet<QueueItemEntity> QueueItems { get; set; }
    public DbSet<Entities.SpotifyMetadataCacheEntity> SpotifyMetadataCache { get; set; }
    public DbSet<LibraryHealthEntity> LibraryHealth { get; set; }
    public DbSet<Entities.PendingOrchestrationEntity> PendingOrchestrations { get; set; }
    public DbSet<Entities.EnrichmentTaskEntity> EnrichmentTasks { get; set; }
    public DbSet<Entities.AudioAnalysisEntity> AudioAnalysis { get; set; }
    public DbSet<Entities.AudioFeaturesEntity> AudioFeatures { get; set; }
    public DbSet<Entities.AnalysisRunEntity> AnalysisRuns { get; set; } // Phase 21: Analysis Run Tracking
    public DbSet<Entities.ForensicLogEntry> ForensicLogs { get; set; } // Phase 4.7: Forensic Logging
    public DbSet<Entities.StyleDefinitionEntity> StyleDefinitions { get; set; } // Phase 15: Style Lab
    public DbSet<Entities.LibraryActionLogEntity> LibraryActionLogs { get; set; } // Phase 16.1: Ledger
    public DbSet<Entities.BlacklistedItemEntity> Blacklist { get; set; } // Phase 7: Forensic Duplication
    public DbSet<Entities.LibraryFolderEntity> LibraryFolders { get; set; } // Library Folder Scanner
    public DbSet<Entities.TrackPhraseEntity> TrackPhrases { get; set; } // Phase 17: Cue Generation
    public DbSet<Entities.GenreCueTemplateEntity> GenreCueTemplates { get; set; } // Phase 17: Cue Generation
    public DbSet<Entities.SmartCrateDefinitionEntity> SmartCrateDefinitions { get; set; } // Phase 23: Smart Crates
    public DbSet<Entities.SetListEntity> SetLists { get; set; } // Phase 3: Set-Prep Intelligence
    public DbSet<Entities.SetTrackEntity> SetTracks { get; set; } // Phase 3: Set-Prep Intelligence
    public DbSet<Entities.StemPreferenceEntity> StemPreferences { get; set; } // Phase 5: Engagement

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            var dbPath = Path.Combine(appData, "ORBIT", "library.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            // Phase 1B/0: Enable WAL Mode and Busy Timeout for better concurrency
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                DefaultTimeout = 10000 // 10 seconds busy timeout
            }.ToString();

            optionsBuilder.UseSqlite(connectionString, options =>
            {
                options.CommandTimeout(30); // 30 second timeout for long operations
            })
            .ConfigureWarnings(warnings =>
            {
                // Suppress this warning since we use runtime schema patching via SchemaMigratorService
                // instead of code-first migrations for flexibility
                warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning);
            });
        }
    }

    /// <summary>
    /// Phase 1B: Configures SQLite connection with WAL mode and optimal settings.
    /// Called by DatabaseService.InitAsync during startup.
    /// </summary>
    public void ConfigureSqliteOptimizations(System.Data.Common.DbConnection connection)
    {
        if (connection is not SqliteConnection sqliteConnection) return;

        if (sqliteConnection.State != System.Data.ConnectionState.Open)
            sqliteConnection.Open();

        using var command = connection.CreateCommand();

        // Phase 1B: Enable Write-Ahead Logging
        command.CommandText = "PRAGMA journal_mode=WAL;";
        var result = command.ExecuteScalar()?.ToString();
        System.Console.WriteLine($"[Phase 1B] Journal mode set to: {result}");

        // Set synchronous mode to NORMAL (safe with WAL, much faster than FULL)
        command.CommandText = "PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();

        // Sprint 5C Hardening: Explicit busy timeout
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();

        // Increase cache size to 10MB (default is ~2MB)
        command.CommandText = "PRAGMA cache_size=-10000;"; // Negative = KB
        command.ExecuteNonQuery();

        // Auto-checkpoint at 1000 pages (~4MB)
        command.CommandText = "PRAGMA wal_autocheckpoint=1000;";
        command.ExecuteNonQuery();

        System.Console.WriteLine("[Phase 1B] SQLite WAL mode enabled successfully");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Phase 0B: Map PlaylistJobEntity to "Projects" table (terminology unification)
        modelBuilder.Entity<PlaylistJobEntity>().ToTable("Projects");

        // Configure PlaylistJob -> PlaylistTrack relationship
        modelBuilder.Entity<PlaylistJobEntity>()
            .HasMany(j => j.Tracks)
            .WithOne(t => t.Job)
            .HasForeignKey(t => t.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 0: Composite Indexing for Library Performance
        // Searching by Artist + Title is the #1 query.
        modelBuilder.Entity<TrackEntity>()
            .HasIndex(t => new { t.Artist, t.Title })
            .HasDatabaseName("IX_Tracks_Artist_Title");

        // Phase 0: Fast Lookup Index for Orchestration
        modelBuilder.Entity<TrackEntity>()
            .HasIndex(t => t.GlobalId)
            .IsUnique();

        // Phase 1A: Add Query Indexes
        modelBuilder.Entity<PlaylistTrackEntity>()
            .HasIndex(t => t.PlaylistId)
            .HasDatabaseName("IX_PlaylistTrack_PlaylistId");

        modelBuilder.Entity<PlaylistTrackEntity>()
            .HasIndex(t => t.Status)
            .HasDatabaseName("IX_PlaylistTrack_Status");

        // Data Integrity: Add index for sync checks
        modelBuilder.Entity<PlaylistTrackEntity>()
            .HasIndex(t => t.TrackUniqueHash);

        modelBuilder.Entity<PlaylistJobEntity>()
            .HasIndex(j => j.CreatedAt)
            .HasDatabaseName("IX_PlaylistJob_CreatedAt");

        // Phase 1B: Centralize Status Enum (using EF Core's built-in converter)
        modelBuilder
            .Entity<PlaylistTrackEntity>()
            .Property(e => e.Status)
            .HasConversion<string>();

        // Phase 1C: Implement Global Query Filter for Soft Deletes
        modelBuilder.Entity<PlaylistJobEntity>().HasQueryFilter(j => !j.IsDeleted);
        // Playlist Activity Logs
        modelBuilder.Entity<PlaylistJobEntity>()
            .HasMany<PlaylistActivityLogEntity>()
            .WithOne(l => l.Job)
            .HasForeignKey(l => l.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 1: Enrichment Task Indexes
        modelBuilder.Entity<Entities.EnrichmentTaskEntity>()
            .HasIndex(e => e.Status)
            .HasDatabaseName("IX_EnrichmentTasks_Status");

        modelBuilder.Entity<Entities.EnrichmentTaskEntity>()
            .HasIndex(e => new { e.Status, e.CreatedAt })
            .HasDatabaseName("IX_EnrichmentTasks_Status_CreatedAt");

        // Phase 1: Track Technical Details 1:1 Relationship
        modelBuilder.Entity<Entities.TrackTechnicalEntity>()
            .HasOne(t => t.PlaylistTrack)
            .WithOne(p => p.TechnicalDetails)
            .HasForeignKey<Entities.TrackTechnicalEntity>(t => t.PlaylistTrackId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 7: Forensic Duplication
        modelBuilder.Entity<Entities.BlacklistedItemEntity>()
            .HasIndex(b => b.Hash)
            .IsUnique()
            .HasDatabaseName("IX_Blacklist_Hash");

        // Phase 21: AI Brain Relationships

        // 1. Valid Join Target: AudioFeatures must have Unique Hash
        modelBuilder.Entity<AudioFeaturesEntity>()
            .HasIndex(af => af.TrackUniqueHash)
            .IsUnique();

        modelBuilder.Entity<AudioFeaturesEntity>()
            .HasAlternateKey(af => af.TrackUniqueHash);

        // 2. LibraryEntry -> AudioFeatures (1:1)
        modelBuilder.Entity<LibraryEntryEntity>()
            .HasOne(e => e.AudioFeatures)
            .WithOne()
            .HasForeignKey<AudioFeaturesEntity>(af => af.TrackUniqueHash)
            .HasPrincipalKey<LibraryEntryEntity>(le => le.UniqueHash)
            .OnDelete(DeleteBehavior.Cascade);

        // 2b. LibraryEntry alternate key for Guid-based references
        modelBuilder.Entity<LibraryEntryEntity>()
            .HasAlternateKey(e => e.Id);

        // 3. PlaylistTrack -> AudioFeatures (Many:1 Lookup)
        modelBuilder.Entity<PlaylistTrackEntity>()
            .HasOne(pt => pt.AudioFeatures)
            .WithMany()
            .HasForeignKey(pt => pt.TrackUniqueHash)
            .HasPrincipalKey(af => af.TrackUniqueHash)
            .IsRequired(false);

        // 4. SetList -> SetTrack (1:Many)
        modelBuilder.Entity<Entities.SetListEntity>()
            .HasMany(s => s.Tracks)
            .WithOne(t => t.SetList)
            .HasForeignKey(t => t.SetListId)
            .OnDelete(DeleteBehavior.Cascade);

        // 4b. SetTrack -> LibraryEntry (optional, via LibraryId Guid)
        modelBuilder.Entity<Entities.SetTrackEntity>()
            .HasOne(t => t.Library)
            .WithMany()
            .HasForeignKey(t => t.LibraryId)
            .HasPrincipalKey(e => e.Id)
            .IsRequired(false);

        modelBuilder.Entity<Entities.SetTrackEntity>()
            .Property(e => e.TransitionType)
            .HasConversion<string>();

        // Phase 5: Stem Preferences (1:1 with LibraryEntry)
        modelBuilder.Entity<Entities.StemPreferenceEntity>()
            .HasOne(p => p.LibraryEntry)
            .WithOne()
            .HasForeignKey<Entities.StemPreferenceEntity>(p => p.TrackUniqueHash)
            .HasPrincipalKey<LibraryEntryEntity>(e => e.UniqueHash)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
