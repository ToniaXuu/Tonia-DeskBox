using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ToniaDeskBox.Services;

/// <summary>整理时被搬走的一项。恢复要靠它找到「从哪来、回哪去」。</summary>
public sealed class TidyRestoreItem
{
    public string Name { get; set; } = "";
    /// <summary>整理前的桌面路径。</summary>
    public string OriginalPath { get; set; } = "";
    /// <summary>整理后在盒子里的路径。用户可能又挪过，所以恢复前要重新判断是否存在。</summary>
    public string BoxPath { get; set; } = "";
    public string BoxName { get; set; } = "";
    public bool IsDirectory { get; set; }
}

/// <summary>
/// 一次整理的完整快照。
///
/// 「布局元数据」与「文件备份」是两件独立的事：
/// - 布局（图标位置 + 自动排列状态）**永远保存**。没有它，恢复只能把文件放回桌面，
///   图标会全堆在「自动排列」的默认位置上 —— 那不叫恢复；
/// - 备份是可选项，且只复制**本次参与整理**的条目。没勾选的文件不得被移动、备份或修改，
///   这是用户对「整理」这个动作最基本的信任前提。
/// </summary>
public sealed class TidyState
{
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }

    /// <summary>布局是否真的采到了。采不到时仍保存快照（文件还能恢复），并在 UI 里如实说明。</summary>
    public bool LayoutCaptured { get; set; }
    public string? LayoutError { get; set; }
    public bool AutoArrange { get; set; }
    public List<DesktopIconPos> Icons { get; set; } = new();

    public List<TidyRestoreItem> Items { get; set; } = new();

    public string? BackupDir { get; set; }
    public int BackupFiles { get; set; }
    public long BackupBytes { get; set; }

    public bool HasBackup => !string.IsNullOrEmpty(BackupDir) && Directory.Exists(BackupDir);
}

/// <summary>快照的落盘位置与读写。放在配置目录下，与盒子数据分开、随版本升级不影响。</summary>
public static class TidyStateStore
{
    public const string FileName = "tidy-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathFor(string configDir) => Path.Combine(configDir, FileName);

    public static void Save(string configDir, TidyState state)
    {
        Directory.CreateDirectory(configDir);
        var path = PathFor(configDir);
        var tmp = path + ".tmp";
        // 先写临时文件再换名：中途崩了也不会留下半个 JSON 让下一次恢复直接失效
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>读取上一次整理快照。没有记录时返回 <c>null</c> 且 <paramref name="error"/> 为空。</summary>
    public static TidyState? TryLoad(string configDir, out string error)
    {
        error = "";
        var path = PathFor(configDir);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var state = JsonSerializer.Deserialize<TidyState>(json, JsonOptions);
            if (state == null)
            {
                error = "整理记录文件是空的";
                return null;
            }
            if (state.Version > 1)
            {
                error = $"整理记录来自更新的版本（v{state.Version}），当前程序读不懂";
                return null;
            }
            return state;
        }
        catch (Exception ex)
        {
            error = $"整理记录损坏：{ex.Message}";
            return null;
        }
    }

    public static void Clear(string configDir)
    {
        try { File.Delete(PathFor(configDir)); }
        catch { /* 删不掉就算了，下次 Save 会覆盖 */ }
    }
}

