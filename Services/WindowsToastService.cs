using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Real OS-level Windows notifications (Action Center toasts), via the classic
/// Shell_NotifyIcon balloon API — Windows 10/11 render these as modern toasts automatically, no
/// AppUserModelID/MSIX packaging registration required, unlike the WinRT toast APIs. Deliberately
/// separate from <see cref="INotificationService"/>/<c>ToastRequestedEvent</c>, which is an
/// in-app-only popup invisible whenever the ORBIT window isn't on screen — this exists specifically
/// so peer chat messages are noticed even while the app is minimized or in the background.
///
/// Owns a small tray icon purely as the anchor Shell_NotifyIcon requires for a balloon; it isn't a
/// general "minimize to tray" feature.
/// </summary>
public sealed class WindowsToastService : IDisposable
{
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;
    private const int NIIF_WARNING = 0x00000002;
    private const int NIIF_ERROR = 0x00000003;
    private const int NIIF_NOSOUND = 0x00000010;
    private const int IDI_APPLICATION = 32512;
    private const uint WDA_NONE = 0x00000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly ILogger<WindowsToastService> _logger;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _iconAdded;
    private const int IconUid = 1;

    public WindowsToastService(ILogger<WindowsToastService> logger)
    {
        _logger = logger;
    }

    /// <summary>Must be called once, on the UI thread, after the main window's native handle exists.</summary>
    public void Initialize(IntPtr mainWindowHandle)
    {
        if (!OperatingSystem.IsWindows() || _iconAdded || mainWindowHandle == IntPtr.Zero)
            return;

        _hwnd = mainWindowHandle;
        try
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = IconUid,
                uFlags = NIF_ICON | NIF_TIP,
                hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION),
                szTip = "ORBIT",
            };

            _iconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!_iconAdded)
                _logger.LogDebug("[WindowsToast] Shell_NotifyIcon(NIM_ADD) failed — OS notifications unavailable this session");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WindowsToast] Failed to register tray icon — OS notifications unavailable this session");
        }
    }

    /// <summary>
    /// Shows a real Windows toast, but only while the ORBIT window doesn't have focus — when it's
    /// focused/on-screen, the existing in-app toast already covers it, and popping both would be
    /// redundant.
    /// </summary>
    public void ShowIfUnfocused(string title, string message, ToastSeverity severity = ToastSeverity.Info)
    {
        if (!_iconAdded)
            return;

        try
        {
            if (GetForegroundWindow() == _hwnd)
                return;

            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = IconUid,
                uFlags = NIF_INFO,
                szInfoTitle = Truncate(title, 63),
                szInfo = Truncate(message, 255),
                dwInfoFlags = severity switch
                {
                    ToastSeverity.Warning => NIIF_WARNING,
                    ToastSeverity.Error => NIIF_ERROR,
                    _ => NIIF_INFO,
                },
            };

            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WindowsToast] Failed to show balloon notification");
        }
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        if (!_iconAdded)
            return;

        try
        {
            var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = IconUid };
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        catch
        {
            // Best-effort cleanup — the OS reclaims orphaned tray icons once the process exits regardless.
        }
    }
}

public enum ToastSeverity
{
    Info,
    Warning,
    Error,
}
