using System.Runtime.InteropServices;
using System.Windows;

namespace ToniaDeskBox.Services;

/// <summary>物理像素坐标点。取代原来只为拿一个光标位置而引入的 System.Drawing。</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>物理像素矩形（工作区 / 监听器）。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// 显示器与光标相关的 Win32 封装。
///
/// 原先这些能力来自 <c>System.Windows.Forms.Screen</c> / <c>Cursor</c> / <c>System.Drawing</c>，
/// 为了一个托盘图标和几个屏幕坐标把整个 WinForms + Drawing 程序集链路拖进进程。
/// 这里用 MonitorFromWindow / GetMonitorInfo / GetCursorPos 直接取，语义完全等价：
/// 返回的矩形都是**物理像素**，调用方按窗口 DPI 自行换算成 DIP。
/// </summary>
public static class ScreenUtil
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private static PixelRect ToRect(in MONITORINFO mi) =>
        new(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);

    /// <summary>窗口所在显示器的工作区（物理像素）。窗口句柄无效时退回主屏。</summary>
    public static PixelRect WorkAreaPx(IntPtr hwnd)
    {
        var monitor = hwnd != IntPtr.Zero
            ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
            : IntPtr.Zero;
        return WorkAreaOf(monitor);
    }

    /// <summary>主显示器的工作区（物理像素）。</summary>
    public static PixelRect PrimaryWorkAreaPx() =>
        WorkAreaOf(MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY));

    private static PixelRect WorkAreaOf(IntPtr monitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref mi))
            return ToRect(mi);

        // 极小概率拿不到显示器信息（远程会话切换瞬间等）：用系统参数兜底，
        // 宁可坐标略偏也不要抛异常打断拖动
        return new PixelRect(0, 0,
            (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
    }

    /// <summary>光标位置（物理像素）。取不到时返回 (0, 0)，由调用方按原有逻辑处理。</summary>
    /// <summary>
    /// 窗口（含 Popup 自己的 HWND）在屏幕上的物理像素矩形。
    /// 菜单是 Popup，不是 Window，`Window.Left/Top` 取不到；只能问它的 HWND。
    /// </summary>
    public static PixelRect? WindowRectPx(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        return GetWindowRect(hwnd, out var r)
            ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
            : null;
    }

    public static PixelPoint CursorPosition() =>
        GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : new PixelPoint(0, 0);
}
