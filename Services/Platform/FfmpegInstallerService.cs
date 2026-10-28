using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Platform;

public sealed record FfmpegInstallResult(bool Success, string Message, string? Path = null);

/// <summary>
/// Installs FFmpeg without the user doing anything: a pinned, checksum-verified Windows build
/// (gyan.dev "essentials", mirrored on GitHub) is downloaded once into %LocalAppData%\ORBIT\tools\ffmpeg,
/// which <c>AudioIngestionPipeline.ResolveFfmpegPath</c> searches. Only ffmpeg.exe, ffprobe.exe and
/// the licence are kept. Linux and macOS get their package manager's FFmpeg instead (installing
/// system packages needs the user's password, so ORBIT only tells them the command).
/// </summary>
public sealed class FfmpegInstallerService
{
    public const string Version = "9.0.2";
    public const string DownloadUrl = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
    /// <summary>Matches the checksum gyan.dev publishes next to the build.</summary>
    public const string Sha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";
    private const long ExpectedSizeBytes = 114_768_076;

    private static readonly string[] KeptEntries = { "bin/ffmpeg.exe", "bin/ffprobe.exe", "LICENSE" };

    private readonly ILogger<FfmpegInstallerService> _logger;
    private readonly NativeDependencyHealthService? _health;
    private readonly IEventBus? _eventBus;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public FfmpegInstallerService(
        ILogger<FfmpegInstallerService> logger,
        NativeDependencyHealthService? health = null,
        IEventBus? eventBus = null)
    {
        _logger = logger;
        _health = health;
        _eventBus = eventBus;
    }

    /// <summary>Own client: the 115 MB download outlives a shared client's default timeout.</summary>
    internal HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromMinutes(15) };

    public static string InstallDirectory => Path.Combine(OrbitPaths.LocalRoot, "ORBIT", "tools", "ffmpeg");
    public static string ManagedFfmpegPath => Path.Combine(InstallDirectory, "ffmpeg.exe");

    /// <summary>Where this instance installs; tests point it at a temp folder instead of AppData.</summary>
    internal string TargetDirectory { get; set; } = InstallDirectory;
    private string TargetFfmpegPath => Path.Combine(TargetDirectory, "ffmpeg.exe");

    /// <summary>Platforms ORBIT can install FFmpeg on by itself.</summary>
    public static bool CanAutoInstall => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>What a Linux/macOS user should run instead.</summary>
    public static string ManualInstallHint =>
        OperatingSystem.IsMacOS() ? "Install it with Homebrew: brew install ffmpeg"
        : OperatingSystem.IsLinux() ? "Install it with your package manager, e.g. sudo apt install ffmpeg"
        : "Download FFmpeg from ffmpeg.org and put ffmpeg on your PATH.";

    /// <summary>True while a download is running (the setup wizard and startup can both ask).</summary>
    public bool IsInstalling => _installLock.CurrentCount == 0;

    /// <summary>
    /// Startup: checks FFmpeg (and Essentia) and, when FFmpeg is missing on a supported PC,
    /// installs it in the background with a toast before and after. Never throws.
    /// </summary>
    public async Task EnsureInstalledAsync(CancellationToken ct = default)
    {
        try
        {
            if (_health != null)
            {
                await _health.CheckHealthAsync().ConfigureAwait(false);
                if (_health.FfmpegStatus?.IsAvailable == true) return;
            }
            else if (File.Exists(TargetFfmpegPath))
            {
                return;
            }

            if (!CanAutoInstall)
            {
                _logger.LogWarning("FFmpeg is missing and can't be installed automatically here. {Hint}", ManualInstallHint);
                Toast("FFmpeg is missing", $"Analysis and some playback need FFmpeg. {ManualInstallHint}", Views.NotificationType.Warning);
                return;
            }

            Toast("Installing FFmpeg", "ORBIT is downloading FFmpeg (about 110 MB) for analysis and playback. This happens once.", Views.NotificationType.Information);
            var result = await InstallAsync(null, ct).ConfigureAwait(false);
            Toast(result.Success ? "FFmpeg installed" : "FFmpeg install failed", result.Message,
                result.Success ? Views.NotificationType.Success : Views.NotificationType.Error);

            if (_health != null) await _health.CheckHealthAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("FFmpeg install cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup FFmpeg check/install failed");
        }
    }

    private void Toast(string title, string message, Views.NotificationType type) =>
        _eventBus?.Publish(new ToastRequestedEvent(title, message, type, TimeSpan.FromSeconds(8)));

    /// <param name="progress">0–1 while downloading.</param>
    public async Task<FfmpegInstallResult> InstallAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!CanAutoInstall)
            return new FfmpegInstallResult(false, $"ORBIT can't install FFmpeg on this system by itself. {ManualInstallHint}");

        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(TargetFfmpegPath))
                return new FfmpegInstallResult(true, "FFmpeg is installed.", TargetFfmpegPath);

            Directory.CreateDirectory(TargetDirectory);
            var zipPath = Path.Combine(TargetDirectory, "ffmpeg-download.zip.part");
            try
            {
                _logger.LogInformation("Installing FFmpeg {Version} from {Url}", Version, DownloadUrl);
                await DownloadVerifiedAsync(zipPath, progress, ct).ConfigureAwait(false);
                Extract(zipPath);
            }
            finally
            {
                TryDelete(zipPath);
            }

            if (!File.Exists(TargetFfmpegPath))
                return new FfmpegInstallResult(false, "The FFmpeg download didn't contain ffmpeg.exe.");

            _logger.LogInformation("FFmpeg {Version} installed to {Dir}", Version, TargetDirectory);
            return new FfmpegInstallResult(true, $"FFmpeg {Version} installed.", TargetFfmpegPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing FFmpeg failed");
            return new FfmpegInstallResult(false, ex is HttpRequestException or IOException
                ? $"Couldn't download FFmpeg ({ex.Message}). Check your internet connection and try again."
                : $"Couldn't install FFmpeg: {ex.Message}");
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task DownloadVerifiedAsync(string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? ExpectedSizeBytes;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, n);
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                progress?.Report(Math.Min(1.0, read / (double)total));
            }
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        if (actual != Sha256)
            throw new InvalidDataException($"The FFmpeg download was corrupted or changed (checksum {actual[..12]}…). Nothing was installed.");
    }

    private void Extract(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            // Entries sit under "ffmpeg-9.0.2-essentials_build/".
            var slash = entry.FullName.IndexOf('/');
            var relative = slash >= 0 ? entry.FullName[(slash + 1)..] : entry.FullName;
            if (Array.IndexOf(KeptEntries, relative) < 0) continue;

            var target = Path.Combine(TargetDirectory, Path.GetFileName(relative));
            var temp = target + ".tmp";
            entry.ExtractToFile(temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't remove the FFmpeg download leftover {Path}", path);
        }
    }
}
