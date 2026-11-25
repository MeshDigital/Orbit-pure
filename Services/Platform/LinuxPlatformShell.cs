using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using SLSKDONET.Services.Shell;

namespace SLSKDONET.Services.Platform;

/// <summary>
/// Linux implementation of <see cref="IPlatformShell"/> (#452):
/// <c>xdg-open</c> for file manager, <c>notify-send</c> for desktop notifications,
/// best-effort child-process teardown on app exit, and Secret Service / user-only key file via
/// <see cref="SecretProtector"/>.
/// </summary>
public sealed class LinuxPlatformShell : IPlatformShell
{
    private readonly Func<ProcessStartInfo, bool> _launcher;
    private readonly ConcurrentDictionary<int, Process> _trackedChildren = new();
    private bool _disposed;

    public LinuxPlatformShell(Func<ProcessStartInfo, bool>? launcher = null)
    {
        _launcher = launcher ?? DefaultLaunch;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public string PlatformName => "Linux";

    public ISecretProtector Secrets => SecretProtector.Default;

    public bool SupportsTaskbarProgress => false;

    internal static ProcessStartInfo BuildRevealStartInfo(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        var target = string.IsNullOrEmpty(dir) ? filePath : dir;
        var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        psi.ArgumentList.Add(target);
        return psi;
    }

    internal static ProcessStartInfo BuildOpenFolderStartInfo(string directoryPath)
    {
        var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        psi.ArgumentList.Add(directoryPath);
        return psi;
    }

    internal static ProcessStartInfo BuildNotificationStartInfo(string title, string message, ToastSeverity severity)
    {
        var urgency = severity switch
        {
            ToastSeverity.Error => "critical",
            ToastSeverity.Warning => "normal",
            _ => "low",
        };
        var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
        psi.ArgumentList.Add("--app-name=ORBIT");
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(urgency);
        psi.ArgumentList.Add(title ?? string.Empty);
        psi.ArgumentList.Add(message ?? string.Empty);
        return psi;
    }

    public bool RevealInFolder(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        return _launcher(BuildRevealStartInfo(filePath));
    }

    public bool OpenFolder(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return false;
        return _launcher(BuildOpenFolderStartInfo(directoryPath));
    }

    public void InitializeNotifications(IntPtr mainWindowHandle)
    {
        // No window-handle hook needed for notify-send.
    }

    public void ShowNotificationIfUnfocused(
        string title,
        string message,
        ToastSeverity severity = ToastSeverity.Info,
        string? navigateUsername = null,
        string? navigateRoomName = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message)) return;
        _launcher(BuildNotificationStartInfo(title, message, severity));
    }

    public void TrackChildProcess(Process process)
    {
        if (process is null) return;
        try
        {
            if (process.HasExited) return;
            var pid = process.Id;
            _trackedChildren[pid] = process;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _trackedChildren.TryRemove(pid, out _);
        }
        catch
        {
            // intentional: best-effort child tracking; process may already have exited.
        }
    }

    public void AttachTaskbar(Window window) { }

    public void ApplyTaskbarState(ThumbBarModel thumbs, ShellProgress progress) { }

    private void OnProcessExit(object? sender, EventArgs e) => KillTrackedChildren();

    private void KillTrackedChildren()
    {
        foreach (var kvp in _trackedChildren)
        {
            if (_trackedChildren.TryRemove(kvp.Key, out var proc))
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // intentional: best-effort teardown on process exit.
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        KillTrackedChildren();
    }

    private static bool DefaultLaunch(ProcessStartInfo psi)
    {
        try
        {
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[PlatformShell:Linux] Failed to launch {FileName}", psi.FileName);
            return false;
        }
    }
}
