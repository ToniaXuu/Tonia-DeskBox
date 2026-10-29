using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ToniaDeskBox.Services;

public static class SnapService
{
    private const double Threshold = 12;

    private struct Info
    {
        public System.Drawing.Rectangle Wa;
        public double Dpi;
    }

    private static bool GetInfo(Window window, out Info info)
    {
        info = default;
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        info.Wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        info.Dpi = VisualTreeHelper.GetDpi(window).PixelsPerDip;
        return true;
    }

    // 拖动定位：从期望位置一次性算完 吸附→贴盒→防重叠→钳制，
    // 与当前有差异才移动窗口一次——避免"期望位置/吸附位置"每帧各写一次造成的抽搐
    public static bool ApplyDragPosition(Window window, double desiredLeft, double desiredTop, IEnumerable<Window>? others)
    {
        if (!GetInfo(window, out var info)) return false;

        double l = desiredLeft, t = desiredTop;
        double w = window.ActualWidth, h = window.ActualHeight;

        SnapEdges(ref l, ref t, w, h, info);
        if (others != null)
        {
            SnapBoxes(ref l, ref t, w, h, others);
            ResolveOverlap(ref l, ref t, w, h, others);
        }
        Clamp(ref l, ref t, w, h, info);

        bool changed = Math.Abs(l - window.Left) > 0.1 || Math.Abs(t - window.Top) > 0.1;
        if (changed)
        {
            window.Left = l;
            window.Top = t;
        }
        return changed;
    }

    public static void ClampIntoScreen(Window window)
    {
        if (!GetInfo(window, out var info)) return;
        double l = window.Left, t = window.Top;
        Clamp(ref l, ref t, window.ActualWidth, window.ActualHeight, info);
        window.Left = l;
        window.Top = t;
    }

    // 注意单位：Wa 是物理像素，left/top/w/h 是 DIP，必须先换算再运算
    private static void SnapEdges(ref double l, ref double t, double w, double h, in Info info)
    {
        double waX = info.Wa.Left / info.Dpi, waY = info.Wa.Top / info.Dpi;
        double waW = info.Wa.Width / info.Dpi, waH = info.Wa.Height / info.Dpi;

        if (Math.Abs(l - waX) < Threshold)
            l = waX;
        else if (Math.Abs(l + w - (waX + waW)) < Threshold)
            l = waX + waW - w;

        if (Math.Abs(t - waY) < Threshold)
            t = waY;
        else if (Math.Abs(t + h - (waY + waH)) < Threshold)
            t = waY + waH - h;
    }

    private static void SnapBoxes(ref double l, ref double t, double w, double h, IEnumerable<Window> others)
    {
        foreach (var o in others)
        {
            if (o.ActualWidth <= 0 || o.ActualHeight <= 0) continue;
            double oL = o.Left, oT = o.Top;
            double oR = oL + o.ActualWidth, oB = oT + o.ActualHeight;
            double R = l + w, B = t + h;

            bool hSnapped = false;
            if (Math.Abs(R - oL) < Threshold) { l = oL - w; hSnapped = true; }
            else if (Math.Abs(l - oR) < Threshold) { l = oR; hSnapped = true; }
            else if (Math.Abs(l - oL) < Threshold) { l = oL; hSnapped = true; }
            else if (Math.Abs(R - oR) < Threshold) { l = oR - w; hSnapped = true; }

            // 横排时水平对齐：水平方向已贴靠或重叠，且顶边足够接近
            double nL = l, nR = nL + w;
            bool hOverlap = nR > oL + 1 && nL < oR - 1;
            if ((hSnapped || hOverlap) && Math.Abs(t - oT) < Threshold)
                t = oT;

            bool vSnapped = false;
            if (Math.Abs(B - oT) < Threshold) { t = oT - h; vSnapped = true; }
            else if (Math.Abs(t - oB) < Threshold) { t = oB; vSnapped = true; }
            else if (Math.Abs(t - oT) < Threshold) { t = oT; vSnapped = true; }
            else if (Math.Abs(B - oB) < Threshold) { t = oB - h; vSnapped = true; }

            // 竖排时垂直对齐：垂直方向已贴靠或重叠，且左边足够接近
            double nT = t, nB = nT + h;
            bool vOverlap = nB > oT + 1 && nT < oB - 1;
            if ((vSnapped || vOverlap) && Math.Abs(l - oL) < Threshold)
                l = oL;
        }
    }

    // 防重叠：与其它盒子相交时沿最小穿透轴推出（拖动中与松手后都调用）
    private static void ResolveOverlap(ref double l, ref double t, double w, double h, IEnumerable<Window> others)
    {
        for (int iter = 0; iter < 3; iter++)
        {
            bool moved = false;
            foreach (var o in others)
            {
                if (o.ActualWidth <= 0 || o.ActualHeight <= 0) continue;
                double oL = o.Left, oT = o.Top;
                double oW = o.ActualWidth, oH = o.ActualHeight;

                double ix = Math.Min(l + w, oL + oW) - Math.Max(l, oL);
                double iy = Math.Min(t + h, oT + oH) - Math.Max(t, oT);
                if (ix <= 0 || iy <= 0) continue;

                if (ix < iy)
                    l = l + w / 2 < oL + oW / 2 ? oL - w : oL + oW;
                else
                    t = t + h / 2 < oT + oH / 2 ? oT - h : oT + oH;
                moved = true;
            }
            if (!moved) break;
        }
    }

    private static void Clamp(ref double l, ref double t, double w, double h, in Info info)
    {
        double waX = info.Wa.Left / info.Dpi, waY = info.Wa.Top / info.Dpi;
        double waR = info.Wa.Right / info.Dpi, waB = info.Wa.Bottom / info.Dpi;

        // 窗口比工作区还大时上限会小于下限，Math.Clamp 会抛异常，这里退化为贴边
        l = waR - w < waX ? waX : Math.Clamp(l, waX, waR - w);
        t = waB - h < waY ? waY : Math.Clamp(t, waY, waB - h);
    }
}
