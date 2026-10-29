using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToniaDeskBox.Services;

public static class ShortcutService
{
    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void ShowProperties(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "properties" });
    }

    public static void OpenContainingFolder(string path)
    {
        var target = path;
        if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try { target = ResolveTarget(path) ?? path; }
            catch { target = path; }
        }

        if (File.Exists(target) || Directory.Exists(target))
            Process.Start("explorer.exe", $"/select,\"{target}\"");
        else
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    public static string? ResolveTarget(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(lnkPath);
            return (string)lnk.TargetPath;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public static ImageSource? GetIcon(string path)
    {
        var info = new SHFILEINFOW();
        uint size = (uint)Marshal.SizeOf<SHFILEINFOW>();
        if (SHGetFileInfo(path, 0, ref info, size, SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero)
            return null;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
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
}
