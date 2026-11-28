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

    /// <summary>The file's path on the device that copied it (used to restore it to the same place).</summary>
    public string OriginalPath { get; set; } = string.Empty;

    public DateTime CopiedUtc { get; set; }
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
    DateTime? SnapshotUtc, IReadOnlyList<VaultDevice> Devices);

/// <summary>What a restore would do, shown before anything changes.</summary>
public sealed record VaultRestorePlan(
    int SnapshotTracks, int CurrentTracks,
    int SnapshotPlaylists, int CurrentPlaylists,
    int SnapshotCues, int CurrentCues,
    int FilesMissingLocally, int FilesMissingInVault,
    DateTime? SnapshotUtc, string CurrentBackupFolder);
