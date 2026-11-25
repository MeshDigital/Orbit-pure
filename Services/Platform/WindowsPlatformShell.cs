using System;
using System.Diagnostics;
using Avalonia.Controls;
using SLSKDONET.Services.Shell;

namespace SLSKDONET.Services.Platform;

/// <summary>
/// Windows implementation of <see cref="IPlatformShell"/> (#452). Preserves existing Windows
/// behaviour: <c>explorer.exe</c>, <see cref="WindowsToastService"/>, <see cref="WindowsTaskbarIntegration"/>,
/// Job Objects via <see cref="ProcessJobTracker"/>, and DPAPI via <see cref="SecretProtector"/>.
/// </summary>
public sealed class WindowsPlatformShell : IPlatformShell
{
    private readonly WindowsToastService? _toastService;
    private readonly WindowsTaskbarIntegration? _taskbar;
    private readonly Func<ProcessStartInfo, bool> _launcher;

    public WindowsPlatformShell(
        WindowsToastService? toastService,
        WindowsTaskbarIntegration? taskbar,
        Func<ProcessStartInfo, bool>? launcher = null)
    {
        _toastService = toastService;
        _taskbar = taskbar;
        _launcher = launcher ?? DefaultLaunch;
    }

    public string PlatformName => "Windows";

    public ISecretProtector Secrets => SecretProtector.Default;

    public bool SupportsTaskbarProgress => true;

    internal static ProcessStartInfo BuildRevealStartInfo(string filePath)
    {
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add($"/select,{filePath}");
        return psi;
    }

    internal static ProcessStartInfo BuildOpenFolderStartInfo(string directoryPath)
    {
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(directoryPath);
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
        => _toastService?.Initialize(mainWindowHandle);

    public void ShowNotificationIfUnfocused(
        string title,
        string message,
        ToastSeverity severity = ToastSeverity.Info,
        string? navigateUsername = null,
        string? navigateRoomName = null)
        => _toastService?.ShowIfUnfocused(title, message, severity, navigateUsername, navigateRoomName);

    public void TrackChildProcess(Process process)
        => ProcessJobTracker.RegisterProcess(process);

    public void AttachTaskbar(Window window)
        => _taskbar?.Attach(window);

    public void ApplyTaskbarState(ThumbBarModel thumbs, ShellProgress progress)
        => _taskbar?.Apply(thumbs, progress);

    public void Dispose()
    {
        // Toast service and taskbar integration are owned by DI.
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
            Serilog.Log.Warning(ex, "[PlatformShell:Windows] Failed to launch {FileName}", psi.FileName);
            return false;
        }
    }
}
