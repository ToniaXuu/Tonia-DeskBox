using System.IO;
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
                return;
            }
            catch (IOException) when (!string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            {
                // 跨卷移动：Directory.Move 不支持，退化为复制+删除
            }
            CopyDirectory(src, target);
            Directory.Delete(src, true);
        }
        else
        {
            File.Move(src, target);
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
        return moved;
    }
}
