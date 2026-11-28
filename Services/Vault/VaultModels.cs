using System;
using System.Collections.Generic;

using SLSKDONET.Utils;

namespace SLSKDONET.Services.Vault;

/// <summary>
/// <c>vault.json</c>: what a Vault is (epic #632). A Vault is recognised by <see cref="VaultId"/>,
/// never by its drive letter or mount path, so the same drive works under another letter or on a Mac.
/// </summary>
public sealed class VaultManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string VaultId { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<VaultDevice> Devices { get; set; } = new();
    public List<VaultRoot> Roots { get; set; } = new();

    /// <summary>When "Verify vault" last re-read every copy (null = never).</summary>
    public DateTime? LastVerifiedUtc { get; set; }
}

/// <summary>A machine that has backed up to this Vault.</summary>
public sealed class VaultDevice
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public DateTime? LastBackupUtc { get; set; }
    public int LastBackupTracks { get; set; }
}

/// <summary>
/// A library root: one Library Source folder, stored in the Vault under <c>Music/&lt;Folder&gt;</c>.
/// <see cref="LocalPaths"/> maps a device id to where that root lives on the device, so a track is
/// stored as root + relative path rather than an absolute path.
/// </summary>
public sealed class VaultRoot
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public Dictionary<string, string> LocalPaths { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One file in <c>music-index.json</c>: what was copied, from where, and how to check it.</summary>
public sealed class VaultFileEntry
{
    public string RootId { get; set; } = string.Empty;

    /// <summary>Path inside the root, forward slashes.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public long Size { get; set; }
    public string? ContentHash { get; set; }
    public string TrackHash { get; set; } = string.Empty;

    /// <summary>The file's path on the device that copied it (kept for reference; restores resolve the
    /// root's folder on the restoring device instead, see <see cref="VaultService.ResolveLocalRoot"/>).</summary>
    public string OriginalPath { get; set; } = string.Empty;

    public DateTime CopiedUtc { get; set; }

    /// <summary>SHA-256 (hex) of the copied file's bytes, computed while copying. Lets "Verify vault"
    /// detect a copy that rotted on the backup drive. Null for entries copied before it existed until a
    /// verify fills it in.</summary>
    public string? FileSha256 { get; set; }

    /// <summary>The source file's last-write time when it was copied. A tag edit rewrites the file
    /// without changing the audio (and often not the size), so this is what notices it.</summary>
    public DateTime? SourceModifiedUtc { get; set; }
}

/// <summary>A Vault root as seen from this computer, for the restore screen.</summary>
public sealed record VaultRootPlan(
    string RootId, string Name, int FilesInVault, int MissingHere,
    string? LocalPath, bool LocalPathUsable, IReadOnlyList<string> PathsOnOtherDevices);

/// <summary>What restoring missing files did.</summary>
public sealed record VaultRestoreFilesResult(
    int Restored, int Failed, int RootsSkipped, int LibraryPathsRepointed)
{
    public string Summary =>
        $"Restored {CountFormat.N(Restored)} file" + (Restored == 1 ? "" : "s")
        + (Failed > 0 ? $", {CountFormat.N(Failed)} failed (see the log)" : "")
        + (RootsSkipped > 0 ? $"; {CountFormat.N(RootsSkipped)} folder" + (RootsSkipped == 1 ? "" : "s") + " skipped: choose where it goes on this computer" : "")
        + (LibraryPathsRepointed > 0 ? $"; pointed {CountFormat.N(LibraryPathsRepointed)} library track" + (LibraryPathsRepointed == 1 ? "" : "s") + " at the new folder" : "")
        + ".";
}

/// <summary>What "Verify vault" found.</summary>
public sealed record VaultVerifyResult(
    int Checked, int Ok, int Missing, int Damaged, int ChecksumsAdded, int CouldNotCheck,
    IReadOnlyList<string> Problems)
{
    public bool IsHealthy => Missing == 0 && Damaged == 0;

    public string Summary =>
        $"Checked {CountFormat.N(Checked)} copies: {CountFormat.N(Ok)} ok"
        + (Damaged > 0 ? $", {CountFormat.N(Damaged)} DAMAGED" : "")
        + (Missing > 0 ? $", {CountFormat.N(Missing)} missing from the drive" : "")
        + (CouldNotCheck > 0 ? $", {CountFormat.N(CouldNotCheck)} couldn't be checked (no checksum yet and the original changed or is gone; the next backup copies them again)" : "")
        + (ChecksumsAdded > 0 ? $". Added checksums for {CountFormat.N(ChecksumsAdded)} older copies" : "")
        + (IsHealthy ? "." : ". Run a backup to replace them.");
}

public sealed record VaultProgress(string Phase, int Done, int Total, string? Current);

public sealed record VaultBackupResult(
    int Copied, int Unchanged, int MissingOnDisk, int Failed, long BytesCopied,
    int Verified, int VerifyMismatches, int SnapshotTracks, string SnapshotCheck)
{
    public bool IsHealthy => Failed == 0 && VerifyMismatches == 0 && SnapshotCheck == "ok";

    public string Summary =>
        $"{CountFormat.N(Copied)} copied, {CountFormat.N(Unchanged)} already up to date"
        + (MissingOnDisk > 0 ? $", {CountFormat.N(MissingOnDisk)} missing on this computer" : string.Empty)
        + (Failed > 0 ? $", {CountFormat.N(Failed)} failed" : string.Empty)
        + $". Checked {CountFormat.N(Verified)} copies"
        + (VerifyMismatches > 0 ? $": {CountFormat.N(VerifyMismatches)} DIFFER from the original" : ": all match")
        + (SnapshotCheck == "ok" ? $"; database snapshot ok ({CountFormat.N(SnapshotTracks)} tracks)." : $"; database snapshot check failed: {SnapshotCheck}.");
}

/// <summary>Where this machine stands with a Vault.</summary>
public sealed record VaultStatus(
    string VaultRoot, string VaultId, DateTime? LastBackupUtc, int FilesInVault, long BytesInVault,
    DateTime? SnapshotUtc, IReadOnlyList<VaultDevice> Devices, DateTime? LastVerifiedUtc = null);

/// <summary>What a restore would do, shown before anything changes.</summary>
public sealed record VaultRestorePlan(
    int SnapshotTracks, int CurrentTracks,
    int SnapshotPlaylists, int CurrentPlaylists,
    int SnapshotCues, int CurrentCues,
    int FilesMissingLocally, int FilesMissingInVault,
    DateTime? SnapshotUtc, string CurrentBackupFolder,
    IReadOnlyList<VaultRootPlan> Roots);
