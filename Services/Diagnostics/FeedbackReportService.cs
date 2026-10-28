using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Diagnostics;

/// <param name="Note">What the user says happened (optional).</param>
/// <param name="ScreenshotPng">PNG of the main window taken before the feedback dialog opened, or null.</param>
public sealed record FeedbackReportRequest(string? Note, byte[]? ScreenshotPng);

public sealed record FeedbackReport(string ZipPath, long SizeBytes, bool IncludesScreenshot);

/// <summary>
/// Bundles what a tester's bug report needs into one zip they can send over WhatsApp or mail:
/// a screenshot, the last <see cref="LogWindow"/> of the run logs (current and previous run, so a
/// crash right before restarting is covered), basic system / connection info and their note.
/// Secrets and the Windows user folder name are scrubbed from the logs. Nothing is uploaded.
/// </summary>
public sealed class FeedbackReportService
{
    public static readonly TimeSpan LogWindow = TimeSpan.FromMinutes(15);

    /// <summary>How much of a single run log is scanned (from the end) and how much log goes in the zip.</summary>
    private const long MaxScanBytesPerFile = 32L * 1024 * 1024;
    private const int MaxLogChars = 8 * 1024 * 1024;
    private const int RunLogsToScan = 3;
    private const int ReportsToKeep = 10;

    private static readonly Regex TimestampPrefix = new(
        @"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuerySecret = new(
        @"([?&](?:code|access_token|refresh_token|token|client_secret)=)[^&\s""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex KeyValueSecret = new(
        @"\b(password|passwd|pwd|secret|client_?secret|refresh_?token|access_?token|api_?key)(""?\s*[:=]\s*""?)[^\s""',;&]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BearerToken = new(
        @"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<FeedbackReportService> _logger;
    private readonly AppConfig _config;
    private readonly SpotifyAuthService? _spotifyAuth;
    private readonly ISoulseekAdapter? _soulseek;

    public FeedbackReportService(
        ILogger<FeedbackReportService> logger,
        AppConfig config,
        SpotifyAuthService? spotifyAuth = null,
        ISoulseekAdapter? soulseek = null)
    {
        _logger = logger;
        _config = config;
        _spotifyAuth = spotifyAuth;
        _soulseek = soulseek;
    }

    internal string LogDirectory { get; set; } =
        AppContext.GetData("Orbit.LogDirectory") as string
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORBIT", "logs");

    /// <summary>Where finished reports are written (%LocalAppData%\ORBIT\feedback).</summary>
    public string OutputDirectory { get; internal set; } = Path.Combine(OrbitPaths.LocalRoot, "ORBIT", "feedback");

    /// <summary>Local time, matching the Serilog text log's timestamps.</summary>
    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public Task<FeedbackReport> CreateReportAsync(FeedbackReportRequest request, CancellationToken ct = default)
        => Task.Run(() => CreateReport(request, ct), ct);

    private FeedbackReport CreateReport(FeedbackReportRequest request, CancellationToken ct)
    {
        var now = Now();
        Directory.CreateDirectory(OutputDirectory);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(OutputDirectory, $"ORBIT-feedback-{stamp}.zip");
        for (var n = 2; File.Exists(zipPath); n++)
            zipPath = Path.Combine(OutputDirectory, $"ORBIT-feedback-{stamp}-{n}.zip");

        var (logText, logFiles) = CollectRecentLog(now - LogWindow, ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "system.txt", BuildSystemInfo(now, logFiles));
                WriteEntry(zip, "log-last-15-min.txt", logText.Length > 0 ? logText : "(no log lines in the last 15 minutes)");
                if (!string.IsNullOrWhiteSpace(request.Note))
                    WriteEntry(zip, "note.txt", request.Note.Trim());
                if (request.ScreenshotPng is { Length: > 0 } png)
                {
                    using var stream = zip.CreateEntry("screenshot.png", CompressionLevel.NoCompression).Open();
                    stream.Write(png, 0, png.Length);
                }
            }
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }

