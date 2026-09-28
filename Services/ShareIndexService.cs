using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;

namespace SLSKDONET.Services;

public sealed record ShareIndexEntry(string LocalPath, long Size);

/// <summary>
/// Builds and caches a virtual-path -> local-file index from the configured share folders.
/// This is the data source behind ORBIT's incoming Soulseek browse/search/directory-contents
/// resolvers, download-enqueue validation and the share counts announced to the server.
///
/// Rules (audited 2026-09-29 after a peer's leech check flagged this account):
/// <list type="bullet">
/// <item>Only music files are shared — not in-progress <c>.part</c> downloads, artwork, notes or
/// system files (all of which were shared before, including half-finished downloads).</item>
/// <item>Virtual paths start at a short alias per shared root ("Music\Artist\Track.flac"), never
/// the local path — the full "C:\Users\&lt;name&gt;\…" path used to be what peers saw, and what
/// incoming searches matched against (a search for the Windows user name matched every file).</item>
/// <item>A root nested inside another shared root is indexed once, not twice.</item>
/// </list>
/// Virtual paths are backslash-separated (the Soulseek convention) and are only ever produced by
/// this service's own enumeration — a peer-supplied path can only resolve to a file we indexed.
/// </summary>
public sealed class ShareIndexService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    // Separate, shorter TTL for the folder *list* — ResolveShareFolders() runs on every incoming
    // browse/search/download-enqueue request via EnsureFresh()'s fingerprint check.
    private static readonly TimeSpan FolderListCacheInterval = TimeSpan.FromSeconds(30);

    /// <summary>Audio formats worth sharing on Soulseek.</summary>
    public static readonly IReadOnlySet<string> SharedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aiff", ".aif", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".alac", ".ape", ".wv",
    };

    private readonly AppConfig _config;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly ILogger<ShareIndexService> _logger;
    private readonly object _refreshLock = new();

    private volatile Dictionary<string, ShareIndexEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private int _directoryCount;
    private string _lastFingerprint = string.Empty;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    private string[] _cachedFolders = Array.Empty<string>();
    private DateTime _foldersCachedAtUtc = DateTime.MinValue;

    /// <summary>Raised after a rebuild that changed the shared file or folder count (not on every rebuild).</summary>
    public event EventHandler<(int Files, int Directories)>? CountsChanged;

    public ShareIndexService(AppConfig config, IDbContextFactory<AppDbContext>? dbFactory, ILogger<ShareIndexService> logger)
    {
        _config = config;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public int FileCount => _index.Count;

    /// <summary>Distinct folders a peer sees when browsing — what the server should be told, not the number of shared roots.</summary>
    public int DirectoryCount => _directoryCount;

    public void Invalidate() => _lastRefreshUtc = DateTime.MinValue;

    public void EnsureFresh()
    {
        var folders = ResolveShareFoldersCached();
        var fingerprint = string.Join("|", folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

        if (IsFresh(fingerprint))
            return;

        (int Files, int Dirs) before, after;
        lock (_refreshLock)
        {
            if (IsFresh(fingerprint))
                return;

            before = (_index.Count, _directoryCount);
            var index = BuildIndex(folders);
            _directoryCount = index.Keys.Select(GetVirtualDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _index = index;
            _lastFingerprint = fingerprint;
            _lastRefreshUtc = DateTime.UtcNow;
            after = (_index.Count, _directoryCount);
        }

        if (before != after)
        {
            _logger.LogInformation("Share index: {Files} music file(s) in {Dirs} folder(s) across {Roots} shared root(s)", after.Files, after.Dirs, folders.Length);
            CountsChanged?.Invoke(this, (after.Files, after.Dirs));
        }
    }

    private bool IsFresh(string fingerprint)
        => string.Equals(fingerprint, _lastFingerprint, StringComparison.OrdinalIgnoreCase)
           && DateTime.UtcNow - _lastRefreshUtc < RefreshInterval;

    private Dictionary<string, ShareIndexEntry> BuildIndex(IReadOnlyList<string> folders)
    {
        var index = new Dictionary<string, ShareIndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, alias) in AssignAliases(folders))
        {
            try
            {
                var rootInfo = new DirectoryInfo(root);
                foreach (var file in rootInfo.EnumerateFiles("*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                         }))
                {
                    if (!IsShareable(file)) continue;
                    var relative = Path.GetRelativePath(rootInfo.FullName, file.FullName);
                    var virtualPath = ToVirtualPath(Path.Combine(alias, relative));
                    index.TryAdd(virtualPath, new ShareIndexEntry(file.FullName, file.Length));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index share folder {Folder}", root);
            }
        }
        return index;
    }

    /// <summary>Music file, not empty, not an in-progress download.</summary>
    public static bool IsShareable(FileInfo file) =>
        SharedExtensions.Contains(file.Extension) && file.Length > 0;

    /// <summary>
    /// Gives each shared root a short, unique virtual name (its folder name, numbered on clashes)
    /// and drops roots already covered by another shared root. Public for tests.
    /// </summary>
    public static IReadOnlyList<(string Root, string Alias)> AssignAliases(IEnumerable<string> folders)
    {
        var roots = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Length)
            .ToList();

        var kept = new List<string>();
        foreach (var root in roots)
            if (!kept.Any(parent => root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                kept.Add(root);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();
        foreach (var root in kept)
        {
            var name = Path.GetFileName(root);
            if (string.IsNullOrWhiteSpace(name)) name = "Music"; // a drive root like "D:\"
            var alias = name;
            for (int i = 2; !used.Add(alias); i++) alias = $"{name} ({i})";
            result.Add((root, alias));
        }
        return result;
    }

    private string[] ResolveShareFoldersCached()
    {
        if (DateTime.UtcNow - _foldersCachedAtUtc < FolderListCacheInterval)
            return _cachedFolders;

        _cachedFolders = ResolveShareFolders();
        _foldersCachedAtUtc = DateTime.UtcNow;
        return _cachedFolders;
    }

    /// <summary>
    /// Every folder ORBIT shares: all enabled Library Sources, plus the legacy single "Shared
    /// Folder" and the download folder. Queries the DB directly since this runs from the Soulseek
    /// serving pipeline, independent of whether the Library Sources page has been opened.
    /// </summary>
    public string[] ResolveShareFolders()
    {
        var folders = new List<string>();

        if (_dbFactory != null)
        {
            try
            {
                using var context = _dbFactory.CreateDbContext();
                var libraryFolders = context.LibraryFolders
                    .Where(f => f.IsEnabled)
                    .Select(f => f.FolderPath)
                    .ToList();

                folders.AddRange(libraryFolders.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Library Sources for share-folder resolution");
            }
        }

        if (!string.IsNullOrWhiteSpace(_config.SharedFolderPath) && Directory.Exists(_config.SharedFolderPath))
            folders.Add(_config.SharedFolderPath);

        if (!string.IsNullOrWhiteSpace(_config.DownloadDirectory) && Directory.Exists(_config.DownloadDirectory))
            folders.Add(_config.DownloadDirectory);

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ToVirtualPath(string path)
        => path.Replace('/', '\\');

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

    /// <summary>Simple AND-of-terms / NOT-of-exclusions substring match against the virtual path.</summary>
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
        var extension = Path.GetExtension(name).TrimStart('.');
        return new Soulseek.File(1, name, entry.Size, extension, Enumerable.Empty<Soulseek.FileAttribute>());
    }
}
