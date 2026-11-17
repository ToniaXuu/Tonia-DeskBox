using System.IO;
using ToniaDeskBox.Models;

namespace ToniaDeskBox.Services;

/// <summary>导入方式。两者的"破坏半径"完全不同，所以必须让用户选，而不是替他默认。</summary>
public enum ImportMode
{
    /// <summary>合并：现有盒子原样保留，备份里的盒子追加进来。只增不删。</summary>
    Merge,
    /// <summary>覆盖：用备份整体替换当前数据，旧数据改名留底。</summary>
    Replace,
}

public sealed class ImportApplyResult
{
    public string DataRoot { get; init; } = "";
    /// <summary>导入前的数据被改名到哪了；null 表示原来没有数据。</summary>
    public string? ReplacedDataPath { get; init; }
    /// <summary>导入前的 config.json 副本；null 表示原来没有配置。</summary>
    public string? ConfigBackupPath { get; init; }
    public int Boxes { get; init; }
}

/// <summary>合并计划里的一项：staging 里的目录 → 数据根下的新目录 + 新的盒子配置。</summary>
public sealed class MergeItem
{
    /// <summary>staging 下的源目录名（= 备份里的盒子名）。空串表示"包根目录的散文件"。</summary>
    public string SourceDir { get; init; } = "";
    public string FinalName { get; init; } = "";
    public BoxConfig Box { get; init; } = null!;
}

public sealed class MergeApplyResult
{
    public IReadOnlyList<BoxConfig> AddedBoxes { get; init; } = Array.Empty<BoxConfig>();
    public int FoldersMoved { get; init; }
    public int RenamedForConflict { get; init; }
}

