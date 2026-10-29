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

    public static void SnapToBoxes(Window window, IEnumerable<Window> others)
    {
        const double t = Threshold;
        double L = window.Left, T = window.Top;
        double R = L + window.ActualWidth, B = T + window.ActualHeight;

        foreach (var o in others)
        {
            if (o.ActualWidth <= 0 || o.ActualHeight <= 0) continue;
            double oL = o.Left, oT = o.Top;
            double oR = oL + o.ActualWidth, oB = oT + o.ActualHeight;

            bool hSnapped = false;
            if (Math.Abs(R - oL) < t) { window.Left = oL - window.ActualWidth; hSnapped = true; }
            else if (Math.Abs(L - oR) < t) { window.Left = oR; hSnapped = true; }
            else if (Math.Abs(L - oL) < t) { window.Left = oL; hSnapped = true; }
            else if (Math.Abs(R - oR) < t) { window.Left = oR - window.ActualWidth; hSnapped = true; }

            // 横排时水平对齐：水平方向已贴靠或重叠，且顶边足够接近
            double nL = window.Left, nR = nL + window.ActualWidth;
            bool hOverlap = nR > oL + 1 && nL < oR - 1;
            if ((hSnapped || hOverlap) && Math.Abs(T - oT) < t)
                window.Top = oT;

            bool vSnapped = false;
            if (Math.Abs(B - oT) < t) { window.Top = oT - window.ActualHeight; vSnapped = true; }
            else if (Math.Abs(T - oB) < t) { window.Top = oB; vSnapped = true; }
            else if (Math.Abs(T - oT) < t) { window.Top = oT; vSnapped = true; }
            else if (Math.Abs(B - oB) < t) { window.Top = oB - window.ActualHeight; vSnapped = true; }

            // 竖排时垂直对齐：垂直方向已贴靠或重叠，且左边足够接近
            double nT = window.Top, nB = nT + window.ActualHeight;
            bool vOverlap = nB > oT + 1 && nT < oB - 1;
            if ((vSnapped || vOverlap) && Math.Abs(L - oL) < t)
                window.Left = oL;

            L = window.Left; T = window.Top;
            R = L + window.ActualWidth; B = T + window.ActualHeight;
        }
    }
}
