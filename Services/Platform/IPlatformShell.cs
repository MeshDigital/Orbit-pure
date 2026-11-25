using System;
using System.Diagnostics;
using Avalonia.Controls;
using SLSKDONET.Services.Shell;

namespace SLSKDONET.Services.Platform;

/// <summary>
/// Single cross-platform seam for OS shell integration (#452):
/// file-manager reveal/open, OS notifications, taskbar progress, child-process lifetime,
/// and per-user secret storage.
/// </summary>
public interface IPlatformShell : IDisposable
{
    /// <summary>Human-readable platform identifier ("Windows", "macOS", "Linux").</summary>
    string PlatformName { get; }

    /// <summary>Per-user secret protector (DPAPI on Windows, Keychain on macOS, Secret Service / key file on Linux).</summary>
    ISecretProtector Secrets { get; }

    /// <summary>True when the host OS supports native taskbar progress/thumbnail buttons.</summary>
    bool SupportsTaskbarProgress { get; }

    /// <summary>
    /// Reveals <paramref name="filePath"/> selected in the OS file manager
    /// (Explorer <c>/select</c>, Finder <c>open -R</c>, or the containing folder via <c>xdg-open</c>).
    /// Never executes the file. Returns false (logged) on failure.
    /// </summary>
    bool RevealInFolder(string filePath);

    /// <summary>
    /// Opens <paramref name="directoryPath"/> in the OS file manager.
    /// Returns false (logged) on failure.
    /// </summary>
    bool OpenFolder(string directoryPath);

    /// <summary>Initializes OS-level notifications once the main window handle exists.</summary>
    void InitializeNotifications(IntPtr mainWindowHandle);

    /// <summary>Shows an OS-level notification when the app window is not focused.</summary>
    void ShowNotificationIfUnfocused(
        string title,
        string message,
        ToastSeverity severity = ToastSeverity.Info,
        string? navigateUsername = null,
        string? navigateRoomName = null);

    /// <summary>Registers a child process so it is terminated if ORBIT exits.</summary>
    void TrackChildProcess(Process process);

    /// <summary>Attaches native taskbar integration (thumbnail buttons + progress) when supported.</summary>
    void AttachTaskbar(Window window);

    /// <summary>Updates native taskbar thumbnail buttons and progress when supported; no-op on platforms without it.</summary>
    void ApplyTaskbarState(ThumbBarModel thumbs, ShellProgress progress);
}

/// <summary>
/// Process-wide accessor for static/non-DI call sites (e.g. <c>ExternalUrlHelper</c>, dialogs,
/// and static helpers). Configured from DI at startup; falls back to an OS-appropriate default
/// when accessed in unit tests or before DI completes.
/// </summary>
public static class PlatformShell
{
    private static IPlatformShell? _current;

    public static IPlatformShell Current => _current ??= CreateFallback();

    public static void SetCurrent(IPlatformShell shell)
    {
        _current = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    private static IPlatformShell CreateFallback()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsPlatformShell(toastService: null, taskbar: null);
        if (OperatingSystem.IsMacOS())
            return new MacOSPlatformShell();
        return new LinuxPlatformShell();
    }
}
