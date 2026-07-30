using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services;

public sealed record ShareIndexEntry(string LocalPath, long Size);

/// <summary>
/// Builds and caches a virtual-path -> local-file index from the configured share folders.
/// This is the data source behind ORBIT's incoming Soulseek browse/search/directory-contents
/// resolvers and download-enqueue validation — without it, the app announces share counts to
/// the server but has nothing to actually hand back when a peer looks or asks.
/// Virtual paths are always backslash-separated (the Soulseek protocol convention) regardless
/// of host OS, and are never derived from peer-supplied input — only from what this service
/// itself enumerated on disk, so an incoming request can only ever resolve to a file we chose
/// to share.
/// </summary>
public sealed class ShareIndexService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly AppConfig _config;
    private readonly ILogger<ShareIndexService> _logger;
    private readonly object _refreshLock = new();

    private volatile Dictionary<string, ShareIndexEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private string _lastFingerprint = string.Empty;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public ShareIndexService(AppConfig config, ILogger<ShareIndexService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public int FileCount => _index.Count;

    public void Invalidate() => _lastRefreshUtc = DateTime.MinValue;

    public void EnsureFresh()
    {
        var folders = ResolveShareFolders();
        var fingerprint = string.Join("|", folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

        if (IsFresh(fingerprint))
            return;

        lock (_refreshLock)
        {
            if (IsFresh(fingerprint))
                return;

            _index = BuildIndex(folders);
            _lastFingerprint = fingerprint;
            _lastRefreshUtc = DateTime.UtcNow;
        }
    }

    private bool IsFresh(string fingerprint)
        => string.Equals(fingerprint, _lastFingerprint, StringComparison.OrdinalIgnoreCase)
           && DateTime.UtcNow - _lastRefreshUtc < RefreshInterval;

    private Dictionary<string, ShareIndexEntry> BuildIndex(IReadOnlyList<string> folders)
    {
        var index = new Dictionary<string, ShareIndexEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            try
            {
                foreach (var file in new System.IO.DirectoryInfo(folder).EnumerateFiles("*", System.IO.SearchOption.AllDirectories))
                {
                    var virtualPath = ToVirtualPath(file.FullName);
                    index[virtualPath] = new ShareIndexEntry(file.FullName, file.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index share folder {Folder}", folder);
            }
        }

        _logger.LogInformation("Share index rebuilt: {Count} file(s) across {FolderCount} folder(s)", index.Count, folders.Count);
        return index;
    }

    private string[] ResolveShareFolders()
    {
        var folders = new List<string>();

        if (!string.IsNullOrWhiteSpace(_config.SharedFolderPath) && System.IO.Directory.Exists(_config.SharedFolderPath))
            folders.Add(_config.SharedFolderPath);

        if (!string.IsNullOrWhiteSpace(_config.DownloadDirectory) && System.IO.Directory.Exists(_config.DownloadDirectory))
            folders.Add(_config.DownloadDirectory);

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ToVirtualPath(string fullPath)
        => fullPath.Replace('/', '\\');

    private static string GetVirtualDirectory(string virtualPath)
    {
        var idx = virtualPath.LastIndexOf('\\');
        return idx >= 0 ? virtualPath[..idx] : string.Empty;
    }

    private static string GetVirtualFileName(string virtualPath)
    {
        var idx = virtualPath.LastIndexOf('\\');
        return idx >= 0 ? virtualPath[(idx + 1)..] : virtualPath;
    }

    /// <summary>Resolves a peer-supplied filename strictly against the pre-built index — never touches disk with it directly.</summary>
    public bool TryGetEntry(string virtualPath, out ShareIndexEntry? entry)
    {
        EnsureFresh();
        return _index.TryGetValue(virtualPath, out entry);
    }

    public IReadOnlyList<Soulseek.Directory> GetAllDirectories()
    {
        EnsureFresh();
        var index = _index;

        return index
            .GroupBy(kvp => GetVirtualDirectory(kvp.Key), StringComparer.OrdinalIgnoreCase)
            .Select(g => new Soulseek.Directory(g.Key, g.Select(kvp => BuildFile(kvp.Key, kvp.Value, basenameOnly: true))))
            .ToList();
    }

    public Soulseek.Directory? GetDirectory(string virtualDirectoryName)
    {
        EnsureFresh();
        var index = _index;

        var matches = index
            .Where(kvp => string.Equals(GetVirtualDirectory(kvp.Key), virtualDirectoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count == 0
            ? null
            : new Soulseek.Directory(virtualDirectoryName, matches.Select(kvp => BuildFile(kvp.Key, kvp.Value, basenameOnly: true)));
    }

    /// <summary>Simple AND-of-terms / NOT-of-exclusions substring match against the full virtual path.</summary>
    public IReadOnlyList<(string VirtualPath, ShareIndexEntry Entry)> Search(Soulseek.SearchQuery query, int maxResults = 100)
    {
        EnsureFresh();
        var index = _index;

        var terms = query.Terms?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? Array.Empty<string>();
        if (terms.Length == 0)
            return Array.Empty<(string, ShareIndexEntry)>();

        var exclusions = query.Exclusions ?? Array.Empty<string>();
        var results = new List<(string, ShareIndexEntry)>();

        foreach (var kvp in index)
        {
            if (exclusions.Any(x => kvp.Key.Contains(x, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (terms.All(t => kvp.Key.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add((kvp.Key, kvp.Value));
                if (results.Count >= maxResults)
                    break;
            }
        }

        return results;
    }

    public static Soulseek.File BuildFile(string virtualPath, ShareIndexEntry entry, bool basenameOnly)
    {
        var name = basenameOnly ? GetVirtualFileName(virtualPath) : virtualPath;
        var extension = System.IO.Path.GetExtension(name).TrimStart('.');
        return new Soulseek.File(1, name, entry.Size, extension, Enumerable.Empty<Soulseek.FileAttribute>());
    }
}
