using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Library;

public sealed class LargestFileRowViewModel
{
    public string Artist { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string SizeDisplay => LibraryHealthViewModel.FormatBytes(SizeBytes);
}

public sealed class FormatBreakdownRowViewModel
{
    public string Format { get; init; } = string.Empty;
    public int TrackCount { get; init; }
    public long TotalBytes { get; init; }
    public string TotalSizeDisplay => LibraryHealthViewModel.FormatBytes(TotalBytes);
}

public sealed class UnplayedTrackRowViewModel
{
    public string Artist { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public DateTime? LastPlayedAt { get; init; }
    public string LastPlayedDisplay => LastPlayedAt?.ToString("yyyy-MM-dd") ?? "Never";
}

public sealed class UntrackedFileRowViewModel
{
    public string FilePath { get; init; } = string.Empty;
    public Guid FolderId { get; init; }
    public string FolderPath { get; init; } = string.Empty;
}

/// <summary>
/// Backs the Library Health panel: real disk-usage reporting (today's only signal is raw
/// drive free/total space), an "unplayed in N days" report (PlayCount/LastPlayedAt already exist
/// on LibraryEntryEntity but back no report anywhere), and disk→DB orphan detection — the missing
/// complementary direction to the existing (until now unreachable) Orphaned Tracks panel, which
/// only finds DB rows whose file is gone, never files on disk the DB doesn't know about.
/// Every section is computed on demand (Refresh), not on panel open, since walking every resolved
/// file path for size is real I/O for a large library.
/// </summary>
public sealed class LibraryHealthViewModel : ReactiveObject
{
    private const int LargestFilesLimit = 20;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".aiff", ".aif", ".wma", ".ape",
    };

    private readonly ILogger<LibraryHealthViewModel> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly LibraryFolderScannerService _scannerService;
    private readonly IFileInteractionService? _fileInteractionService;

    public ObservableCollection<FormatBreakdownRowViewModel> FormatBreakdown { get; } = new();
    public ObservableCollection<LargestFileRowViewModel> LargestFiles { get; } = new();
    public ObservableCollection<UnplayedTrackRowViewModel> UnplayedTracks { get; } = new();
    public ObservableCollection<UntrackedFileRowViewModel> UntrackedFiles { get; } = new();

    private long _totalLibraryBytes;
    public long TotalLibraryBytes
    {
        get => _totalLibraryBytes;
        private set { this.RaiseAndSetIfChanged(ref _totalLibraryBytes, value); this.RaisePropertyChanged(nameof(TotalLibrarySizeDisplay)); }
    }
    public string TotalLibrarySizeDisplay => FormatBytes(TotalLibraryBytes);

    private bool _isDiskUsageLoading;
    public bool IsDiskUsageLoading { get => _isDiskUsageLoading; private set => this.RaiseAndSetIfChanged(ref _isDiskUsageLoading, value); }

    private bool _isUnplayedLoading;
    public bool IsUnplayedLoading { get => _isUnplayedLoading; private set => this.RaiseAndSetIfChanged(ref _isUnplayedLoading, value); }

    private bool _isUntrackedLoading;
    public bool IsUntrackedLoading { get => _isUntrackedLoading; private set => this.RaiseAndSetIfChanged(ref _isUntrackedLoading, value); }

    private int _unplayedThresholdDays = 90;
    public int UnplayedThresholdDays
    {
        get => _unplayedThresholdDays;
        set => this.RaiseAndSetIfChanged(ref _unplayedThresholdDays, value);
    }

    public ICommand RefreshDiskUsageCommand { get; }
    public ICommand RefreshUnplayedCommand { get; }
    public ICommand RefreshUntrackedCommand { get; }
    public ICommand RevealFileCommand { get; }
    public ICommand ScanFolderCommand { get; }
    public ICommand SetUnplayedThresholdCommand { get; }

    public LibraryHealthViewModel(
        ILogger<LibraryHealthViewModel> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        LibraryFolderScannerService scannerService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _scannerService = scannerService;
        _fileInteractionService = serviceProvider.GetService(typeof(IFileInteractionService)) as IFileInteractionService;

        RefreshDiskUsageCommand = new AsyncRelayCommand(RefreshDiskUsageAsync);
        RefreshUnplayedCommand = new AsyncRelayCommand(RefreshUnplayedAsync);
        RefreshUntrackedCommand = new AsyncRelayCommand(RefreshUntrackedAsync);
        RevealFileCommand = new RelayCommand<string?>(path =>
        {
            if (!string.IsNullOrEmpty(path)) _fileInteractionService?.RevealFileInExplorer(path);
        });
        ScanFolderCommand = new AsyncRelayCommand<UntrackedFileRowViewModel?>(ScanFolderForRowAsync);
        SetUnplayedThresholdCommand = new RelayCommand<string?>(days =>
        {
            if (int.TryParse(days, out var parsed))
            {
                UnplayedThresholdDays = parsed;
                _ = RefreshUnplayedAsync();
            }
        });
    }

    /// <summary>Kicks off all three sections — called when the panel is opened.</summary>
    public void RefreshAll()
    {
        _ = RefreshDiskUsageAsync();
        _ = RefreshUnplayedAsync();
        _ = RefreshUntrackedAsync();
    }

