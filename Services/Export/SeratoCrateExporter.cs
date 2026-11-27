using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SLSKDONET.Services.Export;

public sealed record SeratoCrateWriteResult(
    bool Success,
    IReadOnlyList<string> WrittenCratePaths,
    int TracksWritten,
    string? Error = null);

/// <summary>
/// Writes Serato DJ binary <c>.crate</c> files (big-endian TLV with UTF-16BE strings) (DX3 #558).
/// Format:
/// - <c>vrsn</c>: <c>"1.0/Serato ScratchLive Crate"</c>
/// - One <c>otrk</c> per track containing <c>ptrk</c> = path relative to the drive root (no drive letter, forward slashes).
/// - Nested playlists use <c>Parent%%Child.crate</c>.
/// - Multi-drive selections produce one crate per drive.
/// - Refuses while Serato DJ is running and backs up any existing crate before overwriting.
/// </summary>
public static class SeratoCrateExporter
{
    public const string CrateVersionString = "1.0/Serato ScratchLive Crate";

    public static bool IsSeratoRunning()
    {
        return Process.GetProcessesByName("Serato DJ Pro").Length > 0
            || Process.GetProcessesByName("Serato DJ Lite").Length > 0
            || Process.GetProcessesByName("Serato DJ").Length > 0;
    }

    /// <summary>
    /// Formats a nested crate filename using Serato's <c>%%</c> separator (<c>Parent%%Child.crate</c>).
    /// </summary>
    public static string FormatCrateFileName(IReadOnlyList<string> hierarchySegments)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = hierarchySegments
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => string.Concat(s.Trim().Split(invalid)).Replace("%%", "_", StringComparison.Ordinal))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var stem = cleaned.Count == 0 ? "ORBIT Crate" : string.Join("%%", cleaned);
        return stem + ".crate";
    }

    /// <summary>
    /// Strips the drive root (e.g. <c>C:\</c> or leading <c>/</c>) and converts separators to <c>/</c>,
    /// matching Serato's <c>ptrk</c> relative-path encoding.
    /// </summary>
    public static (string DriveRoot, string RelativePath) SplitDriveAndRelativePath(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        if (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/')
        {
            var root = normalized[..2].ToUpperInvariant() + Path.DirectorySeparatorChar;
            return (root, normalized[3..]);
        }

        if (normalized.StartsWith('/'))
        {
            return ("/", normalized[1..]);
        }

        return (string.Empty, normalized);
    }

    /// <summary>
    /// Serializes a list of drive-relative track paths into Serato's binary <c>.crate</c> byte stream.
    /// </summary>
    public static byte[] BuildCrateBytes(IReadOnlyList<string> relativeTrackPaths)
    {
        using var ms = new MemoryStream();
        WriteTlv(ms, "vrsn", Encoding.BigEndianUnicode.GetBytes(CrateVersionString));

        foreach (var relPath in relativeTrackPaths)
        {
            if (string.IsNullOrWhiteSpace(relPath)) continue;
            var ptrkPayload = Encoding.BigEndianUnicode.GetBytes(relPath);
            using var otrkBody = new MemoryStream();
            WriteTlv(otrkBody, "ptrk", ptrkPayload);
            WriteTlv(ms, "otrk", otrkBody.ToArray());
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Parses track paths back out of a binary <c>.crate</c> payload (used for round-trip verification).
    /// </summary>
    public static IReadOnlyList<string> ReadCrateTrackPaths(byte[] bytes)
    {
        var results = new List<string>();
        int offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            var tag = Encoding.ASCII.GetString(bytes, offset, 4);
            var len = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8;
            if (len < 0 || offset + len > bytes.Length) break;

            if (tag == "otrk")
            {
                int inner = offset;
                int end = offset + len;
                while (inner + 8 <= end)
                {
                    var innerTag = Encoding.ASCII.GetString(bytes, inner, 4);
                    var innerLen = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(inner + 4, 4));
                    inner += 8;
                    if (innerLen < 0 || inner + innerLen > end) break;
                    if (innerTag == "ptrk")
                    {
                        results.Add(Encoding.BigEndianUnicode.GetString(bytes, inner, innerLen));
                    }
                    inner += innerLen;
                }
            }

            offset += len;
        }
        return results;
    }

    /// <summary>
    /// Groups tracks by drive root and writes one <c>.crate</c> per drive under the resolved
    /// <c>_Serato_/Subcrates</c> folder. Refuses while Serato DJ is running and backs up existing crates.
    /// </summary>
    public static SeratoCrateWriteResult WriteCratesForSelection(
        IReadOnlyList<string> hierarchySegments,
        IReadOnlyList<string> trackFilePaths,
        Func<string, string>? subcratesDirResolver = null,
        Func<bool>? isRunningCheck = null)
    {
        var running = (isRunningCheck ?? IsSeratoRunning)();
        if (running)
        {
            return new SeratoCrateWriteResult(
                false,
                Array.Empty<string>(),
                0,
                "Serato DJ is currently running. Close Serato DJ before writing crates.");
        }

        var byDrive = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in trackFilePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var (driveRoot, rel) = SplitDriveAndRelativePath(path);
            if (!byDrive.TryGetValue(driveRoot, out var list))
            {
                list = new List<string>();
                byDrive[driveRoot] = list;
            }
            list.Add(rel);
        }

        if (byDrive.Count == 0)
        {
            return new SeratoCrateWriteResult(false, Array.Empty<string>(), 0, "No track paths to export.");
        }

        var fileName = FormatCrateFileName(hierarchySegments);
        var written = new List<string>();
        var totalTracks = 0;

        try
        {
            foreach (var (driveRoot, relPaths) in byDrive)
            {
                var targetDir = subcratesDirResolver != null
                    ? subcratesDirResolver(driveRoot)
                    : DefaultSubcratesDirectory(driveRoot);

                Directory.CreateDirectory(targetDir);
                var cratePath = Path.Combine(targetDir, fileName);

                if (File.Exists(cratePath))
                {
                    var backupPath = $"{cratePath}.orbit-backup-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                    File.Copy(cratePath, backupPath, overwrite: true);
                }

                var bytes = BuildCrateBytes(relPaths);
                File.WriteAllBytes(cratePath, bytes);
                written.Add(cratePath);
                totalTracks += relPaths.Count;
            }

            return new SeratoCrateWriteResult(true, written, totalTracks);
        }
        catch (Exception ex)
        {
            return new SeratoCrateWriteResult(false, written, totalTracks, ex.Message);
        }
    }

    private static string DefaultSubcratesDirectory(string driveRoot)
    {
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)) ?? string.Empty;
        if (!string.IsNullOrEmpty(systemRoot) && string.Equals(driveRoot, systemRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "_Serato_", "Subcrates");
        }
        return Path.Combine(driveRoot, "_Serato_", "Subcrates");
    }

    private static void WriteTlv(Stream stream, string tag, byte[] payload)
    {
        var tagBytes = Encoding.ASCII.GetBytes(tag);
        stream.Write(tagBytes, 0, 4);
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, payload.Length);
        stream.Write(lenBuf);
        stream.Write(payload, 0, payload.Length);
    }
}
