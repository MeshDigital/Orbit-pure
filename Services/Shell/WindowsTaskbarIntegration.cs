using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Shell;

/// <summary>
/// Windows taskbar-button integration for the main window: a Previous / Play-Pause / Next
/// thumbnail toolbar (shown when hovering the taskbar button) and the taskbar progress bar for
/// downloads. All Win32/COM interop of the shell layer lives in this one file.
///
/// Everything here is best-effort: on non-Windows, on a missing COM class, or on any failed call
/// the integration logs and turns itself off — the user never sees an error, and nothing throws
/// out of the constructor (this is a DI singleton). The window's messages are observed through
/// Avalonia's own <see cref="Win32Properties.AddWndProcHookCallback"/>, not a second subclass.
/// </summary>
public sealed class WindowsTaskbarIntegration : IDisposable
{
    private const uint IdPrevious = 1;
    private const uint IdPlayPause = 2;
    private const uint IdNext = 3;

    private const uint WM_COMMAND = 0x0111;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const int THBN_CLICKED = 0x1800;
    private const uint MSGFLT_ALLOW = 1;
    private const int SM_CXSMICON = 49;
    private const int SW_RESTORE = 9;

    private readonly ILogger<WindowsTaskbarIntegration> _logger;

    private IntPtr _hwnd;
    private Window? _window;
    private ITaskbarList3? _taskbar;
    private uint _taskbarButtonCreatedMsg;
    private Win32Properties.CustomWndProcHookCallback? _hook; // field: native code holds it
    private bool _buttonsAdded;
    private bool _disabled;
    private bool _callFailureLogged;

    private IntPtr _iconPrevious, _iconPlay, _iconPause, _iconNext;

    // What the caller wants vs. what Windows currently shows (null = unknown → re-send).
    private ThumbBarModel? _wantedThumbs;
    private ShellProgress? _wantedProgress;
    private ThumbBarModel? _appliedThumbs;
    private ShellProgress? _appliedProgress;

    public WindowsTaskbarIntegration(ILogger<WindowsTaskbarIntegration> logger)
    {
        _logger = logger;
    }

    /// <summary>Raised on the UI thread with <see cref="ShellGlyph.Previous"/>, <see cref="ShellGlyph.Play"/> (play/pause) or <see cref="ShellGlyph.Next"/>.</summary>
    public event Action<ShellGlyph>? ThumbButtonClicked;

    public bool IsActive => _taskbar is not null && !_disabled;

    /// <summary>UI thread, after the window has been shown (it needs a native handle).</summary>
    public void Attach(Window window)
    {
        if (!OperatingSystem.IsWindows() || _window is not null) return;
        try
        {
            AttachCore(window);
        }
        catch (Exception ex)
        {
            Disable(ex, "attach");
        }
    }

