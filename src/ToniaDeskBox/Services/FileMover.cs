using System.IO;
using System.Runtime.InteropServices;
using ToniaDeskBox.Models;

namespace ToniaDeskBox.Services;

public static class FileMover
{
    public static bool ExistsConflict(string target) => File.Exists(target) || Directory.Exists(target);

    public static void MoveTo(string src, string target)
    {
        if (Directory.Exists(src))
        {
            try
            {
                Directory.Move(src, target);
                NotifyShellDeleted(src);
                return;
            }
            catch (IOException) when (!string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            {
                // 跨卷移动：Directory.Move 不支持，退化为复制+删除
            }
            CopyDirectory(src, target);
            Directory.Delete(src, true);
            NotifyShellDeleted(src);
        }
        else
        {
            File.Move(src, target);
            NotifyShellDeleted(src);
        }
    }

    private static void CopyDirectory(string src, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(target, Path.GetFileName(d)));
    }

    public static List<string> MoveBackToDesktop(IEnumerable<string> paths, out List<string> skipped)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var moved = new List<string>();
        skipped = new List<string>();
        foreach (var src in paths)
        {
            var name = Path.GetFileName(src);
            var target = Path.Combine(desktop, name);
            if (ExistsConflict(target))
            {
                skipped.Add(name);
                continue;
            }
            try
            {
                MoveTo(src, target);
                moved.Add(name);
            }
            catch
            {
                skipped.Add(name);
            }
        }
        if (moved.Count > 0) NotifyShellDirChanged(desktop);
        return moved;
    }

    private const int SHCNE_DELETE = 0x00000004;
    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public static void NotifyShellDeleted(string path)
    {
        var ptr = Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(SHCNE_DELETE, SHCNF_PATHW, ptr, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static void NotifyShellDirChanged(string dir)
    {
        var ptr = Marshal.StringToHGlobalUni(dir);
        try { SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, ptr, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
