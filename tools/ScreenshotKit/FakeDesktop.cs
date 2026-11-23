using System.IO;

namespace ScreenshotKit;

/// <summary>
/// 一个"看起来像真的"的桌面：给一键整理预览窗造素材。
///
/// 全部落在临时目录里、退出即删；内容都是本机系统文件或空文件，
/// 不读取、也不搬运用户真实桌面的任何东西。
/// </summary>
internal static class FakeDesktop
{
    private static readonly string System32 =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

    public static string Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "tdb-desktop-" + Guid.NewGuid().ToString("N")[..6]);
        var desktop = Path.Combine(root, "Desktop");
        Directory.CreateDirectory(desktop);

        // 快捷方式：用 WScript.Shell 真造 .lnk，图标才会是目标程序的图标
        Shortcut(desktop, "微信.lnk", Path.Combine(System32, "mstsc.exe"));
        Shortcut(desktop, "VS Code.lnk", Path.Combine(System32, "notepad.exe"));
        Shortcut(desktop, "回收站.lnk", Path.Combine(System32, "charmap.exe"));

        // 文档
        Touch(desktop, "季度汇报.pptx");
        Touch(desktop, "会议纪要.docx");
        Touch(desktop, "报价单.xlsx");
        Touch(desktop, "桌面整理方案.pdf");

        // 图片 / 视频 / 音频 / 压缩包
        CopyIcon(desktop, "截图 2026-11-20.png");
        Touch(desktop, "海报.psd");
        Touch(desktop, "演示录屏.mp4");
        Touch(desktop, "会议录音.m4a");
        Touch(desktop, "素材包.zip");
        Touch(desktop, "旧工程备份.7z");

        // 文件夹 + 其他
        Directory.CreateDirectory(Path.Combine(desktop, "学习资料"));
        Touch(desktop, "说明");

        return desktop;
    }

    private static void Shortcut(string dir, string name, string target)
    {
        if (!File.Exists(target)) return;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(Path.Combine(dir, name));
            link.TargetPath = target;
            link.Save();
        }
        catch
        {
            // 造不出真是快捷方式就退化成普通文件：截图仍能体现分组，只是图标不理想
            Touch(dir, name);
        }
    }

    private static void Touch(string dir, string name) =>
        File.WriteAllText(Path.Combine(dir, name), "sample");

    private static void CopyIcon(string dir, string name)
    {
        try
        {
            var repo = RepoRoot();
            var src = repo == null ? null : Path.Combine(repo, "assets", "icons", "preview.png");
            if (src != null && File.Exists(src))
            {
                File.Copy(src, Path.Combine(dir, name), overwrite: true);
                return;
            }
        }
        catch
        {
            // 落到下面的空文件
        }
        Touch(dir, name);
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ToniaDeskBox.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
