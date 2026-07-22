using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class LibraryFolderScannerServiceTests
{
    [Fact]
    public async Task EnsureDefaultFolderAsync_AddsFolder_WhenMissing()
    {
        var scanner = new LibraryFolderScannerService(
            NullLogger<LibraryFolderScannerService>.Instance,
            null!,
            null!,
            null!);

        var testFolder = CreateTempFolder();

        try
        {
            await RemoveLibraryFolderRowsAsync(testFolder);

            await scanner.EnsureDefaultFolderAsync(testFolder);

            var rows = await GetMatchingLibraryFolderRowsAsync(testFolder);
            Assert.Single(rows);
            Assert.True(rows[0].IsEnabled);
        }
        finally
        {
            await RemoveLibraryFolderRowsAsync(testFolder);
            DeleteTempFolder(testFolder);
        }
    }

    [Fact]
    public async Task EnsureDefaultFolderAsync_DoesNotDuplicate_WithTrailingSeparatorVariant()
    {
        var scanner = new LibraryFolderScannerService(
            NullLogger<LibraryFolderScannerService>.Instance,
            null!,
            null!,
            null!);

        var testFolder = CreateTempFolder();
        var withTrailing = testFolder + Path.DirectorySeparatorChar;

        try
        {
            await RemoveLibraryFolderRowsAsync(testFolder);

            await scanner.EnsureDefaultFolderAsync(withTrailing);
            await scanner.EnsureDefaultFolderAsync(testFolder);

            var rows = await GetMatchingLibraryFolderRowsAsync(testFolder);
            Assert.Single(rows);
        }
        finally
        {
            await RemoveLibraryFolderRowsAsync(testFolder);
            DeleteTempFolder(testFolder);
        }
    }

    [Fact]
    public async Task EnsureDefaultFolderAsync_DoesNotDuplicate_WithCaseVariantOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var scanner = new LibraryFolderScannerService(
            NullLogger<LibraryFolderScannerService>.Instance,
            null!,
            null!,
            null!);

        var testFolder = CreateTempFolder();
        var lowerPath = testFolder.ToLowerInvariant();
        var upperPath = testFolder.ToUpperInvariant();

        try
        {
            await RemoveLibraryFolderRowsAsync(testFolder);

            await scanner.EnsureDefaultFolderAsync(lowerPath);
            await scanner.EnsureDefaultFolderAsync(upperPath);

            var rows = await GetMatchingLibraryFolderRowsAsync(testFolder);
            Assert.Single(rows);
        }
        finally
        {
            await RemoveLibraryFolderRowsAsync(testFolder);
            DeleteTempFolder(testFolder);
        }
    }

    [Fact]
    public async Task EnsureDefaultFolderAsync_DoesNotDuplicate_WithRelativePathVariant()
    {
        var scanner = new LibraryFolderScannerService(
            NullLogger<LibraryFolderScannerService>.Instance,
            null!,
            null!,
            null!);

        var testFolder = CreateTempFolder();
        var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), testFolder);

        try
        {
            await RemoveLibraryFolderRowsAsync(testFolder);

            await scanner.EnsureDefaultFolderAsync(relativePath);
            await scanner.EnsureDefaultFolderAsync(testFolder);

            var rows = await GetMatchingLibraryFolderRowsAsync(testFolder);
            Assert.Single(rows);
        }
        finally
        {
            await RemoveLibraryFolderRowsAsync(testFolder);
            DeleteTempFolder(testFolder);
        }
    }

    [Fact]
    public async Task EnsureDefaultFolderAsync_ConsolidatesDuplicateRows_AndEnablesPrimary()
    {
        var scanner = new LibraryFolderScannerService(
            NullLogger<LibraryFolderScannerService>.Instance,
            null!,
            null!,
            null!);

        var testFolder = CreateTempFolder();
        var duplicateVariant = testFolder + Path.DirectorySeparatorChar;

        try
        {
            await RemoveLibraryFolderRowsAsync(testFolder);

            await InsertLibraryFolderRowAsync(testFolder, isEnabled: false, DateTime.UtcNow.AddMinutes(-2));
            await InsertLibraryFolderRowAsync(duplicateVariant, isEnabled: true, DateTime.UtcNow.AddMinutes(-1));

            await scanner.EnsureDefaultFolderAsync(testFolder);

            var rows = await GetMatchingLibraryFolderRowsAsync(testFolder);
            Assert.Single(rows);
            Assert.True(rows[0].IsEnabled);
            Assert.Equal(NormalizePath(testFolder), NormalizePath(rows[0].FolderPath));
        }
        finally
        {
            await RemoveLibraryFolderRowsAsync(testFolder);
            DeleteTempFolder(testFolder);
        }
    }

    [Fact]
    public async Task EnsureDefaultFolderAsync_EnablesExistingDisabledFolder()
    {
        var scanner = new LibraryFolderScannerService(
            NullLogger<LibraryFolderScannerService>.Instance,
            null!,
            null!,
            null!);

        var testFolder = CreateTempFolder();

        try
        {
            await RemoveLibraryFolderRowsAsync(testFolder);
            await InsertLibraryFolderRowAsync(testFolder, isEnabled: false, DateTime.UtcNow);

            await scanner.EnsureDefaultFolderAsync(testFolder);

            var rows = await GetMatchingLibraryFolderRowsAsync(testFolder);
            Assert.Single(rows);
            Assert.True(rows[0].IsEnabled);
        }
        finally
        {
            await RemoveLibraryFolderRowsAsync(testFolder);
            DeleteTempFolder(testFolder);
        }
    }

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "orbit-folder-scanner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteTempFolder(string folder)
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    private static async Task RemoveLibraryFolderRowsAsync(string path)
    {
        var normalized = NormalizePath(path);
        var comparer = GetPathComparer();

        await using var context = new AppDbContext();
        var rows = context.LibraryFolders
            .Where(row => !string.IsNullOrWhiteSpace(row.FolderPath))
            .ToList()
            .Where(row => comparer.Equals(NormalizePath(row.FolderPath), normalized))
            .ToList();

        if (rows.Count == 0)
            return;

        context.LibraryFolders.RemoveRange(rows);
        await context.SaveChangesAsync();
    }

    private static async Task<SLSKDONET.Data.Entities.LibraryFolderEntity[]> GetMatchingLibraryFolderRowsAsync(string path)
    {
        var normalized = NormalizePath(path);
        var comparer = GetPathComparer();

        await using var context = new AppDbContext();
        var rows = context.LibraryFolders
            .Where(row => !string.IsNullOrWhiteSpace(row.FolderPath))
            .ToList()
            .Where(row => comparer.Equals(NormalizePath(row.FolderPath), normalized))
            .ToArray();

        return rows;
    }

    private static async Task InsertLibraryFolderRowAsync(string path, bool isEnabled, DateTime addedAt)
    {
        await using var context = new AppDbContext();
        context.LibraryFolders.Add(new SLSKDONET.Data.Entities.LibraryFolderEntity
        {
            FolderPath = path,
            IsEnabled = isEnabled,
            AddedAt = addedAt
        });

        await context.SaveChangesAsync();
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}