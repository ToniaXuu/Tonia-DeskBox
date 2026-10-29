using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ToniaDeskBox.Services;

public static class SnapService
{
    private const double Threshold = 12;

    public static void SnapToScreenEdges(Window window)
    {
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        double dpi = VisualTreeHelper.GetDpi(window).PixelsPerDip;
        double waX = wa.Left / dpi, waY = wa.Top / dpi;
        double waW = wa.Width / dpi, waH = wa.Height / dpi;

        double left = window.Left;
        double right = left + window.ActualWidth;
        double top = window.Top;
        double bottom = top + window.ActualHeight;

        if (Math.Abs(left - waX) < Threshold)
            window.Left = waX;
        else if (Math.Abs(right - (waX + waW)) < Threshold)
            window.Left = waX + waW - window.ActualWidth;

        if (Math.Abs(top - waY) < Threshold)
            window.Top = waY;
        else if (Math.Abs(bottom - (waY + waH)) < Threshold)
            window.Top = waY + waH - window.ActualHeight;
    }

    public static void ClampIntoScreen(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        double dpi = VisualTreeHelper.GetDpi(window).PixelsPerDip;

        window.Left = Math.Clamp(window.Left, wa.Left / dpi, (wa.Right - window.ActualWidth) / dpi);
        window.Top = Math.Clamp(window.Top, wa.Top / dpi, (wa.Bottom - window.ActualHeight) / dpi);
    }
}
