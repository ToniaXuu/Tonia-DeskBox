using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ToniaDeskBox.Services;

public static class ShortcutService
{
    public static void Open(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            ShellOpen(path);
            return;
        }

        // 手动解析 .lnk 再启动：修正工作目录、过滤宿主环境变量（否则 Electron 应用会静默退出）
        try
        {
            var (target, args, workDir) = ResolveLnk(path);
            if (string.IsNullOrEmpty(target) || (!File.Exists(target) && !Directory.Exists(target)))
            {
                ShellOpen(path);
                return;
            }

            var ext = Path.GetExtension(target);
            if (File.Exists(target) && ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    StartFiltered(target, args, workDir);
                    return;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
                {
                    // 需要提权（UAC），交给 shell 弹 UAC
                    ShellOpen(path);
                    return;
                }
                catch
                {
                    // 启动失败退回 shell 打开整个 .lnk
                }
            }
            ShellOpen(target);
        }
        catch
        {
            ShellOpen(path);
        }
    }

    private static void ShellOpen(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static void StartFiltered(string target, string args, string workDir)
    {
        var psi = new ProcessStartInfo(target)
        {
            Arguments = args,
            UseShellExecute = false,
            WorkingDirectory = ResolveWorkDir(workDir, target),
        };
        foreach (var key in psi.EnvironmentVariables.Keys.Cast<string>().ToList())
        {
            if (key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase))
                psi.EnvironmentVariables.Remove(key);
        }
        Process.Start(psi);
    }

    private static string ResolveWorkDir(string workDir, string target)
    {
        if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
            return workDir;
        var dir = Path.GetDirectoryName(target);
        return dir != null && Directory.Exists(dir) ? dir : AppContext.BaseDirectory;
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

    public static (string Target, string Args, string WorkDir) ResolveLnk(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(lnkPath);
            return ((string)lnk.TargetPath, (string)lnk.Arguments, (string)lnk.WorkingDirectory);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public static string? ResolveTarget(string lnkPath)
    {
        try
        {
            var (target, _, _) = ResolveLnk(lnkPath);
            return target;
        }
        catch
        {
            return null;
        }
    }
}