public sealed class TidyBackupResult
{
    public string Dir { get; init; } = "";
    public int Files { get; init; }
    public long Bytes { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 整理前的可选备份。
///
/// 硬约束：**只复制传进来的这些路径**，绝不遍历桌面目录 —— 用户只勾了 5 个文件，
/// 就不能因为"顺手"把其余 20 个也复制一份（既占空间，也让人怀疑自己没勾的东西被动过）。
/// </summary>
public static class TidyBackupService
{
    public static string RootFor(string configDir) => Path.Combine(configDir, "tidy-backups");

    public static TidyBackupResult Create(
        IReadOnlyList<string> sources,
        string configDir,
        string stamp,
        IProgress<TidyProgress>? progress = null,
        CancellationToken token = default)
    {
        var dir = Path.Combine(RootFor(configDir), stamp);
        Directory.CreateDirectory(dir);

        // 先数一遍文件数，进度才有分母（目录可能是几百个文件）
        int totalFiles = 0;
        long totalBytes = 0;
        foreach (var src in sources)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(src)) CountDirectory(src, ref totalFiles, ref totalBytes);
            else if (File.Exists(src))
            {
                totalFiles++;
                try { totalBytes += new FileInfo(src).Length; } catch { }
            }
        }

        int done = 0, skipped = 0;
        long copied = 0;
        var problems = new List<string>();

        void Report(string current)
        {
            progress?.Report(new TidyProgress
            {
                Done = done,
                Total = totalFiles,
                Current = current,
            });
        }

        Report("准备备份…");

        foreach (var src in sources)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(src);
            if (string.IsNullOrEmpty(name)) continue;

            var target = Path.Combine(dir, name);
            // 理论上桌面顶层不会重名，但目录里可能嵌套同名文件，冲突时加序号而不是覆盖
            target = UniqueTarget(target);

            try
            {
                if (Directory.Exists(src))
                {
                    CopyDirectory(src, target, ref done, ref copied, totalFiles, token,
                        n => Report($"{name}/{n}"));
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(src, target, overwrite: false);
                    try { copied += new FileInfo(target).Length; } catch { }
                    done++;
                    Report(name);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped++;
                problems.Add($"{name}：{ex.Message}");
            }
        }

        return new TidyBackupResult
        {
            Dir = dir,
            Files = done,
            Bytes = copied,
            Skipped = skipped,
            Problems = problems,
        };
    }

    private static void CopyDirectory(string src, string target, ref int done, ref long copied,
        int total, CancellationToken token, Action<string> onFile)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(src))
        {
            token.ThrowIfCancellationRequested();
            var dest = UniqueTarget(Path.Combine(target, Path.GetFileName(file)));
            File.Copy(file, dest, overwrite: false);
            try { copied += new FileInfo(dest).Length; } catch { }
            done++;
            onFile(Path.GetFileName(file));
        }
        foreach (var sub in Directory.GetDirectories(src))
        {
            token.ThrowIfCancellationRequested();
            CopyDirectory(sub, UniqueTarget(Path.Combine(target, Path.GetFileName(sub))),
                ref done, ref copied, total, token, onFile);
        }
    }

    private static void CountDirectory(string dir, ref int files, ref long bytes)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                files++;
                try { bytes += new FileInfo(f).Length; } catch { }
            }
            foreach (var sub in Directory.GetDirectories(dir))
                CountDirectory(sub, ref files, ref bytes);
        }
        catch
        {
            // 无权限的子目录直接跳过统计，备份阶段会把它记进 problems
        }
    }

    private static string UniqueTarget(string path)
    {
        if (!FileMover.ExistsConflict(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!FileMover.ExistsConflict(candidate)) return candidate;
        }
        return path;
    }
}

public enum TidyRestoreOrigin
{
    /// <summary>文件还在盒子里，直接搬回去。</summary>
    Box = 0,
    /// <summary>盒子里没了，但整理前的备份还在。</summary>
    Backup = 1,
    /// <summary>两边都找不到 —— 只能如实告诉用户。</summary>
    Missing = 2,
}

public sealed class TidyRestorePlanItem
{
    public TidyRestoreItem Item { get; init; } = null!;
    public TidyRestoreOrigin Origin { get; init; }
    public string SourcePath { get; init; } = "";
    /// <summary>不可恢复的具体原因（Origin 为 Missing 时才有意义）。</summary>
    public string Problem { get; init; } = "";
}

public sealed class TidyRestorePlan
{
    public IReadOnlyList<TidyRestorePlanItem> Items { get; init; } = Array.Empty<TidyRestorePlanItem>();
    public int RestorableCount => Items.Count(i => i.Origin != TidyRestoreOrigin.Missing);
    public int MissingCount => Items.Count(i => i.Origin == TidyRestoreOrigin.Missing);
    public int FromBackupCount => Items.Count(i => i.Origin == TidyRestoreOrigin.Backup);
}

