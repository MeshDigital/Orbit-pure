using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>One cue row from Rekordbox's master.db (djmdCue), positions in milliseconds.</summary>
public sealed record RekordboxDbCue(int Kind, long InMsec, long OutMsec, int ColorTableIndex, string? Comment)
{
    /// <summary>Hot cue pad 0–7 (A–H), or -1 for a memory cue.</summary>
    public int Slot => RekordboxLibraryService.SlotForKind(Kind);
    public bool IsLoop => OutMsec > InMsec && OutMsec > 0;
}

/// <summary>One Rekordbox collection track that has cues.</summary>
public sealed record RekordboxDbTrack(string Id, string? Path, string? Title, string? Artist, double Bpm, IReadOnlyList<RekordboxDbCue> Cues);

public sealed record RekordboxReadResult(bool Success, string Message, IReadOnlyList<RekordboxDbTrack> Tracks)
{
    public static RekordboxReadResult Fail(string message) => new(false, message, Array.Empty<RekordboxDbTrack>());
}

/// <summary>
/// Reads the hot and memory cues from Rekordbox 6/7's own library database (master.db) — every
/// hand-placed cue, not just the ones that happen to be in a USB export. master.db is SQLCipher-
/// encrypted, so the reading happens in the RekordboxBridge companion process (its own SQLCipher
/// engine; ORBIT's SQLite engine is untouched). Read-only: nothing in Rekordbox changes.
///
/// Cue encoding, verified against a real Rekordbox 7.2.14 library (589 cues on 156 tracks):
/// Kind 0 = memory cue; hot cues A–H = Kind 1, 2, 3, 5, 6, 7, 8, 9 (4 is never used — every track
/// with eight hot cues has exactly that set); InFrame = InMsec × 0.15 (1/150 s frames).
/// </summary>
public sealed class RekordboxLibraryService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly ILogger<RekordboxLibraryService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime Stamp, RekordboxReadResult Result)? _cache;

    public RekordboxLibraryService(ILogger<RekordboxLibraryService> logger)
    {
        _logger = logger;
    }

    /// <summary>Tests and portable setups override where the bridge lives.</summary>
    internal string BridgePath { get; set; } = DefaultBridgePath();

    /// <summary>Tests point this at a fixture; otherwise Rekordbox's configured database.</summary>
    internal string? DatabasePathOverride { get; set; }

    public static int SlotForKind(int kind) => kind switch
    {
        1 => 0, 2 => 1, 3 => 2, 5 => 3, 6 => 4, 7 => 5, 8 => 6, 9 => 7,
        _ => -1,
    };

    /// <summary>Rekordbox's own record of its database (rekordboxAgent options.json "db-path"),
    /// else the default location; null when no Rekordbox 6/7 library exists.</summary>
    public string? DatabasePath => DatabasePathOverride ?? FindDatabase();

    public bool IsAvailable => DatabasePath != null && File.Exists(BridgePath);

    /// <summary>All Rekordbox tracks with cues. Cached until master.db (or its WAL) changes.</summary>
    public async Task<RekordboxReadResult> ReadCuesAsync(CancellationToken ct = default)
    {
        var db = DatabasePath;
        if (db == null) return RekordboxReadResult.Fail("No Rekordbox 6/7 library found on this computer.");
        if (!File.Exists(BridgePath)) return RekordboxReadResult.Fail("The Rekordbox reader isn't installed with this build of ORBIT.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stamp = LastWrite(db);
            if (_cache is { } c && c.Stamp == stamp) return c.Result;

            var (exit, stdout, stderr) = await RunBridgeAsync("cues", db, ct).ConfigureAwait(false);
            RekordboxReadResult result = exit switch
            {
                0 => Parse(stdout),
                2 => RekordboxReadResult.Fail("Rekordbox's library file wasn't found."),
                3 => RekordboxReadResult.Fail("Couldn't unlock Rekordbox's library. This Rekordbox version may use a different key."),
                _ => RekordboxReadResult.Fail($"Couldn't read Rekordbox's library: {FirstLine(stderr)}"),
            };
            if (!result.Success) _logger.LogWarning("Rekordbox library read failed (exit {Exit}): {Error}", exit, FirstLine(stderr));
            else _logger.LogInformation("Rekordbox library read: {Tracks} tracks with cues", result.Tracks.Count);
            _cache = result.Success ? (stamp, result) : null;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The Rekordbox track for a file ORBIT knows, matched by path (case- and separator-insensitive).</summary>
    public async Task<RekordboxDbTrack?> FindByPathAsync(string? filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var read = await ReadCuesAsync(ct).ConfigureAwait(false);
        if (!read.Success) return null;
        string key = NormalizePath(filePath);
        return read.Tracks.FirstOrDefault(t => t.Path != null && NormalizePath(t.Path) == key);
    }

    internal static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    internal static RekordboxReadResult Parse(string json)
    {
        var tracks = new List<RekordboxDbTrack>();
        using var doc = JsonDocument.Parse(json);
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var cues = t.GetProperty("cues").EnumerateArray().Select(c => new RekordboxDbCue(
                c.GetProperty("kind").GetInt32(),
                c.GetProperty("inMsec").GetInt64(),
                c.GetProperty("outMsec").GetInt64(),
                c.GetProperty("colorTableIndex").GetInt32(),
                c.GetProperty("comment").GetString())).ToList();
            tracks.Add(new RekordboxDbTrack(
                t.GetProperty("id").GetString() ?? "",
                t.GetProperty("path").GetString(),
                t.GetProperty("title").GetString(),
                t.GetProperty("artist").GetString(),
                t.GetProperty("bpm").GetDouble(),
                cues));
        }
        return new RekordboxReadResult(true, $"{tracks.Count} tracks with cues", tracks);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunBridgeAsync(string command, string db, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(BridgePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(command);
        psi.ArgumentList.Add("--db");
        psi.ArgumentList.Add(db);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the Rekordbox reader.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException)
            {
                // intentional: the reader exited on its own between the timeout and the kill.
            }
            if (ct.IsCancellationRequested) throw;
            return (4, "", "Reading Rekordbox's library took too long.");
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string? FindDatabase()
    {
        try
        {
            var options = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Pioneer", "rekordboxAgent", "storage", "options.json");
            if (OperatingSystem.IsMacOS())
                options = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Pioneer", "rekordboxAgent", "storage", "options.json");
            if (File.Exists(options))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(options));
                if (doc.RootElement.TryGetProperty("options", out var opts))
                    foreach (var o in opts.EnumerateArray())
                        if (o.ValueKind == JsonValueKind.Array && o.GetArrayLength() >= 2 && o[0].GetString() == "db-path"
                            && o[1].GetString() is { } configured && File.Exists(configured))
                            return configured;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Couldn't read Rekordbox's options.json; trying the default location");
        }

        var fallback = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Pioneer", "rekordbox", "master.db")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pioneer", "rekordbox", "master.db");
        return File.Exists(fallback) ? fallback : null;
    }

    private static string DefaultBridgePath() => Path.Combine(AppContext.BaseDirectory, "Tools", "RekordboxBridge",
        OperatingSystem.IsWindows() ? "RekordboxBridge.exe" : "RekordboxBridge");

    private static DateTime LastWrite(string db)
    {
        var wal = db + "-wal";
        var a = File.GetLastWriteTimeUtc(db);
        return File.Exists(wal) ? (a > File.GetLastWriteTimeUtc(wal) ? a : File.GetLastWriteTimeUtc(wal)) : a;
    }

    private static string FirstLine(string text) =>
        string.IsNullOrWhiteSpace(text) ? "no details" : text.Trim().Split('\n')[0].Trim();
}
