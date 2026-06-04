-- Frequent Sources: local-only, opt-in tables for repeated source tracking and prefetch staging.
CREATE TABLE IF NOT EXISTS FrequentSources (
    SourceUsername TEXT NOT NULL,
    FolderPath TEXT NOT NULL,
    DownloadCount INTEGER NOT NULL,
    LastDownloadedAtUtc TEXT NOT NULL,
    TotalBytesDownloaded INTEGER NOT NULL,
    LocalNote TEXT NULL,
    IsFriend INTEGER NOT NULL,
    IsPinned INTEGER NOT NULL,
    PRIMARY KEY (SourceUsername, FolderPath)
);

CREATE INDEX IF NOT EXISTS IX_FrequentSources_Rank
ON FrequentSources (IsPinned, IsFriend, DownloadCount, LastDownloadedAtUtc);

CREATE TABLE IF NOT EXISTS PrefetchQueueItems (
    Id TEXT NOT NULL PRIMARY KEY,
    SourceUsername TEXT NOT NULL,
    RemotePath TEXT NOT NULL,
    LocalStagingPath TEXT NOT NULL,
    Status INTEGER NOT NULL,
    EnqueuedAtUtc TEXT NOT NULL,
    CompletedAtUtc TEXT NULL,
    BytesDownloaded INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_PrefetchQueue_Source_Status
ON PrefetchQueueItems (SourceUsername, Status);

CREATE INDEX IF NOT EXISTS IX_PrefetchQueue_EnqueuedAt
ON PrefetchQueueItems (EnqueuedAtUtc);