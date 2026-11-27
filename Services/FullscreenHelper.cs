using System.Runtime.InteropServices;
using System.Text;

namespace NotchturneWin.Services;

/// <summary>
/// Reliable Win32 fullscreen application detector (monitor-aware, desktop/shell-safe,
/// supports exclusive Direct3D games, borderless fullscreen, and browser/video fullscreen).
/// </summary>
public static class FullscreenHelper
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int pquns);

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Shell_InputSwitchTopLevelWindow",
        "NotifyIconOverflowWindow",
        "DV2ControlHost",
        "MultitaskingViewFrame",
        "TaskListThumbnailWnd",
        "Windows.UI.Core.CoreWindow"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public static bool IsFullscreenOnSameMonitor(IntPtr selfHwnd)
    {
        if (selfHwnd == IntPtr.Zero) return false;

        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == selfHwnd) return false;
        if (fg == GetDesktopWindow() || fg == GetShellWindow()) return false;
        if (!IsWindowVisible(fg) || IsIconic(fg)) return false;

        GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == (uint)Environment.ProcessId) return false;

        var cls = new StringBuilder(128);
        if (GetClassName(fg, cls, cls.Capacity) > 0 && IgnoredClasses.Contains(cls.ToString()))
            return false;

        IntPtr selfMon = MonitorFromWindow(selfHwnd, MONITOR_DEFAULTTONEAREST);
        IntPtr fgMon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (selfMon == IntPtr.Zero || fgMon == IntPtr.Zero || selfMon != fgMon)
            return false;

        // Direct3D exclusive fullscreen or Windows presentation mode on our monitor
        if (SHQueryUserNotificationState(out int quns) == 0 &&
            (quns == QUNS_RUNNING_D3D_FULL_SCREEN || quns == QUNS_PRESENTATION_MODE))
        {
            return true;
        }

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(fgMon, ref mi)) return false;
        if (!GetWindowRect(fg, out RECT r)) return false;

        var mon = mi.rcMonitor;
        var work = mi.rcWork;

        bool coversMonitor =
            r.Left <= mon.Left + 2 &&
            r.Top <= mon.Top + 2 &&
            r.Right >= mon.Right - 2 &&
            r.Bottom >= mon.Bottom - 2;

        if (!coversMonitor) return false;

        // If Windows taskbar is auto-hidden, a normal maximized desktop window also reaches mon.Bottom;
        // distinguish it from true fullscreen (F11 / borderless game / video player) by frame style.
        bool taskbarAutoHidden =
            work.Left <= mon.Left + 4 &&
            work.Top <= mon.Top + 4 &&
            work.Right >= mon.Right - 4 &&
            work.Bottom >= mon.Bottom - 4;

        if (taskbarAutoHidden && IsZoomed(fg))
        {
            int style = GetWindowLong(fg, GWL_STYLE);
            bool hasStandardFrame = (style & WS_CAPTION) == WS_CAPTION && (style & WS_THICKFRAME) == WS_THICKFRAME;
            if (hasStandardFrame) return false;
        }

        return true;
    }
}
