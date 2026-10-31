using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace SLSKDONET.Services.Platform;

public sealed record DotNetInstallResult(bool Success, string Message, string? Version = null);

/// <summary>
/// Checks for and (on supported PCs) auto-installs the .NET 9 base runtime, mirroring
/// <see cref="FfmpegInstallerService"/>. The self-contained build bundles the runtime, but this is a
/// belt-and-suspenders prerequisite for the framework-dependent/updater path and companion tools.
/// </summary>
public sealed class DotNetRuntimeInstallerService
{
    public const string RequiredRuntime = "Microsoft.NETCore.App";
    public const string RequiredMajorMinor = "9.0";
    public const string PinnedVersion = "9.0.0";

    /// <summary>Pinned, silent-installable .NET runtime installer (Windows x64).</summary>
    public const string DownloadUrl =
        $"https://dotnetcli.azureedge.net/dotnet/Runtime/{PinnedVersion}/dotnet-runtime-{PinnedVersion}-win-x64.exe";

    private readonly ILogger<DotNetRuntimeInstallerService> _logger;

    public DotNetRuntimeInstallerService(ILogger<DotNetRuntimeInstallerService> logger)
    {
        _logger = logger;
    }

    public static bool CanAutoInstall => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    public static string ManualInstallHint =>
        "Install the .NET 9 Runtime from https://dotnet.microsoft.com/download/dotnet/9.0 (x64).";

    /// <summary>True when the .NET 9 base runtime is installed (registry check, Windows).</summary>
    public static string? FindInstalledVersion()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            const string keyPath = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App";
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null)
                return null;

            foreach (var name in key.GetValueNames())
                if (name.StartsWith(RequiredMajorMinor, StringComparison.Ordinal))
                    return name;

            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<DotNetInstallResult> EnsureInstalledAsync(CancellationToken ct = default)
    {
        var existing = FindInstalledVersion();
        if (existing is not null)
            return new DotNetInstallResult(true, $".NET {existing} is installed.", existing);

        if (!CanAutoInstall)
            return new DotNetInstallResult(false, $"ORBIT can't install .NET on this system by itself. {ManualInstallHint}");

        return await InstallAsync(ct).ConfigureAwait(false);
    }

    public async Task<DotNetInstallResult> InstallAsync(CancellationToken ct = default)
    {
        var installerPath = Path.Combine(Path.GetTempPath(), $"dotnet-runtime-{PinnedVersion}-win-x64.exe");
        try
        {
            _logger.LogInformation("Downloading .NET {Version} runtime from {Url}", PinnedVersion, DownloadUrl);
            using var http = new HttpClient();
            await using (var src = await http.GetStreamAsync(DownloadUrl, ct).ConfigureAwait(false))
            await using (var dst = File.Create(installerPath))
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);

            _logger.LogInformation("Running .NET runtime installer silently…");
            var psi = new ProcessStartInfo(installerPath, "/install /quiet /norestart")
            {
                UseShellExecute = true,
                Verb = "runas", // the runtime installs machine-wide → needs elevation
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("The .NET installer didn't start.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var installed = FindInstalledVersion();
            return installed is not null
                ? new DotNetInstallResult(true, $".NET {installed} installed.", installed)
                : new DotNetInstallResult(false, $"The .NET installer exited {process.ExitCode}, but the runtime wasn't detected. {ManualInstallHint}");
        }
        catch (OperationCanceledException)
        {
            return new DotNetInstallResult(false, "The .NET install was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ".NET runtime install failed");
            return new DotNetInstallResult(false, $"Couldn't install .NET: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(installerPath)) File.Delete(installerPath); }
            catch { /* best-effort cleanup */ }
        }
    }
}
