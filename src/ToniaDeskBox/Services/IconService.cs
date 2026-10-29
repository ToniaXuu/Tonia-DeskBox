using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImagingHelper = System.Windows.Interop.Imaging;

namespace ToniaDeskBox.Services;

/// <summary>
/// 图标提取：走 IShellItemImageFactory 取 96px 高分辨率图标。
/// 系统大图标接口只有 32px，在高 DPI 屏上放大到 44 DIP（175% 下约 77 物理像素）会明显发糊。
/// 提取是耗时的 COM 调用，统一放后台线程 + 内存缓存，面板打开不再阻塞 UI。
/// </summary>
public static class IconService
{
    private const int IconSize = 96;
    private const int CacheLimit = 800;
    private const uint SiigbfBiggersizeOk = 0x00000004;

    private static readonly ConcurrentDictionary<string, ImageSource> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> InFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetCached(string path) =>
        Cache.TryGetValue(Key(path), out var src) ? src : null;

    public static Task<ImageSource?> GetAsync(string path)
    {
        var key = Key(path);
        if (Cache.TryGetValue(key, out var cached))
            return Task.FromResult<ImageSource?>(cached);
        return InFlight.GetOrAdd(key, _ => ExtractAsync(path, key));
    }

    // 快捷方式被替换后图标要跟着变，缓存键带上文件修改时间
    private static string Key(string path)
    {
        try { return path + "|" + File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return path; }
    }

    private static async Task<ImageSource?> ExtractAsync(string path, string key)
    {
        ImageSource? src = null;
        try
        {
            src = await Task.Run(() => Extract(path)).ConfigureAwait(false);
        }
        catch
        {
            src = null;
        }

        if (src != null)
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[key] = src;
        }
        InFlight.TryRemove(key, out _);
        return src;
    }

    private static ImageSource? Extract(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;

        var src = ExtractViaShellItem(path);
        if (src != null) return src;

        return ExtractViaShellInfo(path);
    }

    // IShellItemImageFactory：按请求尺寸返回真实大图（48/96/256 都能拿到）
    private static ImageSource? ExtractViaShellItem(string path)
    {
        IntPtr hbm = IntPtr.Zero;
        object? factory = null;
        try
        {
            Guid iid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (factory == null) return null;

            var imageFactory = (IShellItemImageFactory)factory;
            int hr = imageFactory.GetImage(
                new NativeSize { cx = IconSize, cy = IconSize }, (int)SiigbfBiggersizeOk, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;

            var bmp = ImagingHelper.CreateBitmapSourceFromHBitmap(
                hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (bmp == null) return null;
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (factory != null) Marshal.FinalReleaseComObject(factory);
        }
    }

    // 兜底：系统大图标（32px），仅在 shell 接口失败时使用
    private static ImageSource? ExtractViaShellInfo(string path)
    {
        var info = new SHFILEINFOW();
        uint size = (uint)Marshal.SizeOf<SHFILEINFOW>();
        if (SHGetFileInfo(path, 0, ref info, size, SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero)
            return null;
        try
        {
            var bmp = ImagingHelper.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (bmp == null) return null;
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