    [SupportedOSPlatform("windows")]
    private void AttachCore(Window window)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            _logger.LogInformation("[Taskbar] Main window has no native handle; taskbar integration off");
            return;
        }

        _window = window;
        _hwnd = hwnd;
        _taskbarButtonCreatedMsg = RegisterWindowMessage("TaskbarButtonCreated");

        // If ORBIT ever runs elevated, UIPI would drop these from the (non-elevated) shell.
        TryAllowMessage(_taskbarButtonCreatedMsg);
        TryAllowMessage(WM_COMMAND);

        var type = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), throwOnError: false);
        if (type is null || Activator.CreateInstance(type) is not ITaskbarList3 taskbar)
        {
            _logger.LogInformation("[Taskbar] ITaskbarList3 not available on this system; taskbar integration off");
            return;
        }

        var hr = taskbar.HrInit();
        if (hr < 0)
        {
            _logger.LogInformation("[Taskbar] ITaskbarList3.HrInit failed (0x{Hr:X8}); taskbar integration off", hr);
            Marshal.ReleaseComObject(taskbar);
            return;
        }
        _taskbar = taskbar;

        CreateIcons();

        _hook = WndProcHook;
        Win32Properties.AddWndProcHookCallback(window, _hook);

        // The window is already showing, so its taskbar button normally exists already and the
        // TaskbarButtonCreated message has come and gone; set up now and again on every re-create.
        Sync();
    }

    /// <summary>Sets what the thumbnail toolbar and progress bar should show; applied now if possible.</summary>
    public void Apply(ThumbBarModel thumbs, ShellProgress progress)
    {
        _wantedThumbs = thumbs;
        _wantedProgress = progress;
        if (!IsActive || !OperatingSystem.IsWindows()) return;
        try
        {
            Sync();
        }
        catch (Exception ex)
        {
            Disable(ex, "update");
        }
    }

    /// <summary>Call after the window is shown again (a hidden window has no taskbar button, so its state was lost).</summary>
    public void OnWindowShown()
    {
        if (!IsActive || !OperatingSystem.IsWindows()) return;
        _appliedThumbs = null;
        _appliedProgress = null;
        try
        {
            Sync();
        }
        catch (Exception ex)
        {
            Disable(ex, "re-sync");
        }
    }

    [SupportedOSPlatform("windows")]
    private void Sync()
    {
        var taskbar = _taskbar;
        if (taskbar is null || _disabled || _hwnd == IntPtr.Zero) return;

        if (_wantedThumbs is { } thumbs && (!_buttonsAdded || thumbs != _appliedThumbs))
        {
            var buttons = BuildButtons(thumbs);
            int hr;
            if (!_buttonsAdded)
            {
                hr = taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
                // Already added for this taskbar button (e.g. TaskbarButtonCreated was missed): update instead.
                if (hr < 0) hr = taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
            }
            else
            {
                hr = taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
            }

            if (hr >= 0)
            {
                _buttonsAdded = true;
                _appliedThumbs = thumbs;
            }
            else
            {
                LogCallFailure("thumbnail toolbar", hr);
            }
        }

        if (_wantedProgress is { } progress && progress != _appliedProgress)
        {
            var hr = taskbar.SetProgressState(_hwnd, ToFlag(progress.Kind));
            if (hr >= 0 && progress.Kind is ShellProgressKind.Normal or ShellProgressKind.Paused or ShellProgressKind.Error)
                hr = taskbar.SetProgressValue(_hwnd, (ulong)Math.Round(Math.Clamp(progress.Value, 0, 1) * 1000), 1000);

            if (hr >= 0) _appliedProgress = progress;
            else LogCallFailure("progress", hr);
        }
    }

    private THUMBBUTTON[] BuildButtons(ThumbBarModel m) => new[]
    {
        Button(IdPrevious, _iconPrevious, "Previous", m.CanPrevious),
        Button(IdPlayPause, m.ShowPauseGlyph ? _iconPause : _iconPlay, m.PlayPauseTip, m.CanPlayPause),
        Button(IdNext, _iconNext, "Next", m.CanNext),
    };

    private static THUMBBUTTON Button(uint id, IntPtr icon, string tip, bool enabled) => new()
    {
        dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
        iId = id,
        hIcon = icon,
        szTip = tip,
        dwFlags = enabled ? THBF.ENABLED : THBF.DISABLED,
    };

    private static TBPFLAG ToFlag(ShellProgressKind kind) => kind switch
    {
        ShellProgressKind.Indeterminate => TBPFLAG.INDETERMINATE,
        ShellProgressKind.Normal => TBPFLAG.NORMAL,
        ShellProgressKind.Error => TBPFLAG.ERROR,
        ShellProgressKind.Paused => TBPFLAG.PAUSED,
        _ => TBPFLAG.NOPROGRESS,
    };

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (msg == _taskbarButtonCreatedMsg && _taskbarButtonCreatedMsg != 0)
            {
                // New taskbar button (window re-shown, or Explorer restarted): everything is gone.
                _buttonsAdded = false;
                _appliedThumbs = null;
                _appliedProgress = null;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (OperatingSystem.IsWindows()) Sync();
                    }
                    catch (Exception ex)
                    {
                        Disable(ex, "re-create");
                    }
                });
            }
            else if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero
                     && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (OperatingSystem.IsWindows() && IsActive) OnThemeChanged();
                    }
                    catch (Exception ex)
                    {
                        Disable(ex, "theme change");
                    }
                });
            }
            else if (msg == WM_COMMAND && HiWord(wParam) == THBN_CLICKED)
            {
                var glyph = LoWord(wParam) switch
                {
                    (int)IdPrevious => ShellGlyph.Previous,
                    (int)IdPlayPause => ShellGlyph.Play,
                    (int)IdNext => ShellGlyph.Next,
                    _ => (ShellGlyph?)null,
                };
                if (glyph is { } g)
                {
                    handled = true;
                    // Never run app logic inside the window procedure itself.
                    Dispatcher.UIThread.Post(() => RaiseClicked(g));
                }
            }
        }
        catch (Exception ex)
        {
            // An exception escaping into the native message loop would take the process down.
            _logger.LogWarning(ex, "[Taskbar] Window message hook failed");
        }
        return IntPtr.Zero;
    }

    private void RaiseClicked(ShellGlyph glyph)
    {
        try
        {
            ThumbButtonClicked?.Invoke(glyph);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Taskbar] Thumbnail button {Glyph} handler failed", glyph);
        }
    }

    private static int LoWord(IntPtr value) => (int)((long)value & 0xFFFF);
    private static int HiWord(IntPtr value) => (int)(((long)value >> 16) & 0xFFFF);

    // ── Icons ────────────────────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private void CreateIcons()
    {
        DestroyIcons();

        // The taskbar (and its thumbnail flyout) renders at the system DPI, not the window's.
        var metric = GetSystemMetrics(SM_CXSMICON);
        var size = metric > 0 ? metric : 16;

        // Glyph colour follows the taskbar theme: white on the dark flyout, near-black on light.
        var (r, g, b) = TaskbarUsesLightTheme() ? ((byte)32, (byte)32, (byte)32) : ((byte)255, (byte)255, (byte)255);
        _iconPrevious = CreateGlyphIcon(ShellGlyph.Previous, size, r, g, b);
        _iconPlay = CreateGlyphIcon(ShellGlyph.Play, size, r, g, b);
        _iconPause = CreateGlyphIcon(ShellGlyph.Pause, size, r, g, b);
        _iconNext = CreateGlyphIcon(ShellGlyph.Next, size, r, g, b);
    }

    private void DestroyIcons()
    {
        foreach (var icon in new[] { _iconPrevious, _iconPlay, _iconPause, _iconNext })
            if (icon != IntPtr.Zero) DestroyIcon(icon);
        _iconPrevious = _iconPlay = _iconPause = _iconNext = IntPtr.Zero;
    }

    [SupportedOSPlatform("windows")]
    private bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Taskbar] Reading the taskbar theme failed; assuming dark");
            return false;
        }
    }

    /// <summary>Light/dark switch (WM_SETTINGCHANGE "ImmersiveColorSet"): re-render the glyphs and re-send the buttons.</summary>
    [SupportedOSPlatform("windows")]
    private void OnThemeChanged()
    {
        CreateIcons();
        _appliedThumbs = null;
        Sync();
    }

    [SupportedOSPlatform("windows")]
    private IntPtr CreateGlyphIcon(ShellGlyph glyph, int size, byte r, byte g, byte b)
    {
        var pixels = ShellGlyphRasterizer.Render(glyph, size, r, g, b);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero)
        {
            _logger.LogDebug("[Taskbar] CreateDIBSection failed for {Glyph} icon", glyph);
            return IntPtr.Zero;
        }

        var mask = IntPtr.Zero;
        try
        {
            var asInts = new int[pixels.Length];
            Buffer.BlockCopy(pixels, 0, asInts, 0, pixels.Length * sizeof(uint));
            Marshal.Copy(asInts, 0, bits, asInts.Length);

            mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero); // alpha channel decides; mask unused
            var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            var icon = CreateIconIndirect(ref info);
            if (icon == IntPtr.Zero)
                _logger.LogDebug("[Taskbar] CreateIconIndirect failed for {Glyph} icon", glyph);
            return icon;
        }
        finally
        {
            // CreateIconIndirect copies both bitmaps.
            DeleteObject(color);
            if (mask != IntPtr.Zero) DeleteObject(mask);
        }
    }

    // ── Foreground ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores (if minimised) and really brings the window to the foreground. Plain Activate() is
    /// often refused by Windows' foreground lock and only flashes the taskbar button.
    /// </summary>
    public void BringToForeground(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;

            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;

            // Foreground lock: borrow the current foreground thread's input state for a moment.
            var foreground = GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out _);
            var thisThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != thisThread
                           && AttachThreadInput(thisThread, foregroundThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Taskbar] Bringing the window to the foreground failed");
        }
    }

    // ── Failure handling / cleanup ─────────────────────────────────────────────────────────

    private void LogCallFailure(string what, int hr)
    {
        // Expected while the window is hidden to the tray (no taskbar button exists then).
        if (_callFailureLogged) return;
        _callFailureLogged = true;
        _logger.LogDebug("[Taskbar] {What} call failed (0x{Hr:X8}); will retry when the taskbar button is re-created", what, hr);
    }

    private void Disable(Exception ex, string stage)
    {
        _disabled = true;
        _logger.LogWarning(ex, "[Taskbar] Taskbar integration failed during {Stage}; turned off for this session", stage);
    }

    [SupportedOSPlatform("windows")]
    private void TryAllowMessage(uint msg)
    {
        if (msg == 0) return;
        try
        {
            if (!ChangeWindowMessageFilterEx(_hwnd, msg, MSGFLT_ALLOW, IntPtr.Zero))
                _logger.LogDebug("[Taskbar] ChangeWindowMessageFilterEx({Msg}) failed", msg);
        }
        catch (EntryPointNotFoundException ex)
        {
            _logger.LogDebug(ex, "[Taskbar] ChangeWindowMessageFilterEx unavailable");
        }
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (_window is not null && _hook is not null)
                Win32Properties.RemoveWndProcHookCallback(_window, _hook);

            if (_taskbar is not null)
            {
                if (_hwnd != IntPtr.Zero) _taskbar.SetProgressState(_hwnd, TBPFLAG.NOPROGRESS);
                Marshal.ReleaseComObject(_taskbar);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Taskbar] Cleanup failed (window is closing anyway)");
        }
        finally
        {
            _taskbar = null;
            _window = null;
            DestroyIcons();
        }
    }

    // ── Native definitions ──────────────────────────────────────────────────────────────────

    private enum TBPFLAG
    {
        NOPROGRESS = 0,
        INDETERMINATE = 0x1,
        NORMAL = 0x2,
        ERROR = 0x4,
        PAUSED = 0x8,
    }

    [Flags]
    private enum THB : uint
    {
        BITMAP = 0x1,
        ICON = 0x2,
        TOOLTIP = 0x4,
        FLAGS = 0x8,
    }

    [Flags]
    private enum THBF : uint
    {
        ENABLED = 0x0,
        DISABLED = 0x1,
        DISMISSONCLICK = 0x2,
        NOBACKGROUND = 0x4,
        HIDDEN = 0x8,
        NONINTERACTIVE = 0x10,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public THB dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public THBF dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    // Vtable order matters: ITaskbarList → ITaskbarList2 → ITaskbarList3. PreserveSig everywhere so a
    // failing call returns its HRESULT instead of throwing a COMException through the UI thread.
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, TBPFLAG flags);
        [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        [PreserveSig] int UnregisterTab(IntPtr hwndTab);
        [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
        [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);


    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
}
