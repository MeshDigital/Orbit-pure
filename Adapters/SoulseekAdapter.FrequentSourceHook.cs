using System;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Services;

namespace SLSKDONET.Services;

/// <summary>
/// Hook helpers for the opt-in Frequent Sources feature.
/// Keep this local-only: do not capture IPs, track IDs, or playlist contents here.
/// </summary>
public sealed partial class SoulseekAdapter
{
    internal static async Task TryRecordFrequentSourceDownloadAsync(
        FrequentSourceService? frequentSourceService,
        string username,
        string remoteFilename,
        long? expectedSize,
        long observedBytes,
        DateTime completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var folderPath = TryExtractRemoteFolderPath(remoteFilename);
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        var downloadedBytes = expectedSize.GetValueOrDefault(Math.Max(observedBytes, 0));
        await OnTransferCompletedForFrequentSourcesAsync(
            frequentSourceService,
            username,
            folderPath,
            downloadedBytes,
            completedAtUtc,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task OnTransferCompletedForFrequentSourcesAsync(
        FrequentSourceService? frequentSourceService,
        string username,
        string folderPath,
        long bytesDownloaded,
        DateTime completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (frequentSourceService is null)
            return;

        await frequentSourceService.UpsertAsync(
            username,
            folderPath,
            bytesDownloaded,
            completedAtUtc,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string? TryExtractRemoteFolderPath(string? remoteFilename)
    {
        if (string.IsNullOrWhiteSpace(remoteFilename))
            return null;

        // Soulseek paths commonly use backslashes, but normalize forward slashes as well.
        var normalized = remoteFilename
            .Trim()
            .Replace('/', '\\');

        var idx = normalized.LastIndexOf('\\');
        if (idx <= 0)
            return null;

        var folderPath = normalized[..idx].Trim();
        return folderPath.Length == 0 ? null : folderPath;
    }
}