/// <summary>
/// 导入的「落地」环节。
///
/// 两种模式共用一条铁律：只做整目录 rename，绝不逐文件删除——数据永远不因导入而消失。
/// 覆盖模式把旧数据整体改名留底；合并模式只往数据根里加目录，现有盒子一个都不动。
/// </summary>
public static class ImportService
{
    /// <summary>
    /// 临时解压目录：放在数据根旁边，保证同卷。
    /// 名字带时间戳，避免上一次失败留下的残骸被复用。
    /// </summary>
    public static string CreateStagingDir(string dataRoot)
    {
        var root = Normalize(dataRoot);
        var parent = Path.GetDirectoryName(root)
                     ?? throw new IOException($"数据根没有父目录，无法解压：{root}");
        Directory.CreateDirectory(parent);
        var name = Path.GetFileName(root) + ".importing-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(parent, name);

        // 同名残骸（同一秒内重试）先让路
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>确认窗要提前把"旧数据会去哪"写清楚，所以这里先把目标名算出来。</summary>
    public static string PreviewBackupPath(string dataRoot, string stamp) =>
        $"{Normalize(dataRoot)}.before-import-{stamp}";

    /// <param name="stamp">时间戳由调用方一次算好，确认窗展示的路径和实际落地的一致。</param>
    public static ImportApplyResult Apply(
        string stagingDir, string dataRoot, string configDir, int boxCount, string stamp)
    {
        var root = Normalize(dataRoot);
        if (!Directory.Exists(stagingDir))
            throw new DirectoryNotFoundException($"解压目录不存在：{stagingDir}");

        // rename 必须同卷，跨卷就只能退化成"复制 + 删除"，那不再具备原子性
        var stagingParent = Normalize(Path.GetDirectoryName(stagingDir)!);
        var rootParent = Normalize(Path.GetDirectoryName(root)!);
        if (!string.Equals(stagingParent, rootParent, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"解压目录与数据根不在同一磁盘：{stagingDir} / {root}");

        var replacedTo = PreviewBackupPath(root, stamp);
        bool movedOld = false;

        try
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Move(root, replacedTo);
                    movedOld = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 盒子里有文件正被别的程序打开时 Windows 不允许改目录名。
                    // 此时还没有任何改动，直接告诉用户关掉占用即可。
                    throw new IOException(
                        $"无法替换数据目录，可能有文件正被其他程序使用：{root}", ex);
                }
            }

            Directory.Move(stagingDir, root);

            string? configBackup = null;
            var configPath = Path.Combine(configDir, "config.json");
            if (File.Exists(configPath))
            {
                configBackup = Path.Combine(configDir, $"config.before-import-{stamp}.json");
                File.Copy(configPath, configBackup, overwrite: true);
            }

            return new ImportApplyResult
            {
                DataRoot = root,
                ReplacedDataPath = movedOld ? replacedTo : null,
                ConfigBackupPath = configBackup,
                Boxes = boxCount,
            };
        }
        catch
        {
            // 回滚：新数据没就位就说明数据根此刻是空的，把旧目录名改回去
            try
            {
                if (movedOld && !Directory.Exists(root) && Directory.Exists(replacedTo))
                    Directory.Move(replacedTo, root);
            }
            catch
            {
                // 回滚都失败就只能靠日志留痕，至少旧数据还在 .before-import-* 里
            }
            throw;
        }
    }

    /// <summary>导入被取消 / 失败后清理临时目录。删不掉不致命，下次会另建一个。</summary>
    public static void TryDeleteStaging(string? stagingDir)
    {
        if (string.IsNullOrWhiteSpace(stagingDir)) return;
        try
        {
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        }
        catch
        {
            // 有文件被占用时残留一份临时目录，不影响功能
        }
    }

    // ---------- 合并模式 ----------

    /// <summary>
    /// 算出合并计划：给备份里的每个盒子安排一个**不与现有盒子重名**的落地名。
    ///
    /// 为什么要改名而不是直接覆盖同名目录？因为盒子的数据目录就是 <c>DataRoot\盒子名</c>。
    /// 合并时若沿用同名，就会出现两个盒子指同一个目录、后导入的覆盖先前的文件——
    /// 那等于"合并"偷偷变成了"覆盖"。所以重名一律加序号，各走各的目录。
    /// </summary>
    /// <param name="existingNames">当前所有盒子的名字，用来避让。</param>
    /// <param name="offsetStep">新盒子之间、以及与现有盒子的坐标错开量，避免完全叠在一起。</param>
    public static List<MergeItem> BuildMergePlan(
        BackupManifest manifest, IEnumerable<string> existingNames, int offsetStep = 28)
    {
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var plan = new List<MergeItem>();

        var sources = manifest.Config?.Boxes is { Count: > 0 } fromConfig
            ? fromConfig
            : manifest.Boxes.Select(b => new BoxConfig { Name = b.Name }).ToList();

        int index = 0;
        foreach (var src in sources)
        {
            var seed = string.IsNullOrWhiteSpace(src.Name) ? "导入的盒子" : src.Name.Trim();
            var finalName = UniqueName(seed, taken);
            index++;

            plan.Add(new MergeItem
            {
                SourceDir = seed,
                FinalName = finalName,
                Box = new BoxConfig
                {
                    // 新 Id：备份里的 Id 可能和现有盒子撞车
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Name = finalName,
                    X = src.X + offsetStep * index,
                    Y = src.Y + offsetStep * index,
                    Opacity = src.Opacity,
                    BackColor = src.BackColor,
                    Rounded = src.Rounded,
                    CornerSize = src.CornerSize,
                    Topmost = src.Topmost,
                    Hidden = src.Hidden,
                    W = src.W,
                    H = src.H,
                    // 文件按目录名归位：目录改名了，Order 仍存文件名，不受影响
                    Order = src.Order?.ToList() ?? new List<string>(),
                },
            });
        }

        return plan;
    }

    /// <summary>
    /// 合并落盘：把 staging 里各盒子目录搬进数据根。只增不删，失败则把已搬的搬回去。
    /// </summary>
    public static MergeApplyResult MergeStaging(
        string stagingDir, string dataRoot, IReadOnlyList<MergeItem> plan)
    {
        var root = Normalize(dataRoot);
        Directory.CreateDirectory(root);

        var added = new List<BoxConfig>();
        // (源目录, 目标目录)：源为 null 表示"新建的空目录"，回滚时要删掉
        var moved = new List<(string? Src, string Dst)>();
        int renamed = 0;

        try
        {
            foreach (var item in plan)
            {
                var srcDir = string.IsNullOrEmpty(item.SourceDir) ? null : Path.Combine(stagingDir, item.SourceDir);
                var dstDir = Path.Combine(root, item.FinalName);

                if (Directory.Exists(dstDir))
                    throw new IOException($"目标盒子目录已存在，已停止合并：{dstDir}");

                if (srcDir != null && Directory.Exists(srcDir))
                {
                    Directory.Move(srcDir, dstDir);
                    moved.Add((srcDir, dstDir));
                }
                else
                {
                    // 备份里声明了这个盒子但目录是空的：建空目录，界面上能看到一个空盒
                    Directory.CreateDirectory(dstDir);
                    moved.Add((null, dstDir));
                }

                if (!string.Equals(item.SourceDir, item.FinalName, StringComparison.Ordinal)) renamed++;
                added.Add(item.Box);
            }

            // 包根目录的散文件（正常情况下不该有）：单独收进一个盒子，别让它们无处可去
            var loose = Directory.Exists(stagingDir)
                ? Directory.GetFiles(stagingDir).ToList()
                : new List<string>();
            if (loose.Count > 0)
            {
                var taken = plan.Select(p => p.FinalName)
                    .Concat(Directory.GetDirectories(root).Select(Path.GetFileName)!)
                    .Where(n => !string.IsNullOrEmpty(n))!;
                var name = UniqueName("导入的文件", new HashSet<string>(taken!, StringComparer.OrdinalIgnoreCase));
                var dstDir = Path.Combine(root, name);
                Directory.CreateDirectory(dstDir);
                moved.Add((null, dstDir));

                foreach (var f in loose)
                {
                    var target = Path.Combine(dstDir, Path.GetFileName(f));
                    try { File.Move(f, target); }
                    catch (IOException) { }   // 同名散文件已存在：保留先到的那份
                }

                added.Add(new BoxConfig
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Name = name,
                    X = 140,
                    Y = 140,
                    Order = loose.Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToList(),
                });
            }

            return new MergeApplyResult
            {
                AddedBoxes = added,
                FoldersMoved = moved.Count,
                RenamedForConflict = renamed,
            };
        }
        catch
        {
            // 回滚：搬进去的搬回 staging，新建的空目录删掉。配置还没保存，等于什么都没发生。
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                var (src, dst) = moved[i];
                try
                {
                    if (src != null && Directory.Exists(dst) && !Directory.Exists(src))
                        Directory.Move(dst, src);
                    else if (src == null && Directory.Exists(dst))
                        Directory.Delete(dst, recursive: true);
                }
                catch
                {
                    // 回滚失败的目录留在数据根里，日志里有记录
                }
            }
            throw;
        }
    }

    /// <summary>重名就加 "(2)"、"(3)"……；结果必须能当目录名用。</summary>
    private static string UniqueName(string seed, HashSet<string> taken)
    {
        if (taken.Add(seed) && ConfigStore.IsSafeSegment(seed)) return seed;

        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{seed} ({i})";
            if (taken.Add(candidate) && ConfigStore.IsSafeSegment(candidate)) return candidate;
        }
        var fallback = $"{seed}-{Guid.NewGuid():N}"[..8];
        taken.Add(fallback);
        return fallback;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