        PruneOldReports();
        var size = new FileInfo(zipPath).Length;
        _logger.LogInformation("Feedback report created: {Path} ({Bytes} bytes, {Files} log file(s))", zipPath, size, logFiles.Count);
        return new FeedbackReport(zipPath, size, request.ScreenshotPng is { Length: > 0 });
    }

    private (string Text, List<string> Files) CollectRecentLog(DateTime cutoff, CancellationToken ct)
    {
        var files = new List<string>();
        if (!Directory.Exists(LogDirectory)) return (string.Empty, files);

        // run_yyyyMMdd_HHmmss_fff.txt sorts chronologically by name; the previous run matters
        // when the tester restarted after a crash.
        var candidates = Directory.GetFiles(LogDirectory, "run_*.txt")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .TakeLast(RunLogsToScan);

        var sb = new StringBuilder();
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            List<string> lines;
            try
            {
                lines = ReadRecentLines(file, cutoff);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Feedback report: couldn't read log {File}", file);
                continue;
            }
            if (lines.Count == 0) continue;

            files.Add(Path.GetFileName(file));
            sb.Append("===== ").Append(Path.GetFileName(file)).AppendLine(" =====");
            foreach (var line in lines) sb.AppendLine(Redact(line));
            sb.AppendLine();
        }

        var text = sb.ToString();
        if (text.Length > MaxLogChars)
            text = "(older lines trimmed)" + Environment.NewLine + text[^MaxLogChars..];
        return (text, files);
    }

    /// <summary>
    /// Lines logged at or after <paramref name="cutoff"/>. Continuation lines (stack traces,
    /// multi-line messages) follow the timestamped line they belong to.
    /// </summary>
    internal static List<string> ReadRecentLines(string path, DateTime cutoff)
    {
        var result = new List<string>();
        // Serilog keeps the current run's file open for writing.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipPartialLine = false;
        if (stream.Length > MaxScanBytesPerFile)
        {
            stream.Seek(-MaxScanBytesPerFile, SeekOrigin.End);
            skipPartialLine = true;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (skipPartialLine) reader.ReadLine();

        var including = false;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = TimestampPrefix.Match(line);
            if (match.Success
                && DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            {
                including = stamp >= cutoff;
            }
            if (including) result.Add(line);
        }
        return result;
    }

    /// <summary>Removes passwords, tokens, OAuth codes and the Windows user folder name from a log line.</summary>
    internal static string Redact(string line)
    {
        line = QuerySecret.Replace(line, "$1***");
        line = KeyValueSecret.Replace(line, "$1$2***");
        line = BearerToken.Replace(line, "Bearer ***");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3)
            line = line.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return line;
    }

    private string BuildSystemInfo(DateTime now, IReadOnlyList<string> logFiles)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var inv = CultureInfo.InvariantCulture;

        var clientId = _config.SpotifyClientId;
        var clientIdKind = string.IsNullOrWhiteSpace(clientId) ? "not set"
            : clientId == SpotifyAuthService.DefaultClientId ? "ORBIT built-in (development mode: allowlisted accounts only)"
            : "custom";

        var sb = new StringBuilder();
        sb.AppendLine("ORBIT feedback report");
        sb.AppendLine(inv, $"Created:      {now:yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.Id})");
        sb.AppendLine(inv, $"Version:      {version}");
        sb.AppendLine(inv, $"OS:           {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine(inv, $"Runtime:      {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine(inv, $"Culture:      {CultureInfo.CurrentCulture.Name} / UI {CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine(inv, $"Uptime:       {now - process.StartTime:hh\\:mm\\:ss}");
        sb.AppendLine(inv, $"Memory:       {process.WorkingSet64 / (1024 * 1024)} MB working set");
        sb.AppendLine(inv, $"Profile dir:  {(OrbitPaths.IsProfileOverridden ? "overridden" : "default")}");
        sb.AppendLine();
        sb.AppendLine("Connections");
        sb.AppendLine(inv, $"Soulseek connected:   {YesNo(_soulseek?.IsConnected)}");
        sb.AppendLine(inv, $"Spotify signed in:    {YesNo(_spotifyAuth?.IsAuthenticated)}");
        sb.AppendLine(inv, $"Spotify API enabled:  {YesNo(_config.SpotifyUseApi)}");
        sb.AppendLine(inv, $"Spotify Client ID:    {clientIdKind}");
        sb.AppendLine(inv, $"Spotify secret set:   {YesNo(!string.IsNullOrWhiteSpace(_config.SpotifyClientSecret))}");
        sb.AppendLine(inv, $"Spotify redirect URI: {_config.SpotifyRedirectUri}");
        sb.AppendLine();
        sb.AppendLine(inv, $"Log window: last {(int)LogWindow.TotalMinutes} minutes from {(logFiles.Count == 0 ? "(none)" : string.Join(", ", logFiles))}");
        return sb.ToString();
    }

    private static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => "unknown" };

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private void PruneOldReports()
    {
        try
        {
            foreach (var old in Directory.GetFiles(OutputDirectory, "ORBIT-feedback-*.zip")
                         .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                         .Skip(ReportsToKeep))
                File.Delete(old);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Feedback report: couldn't prune old reports in {Dir}", OutputDirectory);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Feedback report: couldn't remove partial report {Path}", path);
        }
    }
}
