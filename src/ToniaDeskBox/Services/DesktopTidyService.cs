using System.IO;
using ToniaDeskBox.Models;

namespace ToniaDeskBox.Services;

/// <summary>桌面条目的归类。顺序即预览窗里的分组顺序。</summary>
public enum TidyCategory
{
    Shortcut = 0,
    Document = 1,
    Image = 2,
    Video = 3,
    Audio = 4,
    Archive = 5,
    Folder = 6,
    Other = 7,
}

public sealed class TidyItem
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public bool IsDirectory { get; init; }
    public TidyCategory Category { get; init; }

    /// <summary>是否勾选。默认全选 —— 一键整理的价值就是"装完立刻见效"，但每一项都能改。</summary>
    public bool IsSelected { get; set; } = true;
}

public sealed class TidyGroup
{
    public TidyCategory Category { get; init; }
    public string Title { get; init; } = "";
    /// <summary>搬进哪个盒子（默认 = 分类名，重名时复用已有盒子）。</summary>
    public string TargetBoxName { get; set; } = "";
    public List<TidyItem> Items { get; init; } = new();

    public IEnumerable<TidyItem> Selected => Items.Where(i => i.IsSelected);
    public int SelectedCount => Items.Count(i => i.IsSelected);
    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.Size);
    public bool AllSelected => Items.Count > 0 && Items.All(i => i.IsSelected);

    public void SetAll(bool selected)
    {
        foreach (var i in Items) i.IsSelected = selected;
    }
}

/// <summary>一次整理要搬的东西：源路径 → 目标盒子目录。</summary>
public sealed record TidyMove(string Source, string Name, string TargetDir, string TargetBox);

public sealed class TidyProgress
{
    public int Done { get; init; }
    public int Total { get; init; }
    public string Current { get; init; } = "";
    public double Percent => Total <= 0 ? 100 : Math.Clamp(Done * 100.0 / Total, 0, 100);
}

