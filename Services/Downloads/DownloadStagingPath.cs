using System;
using System.IO;

namespace SLSKDONET.Services.Downloads;

/// <summary>
/// LF3 (#345): where a download's <c>.part</c> lives while it is in flight. Downloads write into a
/// hidden staging folder under the download directory and are only moved to their final template path
/// (via <c>MoveAtomicAsync</c>) after the transfer and verification succeed, so a failed download never
/// leaves a file inside the library tree.
/// </summary>
public static class DownloadStagingPath
{
    public const string StagingFolderName = ".orbit-staging";

    /// <summary>The hidden staging directory under the download directory.</summary>
    public static string GetStagingDirectory(string downloadDir)
    {
        if (string.IsNullOrWhiteSpace(downloadDir))
            throw new ArgumentException("A download directory is required.", nameof(downloadDir));
        return Path.Combine(downloadDir, StagingFolderName);
    }

    /// <summary>The one true <c>.part</c> path for a track hash, inside the staging directory.</summary>
    public static string GetPartPath(string downloadDir, string hash) =>
        Path.Combine(GetStagingDirectory(downloadDir), hash + PartFileGuard.PartSuffix);
}
