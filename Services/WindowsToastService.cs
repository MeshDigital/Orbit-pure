using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

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
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;
    private const int NIIF_WARNING = 0x00000002;
    private const int NIIF_ERROR = 0x00000003;
    private const int NIIF_NOSOUND = 0x00000010;
    private const int IDI_APPLICATION = 32512;
    private const uint WDA_NONE = 0x00000000;

    // App-defined tray callback message — WM_APP (0x8000) is reserved by Windows specifically for
    // this purpose and guaranteed not to collide with any standard or Avalonia-internal message.
    private const int WM_APP = 0x8000;
    private const int TrayCallbackMessage = WM_APP + 1;
    private const long NIN_BALLOONUSERCLICK = 0x0400 + 5; // WM_USER + 5 — fired when the balloon body itself is clicked
    private const int GWLP_WNDPROC = -4;

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

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // No 32-bit SetWindowLongPtr export exists — SetWindowLong is the correct call there, and
    // both signatures are pointer-width-safe (IntPtr in, IntPtr out) so a single wrapper covers both.
    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);

    private readonly IEventBus _eventBus;
    private readonly ILogger<WindowsToastService> _logger;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _iconAdded;
    private const int IconUid = 1;

    // Held in a field, never a local — native code holds an unmanaged pointer to this delegate for
    // the life of the subclass, so letting the GC collect it would crash the process on the next click.
    private WndProcDelegate? _newWndProc;
    private IntPtr _originalWndProc = IntPtr.Zero;
    private bool _subclassed;

    // Only the most recent notification's navigation target matters — there's one balloon slot,
    // so a newer notification always supersedes whatever an unclicked older one would have opened.
    private string? _pendingNavUsername;
    private string? _pendingNavRoomName;

    public WindowsToastService(IEventBus eventBus, ILogger<WindowsToastService> logger)
    {
        _eventBus = eventBus;
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
                uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE,
                uCallbackMessage = TrayCallbackMessage,
                hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION),
                szTip = "ORBIT",
            };

            _iconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!_iconAdded)
                _logger.LogDebug("[WindowsToast] Shell_NotifyIcon(NIM_ADD) failed — OS notifications unavailable this session");
            else
                TrySubclassWndProc();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WindowsToast] Failed to register tray icon — OS notifications unavailable this session");
        }
    }

    /// <summary>
    /// Installs a WndProc subclass on the main window purely to catch the balloon-click callback
    /// message — every other message is passed through to Avalonia's own WndProc unconditionally
    /// and unmodified. This is the same technique Avalonia's own internal TrayIconImpl and WinForms'
    /// NotifyIcon use for the same purpose; there's no supported shortcut that avoids it.
    /// </summary>
    private void TrySubclassWndProc()
    {
        try
        {
            _newWndProc = WndProc;
            var newProcPtr = Marshal.GetFunctionPointerForDelegate(_newWndProc);
            _originalWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, newProcPtr);
            _subclassed = _originalWndProc != IntPtr.Zero;
            if (!_subclassed)
                _logger.LogDebug("[WindowsToast] WndProc subclass failed — balloon clicks won't navigate this session");
        }
        catch (Exception ex)
        {
            _subclassed = false;
            _logger.LogDebug(ex, "[WindowsToast] WndProc subclass failed — balloon clicks won't navigate this session");
        }
    }

    /// <summary>
    /// Passes every message through to Avalonia's real WndProc unconditionally — the single
    /// exception is the balloon-click callback, which is only ever observed here, never swallowed.
    /// Any exception while handling that one case is caught and logged rather than allowed to
    /// escape into the native message loop, which would otherwise take the whole app down.
    /// </summary>
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayCallbackMessage && lParam.ToInt64() == NIN_BALLOONUSERCLICK)
        {
            try
            {
                var username = _pendingNavUsername;
                var roomName = _pendingNavRoomName;
                if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(roomName))
                    _eventBus.Publish(new OpenConversationRequestedEvent(username, roomName));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WindowsToast] Failed to handle balloon click");
            }
        }

        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Shows a real Windows toast, but only while the ORBIT window doesn't have focus — when it's
    /// focused/on-screen, the existing in-app toast already covers it, and popping both would be
    /// redundant. <paramref name="navigateUsername"/>/<paramref name="navigateRoomName"/> are
    /// optional — when set, clicking the balloon body navigates straight to that conversation/room
    /// (see <see cref="WndProc"/>); there's only one balloon slot, so a newer notification's target
    /// always supersedes an unclicked older one's.
    /// </summary>
    public void ShowIfUnfocused(string title, string message, ToastSeverity severity = ToastSeverity.Info, string? navigateUsername = null, string? navigateRoomName = null)
    {
        if (!_iconAdded)
            return;

        try
        {
            if (GetForegroundWindow() == _hwnd)
                return;

            _pendingNavUsername = navigateUsername;
            _pendingNavRoomName = navigateRoomName;

            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = IconUid,
                uFlags = NIF_INFO | NIF_MESSAGE,
                uCallbackMessage = TrayCallbackMessage,
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

        if (_subclassed)
        {
            try
            {
                SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);
            }
            catch
            {
                // Best-effort — the window is going away regardless.
            }
            _subclassed = false;
        }

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