public sealed class TidyRestoreResult
{
    public int Restored { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    /// <summary>成功放回桌面的条目 —— 上层据此把它们从盒子 Order 里摘掉。</summary>
    public IReadOnlyList<TidyRestoreItem> RestoredItems { get; init; } = Array.Empty<TidyRestoreItem>();
}

/// <summary>
/// 一键恢复：把上次整理搬走的文件放回桌面原位，再还原图标布局。
///
/// 执行顺序是有讲究的：**先文件、后布局**。图标位置是按名字匹配的，文件没回到桌面时
/// 桌面上根本没有这些项，先设位置等于白设。
/// </summary>
public static class TidyRestoreService
{
    /// <summary>只读体检：逐项判断能从哪儿恢复。耗在 IO 上但很轻，可以在 UI 线程调用。</summary>
    public static TidyRestorePlan BuildPlan(TidyState state, string desktopDir)
    {
        var items = new List<TidyRestorePlanItem>(state.Items.Count);
        foreach (var item in state.Items)
        {
            var boxPath = string.IsNullOrEmpty(item.BoxPath)
                ? Path.Combine(desktopDir, item.Name)   // 老记录没有 BoxPath 时兜底
                : item.BoxPath;

            if (File.Exists(boxPath) || Directory.Exists(boxPath))
            {
                items.Add(new TidyRestorePlanItem
                {
                    Item = item,
                    Origin = TidyRestoreOrigin.Box,
                    SourcePath = boxPath,
                });
                continue;
            }

            var backupPath = state.HasBackup
                ? Path.Combine(state.BackupDir!, item.Name)
                : "";
            if (backupPath.Length > 0 && (File.Exists(backupPath) || Directory.Exists(backupPath)))
            {
                items.Add(new TidyRestorePlanItem
                {
                    Item = item,
                    Origin = TidyRestoreOrigin.Backup,
                    SourcePath = backupPath,
                });
                continue;
            }

            items.Add(new TidyRestorePlanItem
            {
                Item = item,
                Origin = TidyRestoreOrigin.Missing,
                Problem = $"{item.Name}：盒子里找不到，也没有可用的备份",
            });
        }

        return new TidyRestorePlan { Items = items };
    }

    /// <summary>执行恢复。可在后台线程调用；单个条目失败不影响其余条目。</summary>
    public static TidyRestoreResult Execute(
        TidyRestorePlan plan,
        IProgress<TidyProgress>? progress = null,
        CancellationToken token = default)
    {
        var problems = new List<string>();
        var restored = new List<TidyRestoreItem>();
        int skipped = 0, failed = 0, done = 0;

        foreach (var entry in plan.Items)
        {
            token.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new TidyProgress
            {
                Done = done,
                Total = plan.Items.Count,
                Current = entry.Item.Name,
            });

            if (entry.Origin == TidyRestoreOrigin.Missing)
            {
                failed++;
                problems.Add(entry.Problem);
                continue;
            }

            var target = entry.Item.OriginalPath;
            if (string.IsNullOrEmpty(target))
                target = Path.Combine(DesktopTidyService.DesktopDir(), entry.Item.Name);

            try
            {
                // 桌面上已经有一份同名的：不覆盖，留在盒子里，如实报告
                if (FileMover.ExistsConflict(target))
                {
                    skipped++;
                    problems.Add($"{entry.Item.Name}：桌面上已有同名条目，未覆盖");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (entry.Origin == TidyRestoreOrigin.Box)
                {
                    FileMover.MoveTo(entry.SourcePath, target);
                }
                else
                {
                    if (Directory.Exists(entry.SourcePath))
                        CopyDirectoryBack(entry.SourcePath, target);
                    else
                        File.Copy(entry.SourcePath, target, overwrite: false);
                }

                restored.Add(entry.Item);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                problems.Add($"{entry.Item.Name}：{ex.Message}");
            }
        }

        FileMover.NotifyShellDirChanged(DesktopTidyService.DesktopDir());

        return new TidyRestoreResult
        {
            Restored = restored.Count,
            Skipped = skipped,
            Failed = failed,
            Problems = problems,
            RestoredItems = restored,
        };
    }

    private static void CopyDirectoryBack(string src, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectoryBack(sub, Path.Combine(target, Path.GetFileName(sub)));
    }
}