public sealed class TidyResult
{
    public int Moved { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    /// <summary>成功搬入的条目，按目标盒子分组时要用。</summary>
    public IReadOnlyList<TidyMove> MovedItems { get; init; } = Array.Empty<TidyMove>();
    /// <summary>"名称：原因"，用于给用户一个如实的交代。</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 一键整理：扫桌面 → 按扩展名分组 → 预览勾选 → 批量搬进盒子。
///
/// 为什么这件事值得做：首次使用成本就是留存率。以前 20 个图标要手工拖 20 次，
/// 现在默认全部勾好、按类型落进对应盒子，重复执行还会复用同名盒子。
///
/// 安全边界：
/// - 只扫**用户桌面**顶层（公用桌面的系统快捷方式需要管理员权限，不动）；
/// - 跳过 desktop.ini，跳过数据根 / 配置目录本身（数据根放在桌面上的绿色版不会自己吃自己）；
/// - 目标已存在同名条目时跳过而不是覆盖，原文件留在桌面；
/// - 搬动仍复用 <see cref="FileMover"/>，跨卷自动退化为复制+删除，并通知资源管理器刷新。
/// </summary>
public static class DesktopTidyService
{
    private static readonly Dictionary<TidyCategory, (string Title, HashSet<string> Extensions)> Categories = new()
    {
        [TidyCategory.Shortcut] = ("快捷方式", new(StringComparer.OrdinalIgnoreCase)
        {
            ".lnk", ".url",
        }),
        [TidyCategory.Document] = ("文档", new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".pdf", ".txt", ".md",
            ".rtf", ".csv", ".wps", ".et", ".dps", ".one", ".epub",
        }),
        [TidyCategory.Image] = ("图片", new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tif", ".tiff",
            ".psd", ".ai", ".raw", ".heic",
        }),
        [TidyCategory.Video] = ("视频", new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".rmvb", ".webm", ".m4v",
        }),
        [TidyCategory.Audio] = ("音频", new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".ape",
        }),
        [TidyCategory.Archive] = ("压缩包", new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab",
        }),
    };

    public static string CategoryTitle(TidyCategory c) =>
        Categories.TryGetValue(c, out var meta) ? meta.Title : "其他";

    /// <summary>桌面条目归到哪一类。目录一律进「文件夹」。</summary>
    public static TidyCategory Categorize(string name, bool isDirectory)
    {
        if (isDirectory) return TidyCategory.Folder;
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) return TidyCategory.Other;
        foreach (var (category, meta) in Categories)
        {
            if (meta.Extensions.Contains(ext)) return category;
        }
        return TidyCategory.Other;
    }

    public static string DesktopDir() =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>
    /// 扫桌面并分组。只读，不移动任何东西 —— 这一步本身就是"试运行"。
    /// </summary>
    /// <param name="desktopDir">桌面目录，测试时可指向临时目录。</param>
    /// <param name="excludedRoots">不参与整理的目录（数据根、配置目录）。</param>
    public static List<TidyGroup> Scan(string desktopDir, IEnumerable<string>? excludedRoots = null)
    {
        var groups = new Dictionary<TidyCategory, TidyGroup>();
        if (!Directory.Exists(desktopDir)) return new List<TidyGroup>();

        var excluded = (excludedRoots ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(SafeFullPath)
            .Where(r => r.Length > 0)
            .ToArray();

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(desktopDir).ToList();
        }
        catch
        {
            return new List<TidyGroup>();
        }

        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsUnderAny(path, excluded)) continue;

            bool isDir;
            long size = 0;
            try
            {
                // 目录大小不预先统计：扫几百 MB 的目录只为显示一个数字，代价太高
                isDir = Directory.Exists(path);
                if (!isDir) size = new FileInfo(path).Length;
            }
            catch
            {
                // 权限 / 占用：当作普通条目照常展示，搬动时再报错
                isDir = false;
            }

            var category = Categorize(name, isDir);
            if (!groups.TryGetValue(category, out var group))
            {
                group = new TidyGroup
                {
                    Category = category,
                    Title = CategoryTitle(category),
                    TargetBoxName = CategoryTitle(category),
                };
                groups[category] = group;
            }

            group.Items.Add(new TidyItem
            {
                Path = path,
                Name = name,
                Size = size,
                IsDirectory = isDir,
                Category = category,
            });
        }

        return groups.Values
            .OrderBy(g => (int)g.Category)
            .Where(g => g.Items.Count > 0)
            .ToList();
    }

    /// <summary>
    /// 把勾选项翻译成"源 → 目标目录"的清单。目标目录由调用方给出
    /// （因为盒子可能还不存在，需要先建配置再落盘）。
    /// </summary>
    public static List<TidyMove> BuildMoves(
        IEnumerable<TidyGroup> groups, Func<string, string> boxDirResolver)
    {
        var moves = new List<TidyMove>();
        foreach (var group in groups)
        {
            var dir = boxDirResolver(group.TargetBoxName);
            foreach (var item in group.Selected)
                moves.Add(new TidyMove(item.Path, item.Name, dir, group.TargetBoxName));
        }
        return moves;
    }

    /// <summary>
    /// 执行搬运。可在后台线程调用；不碰 UI、不读写配置。
    /// 单个条目失败不影响其余条目，失败原因原样带回去。
    /// </summary>
    public static TidyResult MoveAll(
        IReadOnlyList<TidyMove> moves,
        IProgress<TidyProgress>? progress = null,
        CancellationToken token = default)
    {
        var moved = new List<TidyMove>();
        var problems = new List<string>();
        int skipped = 0, failed = 0, done = 0;

        foreach (var move in moves)
        {
            token.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new TidyProgress { Done = done, Total = moves.Count, Current = move.Name });

            try
            {
                Directory.CreateDirectory(move.TargetDir);
                var target = Path.Combine(move.TargetDir, move.Name);

                // 同名已在盒子里：跳过而不是覆盖，桌面上那份原样保留
                if (FileMover.ExistsConflict(target))
                {
                    skipped++;
                    problems.Add($"{move.Name}：盒子里已有同名条目，已跳过");
                    continue;
                }

                FileMover.MoveTo(move.Source, target);
                moved.Add(move);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                problems.Add($"{move.Name}：{ex.Message}");
            }
        }

        return new TidyResult
        {
            Moved = moved.Count,
            Skipped = skipped,
            Failed = failed,
            MovedItems = moved,
            Problems = problems,
        };
    }

    /// <summary>
    /// 给新盒子找一个不与现有盒子重名的名字（和导入合并用的是同一套避让规则）。
    /// </summary>
    public static string UniqueBoxName(string seed, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (used.Add(seed) && ConfigStore.IsSafeSegment(seed)) return seed;
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{seed} ({i})";
            if (used.Add(candidate) && ConfigStore.IsSafeSegment(candidate)) return candidate;
        }
        return $"盒子-{Guid.NewGuid():N}"[..12];
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return ""; }
    }

    private static bool IsUnderAny(string path, string[] roots)
    {
        if (roots.Length == 0) return false;
        var full = SafeFullPath(path);
        if (full.Length == 0) return false;
        foreach (var root in roots)
        {
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