    private async Task RefreshDiskUsageAsync()
    {
        IsDiskUsageLoading = true;
        try
        {
            await using var context = _dbFactory.CreateDbContext();
            var paths = await context.LibraryEntries
                .Where(e => !string.IsNullOrEmpty(e.FilePath))
                .Select(e => new { e.FilePath, e.Artist, e.Title, e.Format })
                .ToListAsync();

            var (breakdown, largest, total) = await Task.Run(() =>
            {
                var byFormat = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
                var files = new List<LargestFileRowViewModel>();
                long totalBytes = 0;

                foreach (var row in paths)
                {
                    long size;
                    try
                    {
                        var info = new FileInfo(row.FilePath);
                        if (!info.Exists) continue;
                        size = info.Length;
                    }
                    catch
                    {
                        continue;
                    }

                    totalBytes += size;
                    var format = string.IsNullOrWhiteSpace(row.Format) ? Path.GetExtension(row.FilePath).TrimStart('.').ToUpperInvariant() : row.Format.ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(format)) format = "UNKNOWN";

                    if (!byFormat.TryGetValue(format, out var agg)) agg = (0, 0);
                    byFormat[format] = (agg.Count + 1, agg.Bytes + size);

                    files.Add(new LargestFileRowViewModel { Artist = row.Artist, Title = row.Title, FilePath = row.FilePath, SizeBytes = size });
                }

                var breakdownRows = byFormat
                    .Select(kv => new FormatBreakdownRowViewModel { Format = kv.Key, TrackCount = kv.Value.Count, TotalBytes = kv.Value.Bytes })
                    .OrderByDescending(r => r.TotalBytes)
                    .ToList();

                var largestRows = files.OrderByDescending(f => f.SizeBytes).Take(LargestFilesLimit).ToList();

                return (breakdownRows, largestRows, totalBytes);
            });

            TotalLibraryBytes = total;
            FormatBreakdown.Clear();
            foreach (var row in breakdown) FormatBreakdown.Add(row);
            LargestFiles.Clear();
            foreach (var row in largest) LargestFiles.Add(row);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute library disk usage");
        }
        finally
        {
            IsDiskUsageLoading = false;
        }
    }

    private async Task RefreshUnplayedAsync()
    {
        IsUnplayedLoading = true;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-UnplayedThresholdDays);

            await using var context = _dbFactory.CreateDbContext();
            var rows = await context.LibraryEntries
                .Where(e => e.PlayCount == 0 || (e.LastPlayedAt != null && e.LastPlayedAt < cutoff))
                .OrderBy(e => e.LastPlayedAt)
                .Take(200)
                .Select(e => new UnplayedTrackRowViewModel
                {
                    Artist = e.Artist,
                    Title = e.Title,
                    FilePath = e.FilePath,
                    LastPlayedAt = e.LastPlayedAt,
                })
                .ToListAsync();

            UnplayedTracks.Clear();
            foreach (var row in rows) UnplayedTracks.Add(row);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute unplayed tracks report");
        }
        finally
        {
            IsUnplayedLoading = false;
        }
    }

    private async Task RefreshUntrackedAsync()
    {
        IsUntrackedLoading = true;
        try
        {
            await using var context = _dbFactory.CreateDbContext();
            var folders = await context.LibraryFolders.Where(f => f.IsEnabled).ToListAsync();
            var knownPaths = new HashSet<string>(
                await context.LibraryEntries.Select(e => e.FilePath).ToListAsync(),
                StringComparer.OrdinalIgnoreCase);

            var untracked = await Task.Run(() =>
            {
                var results = new List<UntrackedFileRowViewModel>();
                foreach (var folder in folders)
                {
                    if (!Directory.Exists(folder.FolderPath)) continue;

                    IEnumerable<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(folder.FolderPath, "*.*", SearchOption.AllDirectories)
                            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to enumerate folder for untracked-file check: {Path}", folder.FolderPath);
                        continue;
                    }

                    foreach (var file in files)
                    {
                        if (!knownPaths.Contains(file))
                        {
                            results.Add(new UntrackedFileRowViewModel { FilePath = file, FolderId = folder.Id, FolderPath = folder.FolderPath });
                            if (results.Count >= 500) return results; // safety cap for a very large untracked backlog
                        }
                    }
                }
                return results;
            });

            UntrackedFiles.Clear();
            foreach (var row in untracked) UntrackedFiles.Add(row);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute untracked-files report");
        }
        finally
        {
            IsUntrackedLoading = false;
        }
    }

    private async Task ScanFolderForRowAsync(UntrackedFileRowViewModel? row)
    {
        if (row == null) return;
        try
        {
            await _scannerService.ScanFolderAsync(row.FolderId);
            await RefreshUntrackedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan Now failed for folder {FolderId}", row.FolderId);
        }
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        // Invariant culture: a locale using ',' as the decimal separator (e.g. nl-NL) would
        // otherwise render "512,0 KB", which reads as a typo/thousands separator, not a decimal.
        return $"{size.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }
}
