using System.IO;

namespace ToniaDeskBox.Services;

/// <summary>迁移前的体量统计。</summary>
public sealed class StoragePlan
{
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>迁移进度快照（每帧不可变，方便直接绑定）。</summary>
public sealed class MigrationProgress
{
    public int DoneFiles { get; init; }
    public int TotalFiles { get; init; }
    public long DoneBytes { get; init; }
    public long TotalBytes { get; init; }
    public string Current { get; init; } = "";

    public double Percent
    {
        get
        {
            if (TotalBytes > 0) return Math.Clamp(DoneBytes * 100.0 / TotalBytes, 0, 100);
            if (TotalFiles > 0) return Math.Clamp(DoneFiles * 100.0 / TotalFiles, 0, 100);
            return 100;
        }
    }
}

public sealed class MigrationResult
{
    public bool Success { get; init; }
    public bool Canceled { get; init; }
    public string? Error { get; init; }
    /// <summary>成功但有需要告知的情况（例如原位置有条目删不掉）。</summary>
    public string? Warning { get; init; }
    public long MovedBytes { get; init; }
    public int MovedFiles { get; init; }
    public int TotalFiles { get; init; }
    /// <summary>复制失败的文件（相对路径 + 原因）。非空时源目录一定没被删。</summary>
    public List<string> Failures { get; } = new();
}

/// <summary>
/// 盒子存储位置迁移。核心安全约定：<b>先复制、校验通过后才删源</b>。
/// 任何一步失败或用户取消，源目录都保持完整，最多留下半份副本。
/// </summary>
public static class StorageMigration
{
    // 这些是配置/日志/备份，不属于盒子内容，迁移数据根时不该跟着走。
    // 只作用于根目录顶层，盒子内部的同名文件照常迁移。
    private static bool IsConfigArtifact(string fullPath, string root)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        if (rel.Contains(Path.DirectorySeparatorChar)) return false;   // 只看根目录顶层

        if (rel.StartsWith("config", StringComparison.OrdinalIgnoreCase)) return true;   // config.json / .bak / .before-restore / corrupt-*
        if (rel.Equals("error.log", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static StoragePlan Scan(string root)
    {
        int count = 0;
        long bytes = 0;
        if (!Directory.Exists(root)) return new StoragePlan();

        foreach (var file in SafeEnumerate(root))
        {
            if (IsConfigArtifact(file, root)) continue;
            try
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
            catch
            {
                // 读不到大小的条目仍然计入文件数，避免进度分母偏小
                count++;
            }
        }
        return new StoragePlan { FileCount = count, TotalBytes = bytes };
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        using var e = files.GetEnumerator();
        while (true)
        {
            string cur;
            try
            {
                if (!e.MoveNext()) yield break;
                cur = e.Current;
            }
            catch
            {
                // 无权限的子目录等：跳过整段枚举，不影响已拿到的文件
                yield break;
            }
            yield return cur;
        }
    }

    /// <summary>剩余可用空间（字节）。拿不到时返回 null（不阻断迁移）。</summary>
    public static long? FreeSpaceOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 校验目标根是否可用。返回 false 时 error 是给人看的原因。
    /// </summary>
    public static bool ValidateTarget(string target, string currentRoot, string configDir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(target))
        {
            error = "请选择存储位置";
            return false;
        }
        if (!Path.IsPathRooted(target))
        {
            error = "需要完整路径";
            return false;
        }

        string full, cur, cfg;
        try
        {
            full = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            cur = Path.GetFullPath(currentRoot).TrimEnd(Path.DirectorySeparatorChar);
            cfg = Path.GetFullPath(configDir).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            error = $"路径无效：{ex.Message}";
            return false;
        }

        if (full.Equals(cur, StringComparison.OrdinalIgnoreCase))
        {
            error = "与当前位置相同";
            return false;
        }

        // 盘符根（C:\、D:\）会让盒子目录散落在盘根，误伤面太大
        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            error = "不能直接使用磁盘根目录，请选择其下的文件夹";
            return false;
        }

        // 新旧根互为父子会形成嵌套/递归，绝对不允许
        if (IsUnder(full, cur))
        {
            error = "不能选择当前存储位置的子目录";
            return false;
        }
        if (IsUnder(cur, full))
        {
            error = "不能选择当前存储位置的上级目录";
            return false;
        }

        // 配置目录本身及其上级也不合适：盒子目录会和配置混在一起
        if (full.Equals(cfg, StringComparison.OrdinalIgnoreCase) || IsUnder(cfg, full))
        {
            error = "不能使用配置所在目录或其上级目录";
            return false;
        }

        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".tonia-write-test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            error = $"目标目录不可写：{ex.Message}";
            return false;
        }

        return true;
    }

    private static bool IsUnder(string child, string parent)
    {
        var p = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return child.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把 source 下的盒子内容复制到 target 并校验。默认不删源：
    /// 调用方应当在「配置已成功切到新根」之后再调 <see cref="PurgeSource"/>，
    /// 这样即使切换失败，源也始终完整。
    /// </summary>
    public static MigrationResult Migrate(
        string source,
        string target,
        IProgress<MigrationProgress>? progress,
        CancellationToken token,
        bool purgeSource = false)
    {
        var plan = Scan(source);
        if (plan.FileCount == 0)
        {
            return new MigrationResult
            {
                Success = true,
                TotalFiles = 0,
                MovedFiles = 0,
                MovedBytes = 0,
            };
        }

        var free = FreeSpaceOf(target);
        if (free.HasValue && free.Value < plan.TotalBytes + 16 * 1024 * 1024)
        {
            return new MigrationResult
            {
                Success = false,
                TotalFiles = plan.FileCount,
                Error = $"目标位置可用空间不足：需要约 {Format(plan.TotalBytes)}，当前可用 {Format(free.Value)}。",
            };
        }

        var files = new List<(string Src, string Rel, long Size)>(plan.FileCount);
        foreach (var f in SafeEnumerate(source))
        {
            if (IsConfigArtifact(f, source)) continue;
            long len = 0;
            try { len = new FileInfo(f).Length; } catch { }
            files.Add((f, Path.GetRelativePath(source, f), len));
        }

        int doneFiles = 0;
        long doneBytes = 0;
        long movedBytes = 0;
        var failures = new List<string>();
        var lastReport = DateTime.UtcNow;

        void Report(string current, bool force = false)
        {
            if (progress == null) return;
            var now = DateTime.UtcNow;
            if (!force && (now - lastReport).TotalMilliseconds < 120) return;
            lastReport = now;
            progress.Report(new MigrationProgress
            {
                DoneFiles = doneFiles,
                TotalFiles = files.Count,
                DoneBytes = doneBytes,
                TotalBytes = plan.TotalBytes,
                Current = current,
            });
        }

        Report("准备中…", force: true);

        try
        {
            foreach (var (src, rel, size) in files)
            {
                token.ThrowIfCancellationRequested();

                var dst = Path.Combine(target, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                    // 同名且同大小视作已复制（重复迁移/中断后重跑都能续上）
                    if (File.Exists(dst) && new FileInfo(dst).Length == size)
                    {
                        doneFiles++;
                        doneBytes += size;
                        movedBytes += size;
                        Report(rel);
                        continue;
                    }

                    CopyFile(src, dst, token, chunk => doneBytes += chunk);
                    try
                    {
                        File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                    }
                    catch
                    {
                        // 时间戳设不上不影响文件本身
                    }

                    doneFiles++;
                    movedBytes += size;
                    Report(rel);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{rel}：{ex.Message}");
                    // 失败的文件也要推进分母，否则进度条会卡住
                    doneFiles++;
                    doneBytes += size;
                    Report(rel);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new MigrationResult
            {
                Success = false,
                Canceled = true,
                TotalFiles = files.Count,
                MovedFiles = doneFiles,
                MovedBytes = movedBytes,
                Error = "已取消。源文件保持完整，目标目录可能留有部分副本。",
            };
        }

        Report("正在校验…", force: true);

        if (failures.Count > 0)
        {
            var failed = new MigrationResult
            {
                Success = false,
                TotalFiles = files.Count,
                MovedFiles = doneFiles,
                MovedBytes = movedBytes,
                Error = $"有 {failures.Count} 个文件没能复制，源目录未删除。",
            };
            foreach (var f in failures) failed.Failures.Add(f);
            return failed;
        }

        // 校验：目标端文件数与字节数必须对得上，对不上就不算成功
        var verify = Scan(target);
        if (verify.FileCount < plan.FileCount || verify.TotalBytes < plan.TotalBytes)
        {
            return new MigrationResult
            {
                Success = false,
                TotalFiles = files.Count,
                MovedFiles = doneFiles,
                MovedBytes = movedBytes,
                Error = $"校验未通过（源 {plan.FileCount} 个 / {Format(plan.TotalBytes)}，" +
                        $"目标 {verify.FileCount} 个 / {Format(verify.TotalBytes)}），源目录未删除。",
            };
        }

        if (purgeSource)
        {
            Report("正在清理原位置…", force: true);
            var cleanErrors = PurgeSource(source);
            return new MigrationResult
            {
                Success = true,
                TotalFiles = files.Count,
                MovedFiles = doneFiles,
                MovedBytes = movedBytes,
                Warning = cleanErrors.Count == 0 ? null : BuildCleanupWarning(cleanErrors),
            };
        }

        return new MigrationResult
        {
            Success = true,
            TotalFiles = files.Count,
            MovedFiles = doneFiles,
            MovedBytes = movedBytes,
        };
    }

    /// <summary>
    /// 删除源根下的盒子目录与残留文件。顶层的 config/error.log 不动
    /// （哪怕已经迁到 ConfigDir，留着也不影响，误删风险更大）。
    /// 返回删不掉的条目。
    /// </summary>
    public static List<string> PurgeSource(string source)
    {
        var errors = new List<string>();

        foreach (var dir in SafeEnumerateDirs(source))
        {
            try { Directory.Delete(dir, true); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(dir)}：{ex.Message}"); }
        }

        foreach (var file in SafeEnumerate(source))
        {
            if (IsConfigArtifact(file, source)) continue;
            try { File.Delete(file); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}：{ex.Message}"); }
        }

        return errors;
    }

    public static string BuildCleanupWarning(List<string> errors) =>
        $"文件已全部迁移，但原位置有 {errors.Count} 个条目删不掉（可能被占用）：{string.Join("；", errors.Take(3))}";

    private static IEnumerable<string> SafeEnumerateDirs(string root)
    {
        // 由深到浅删除，避免父目录非空
        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).ToList();
        }
        catch
        {
            yield break;
        }
        dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (var d in dirs)
        {
            if (Directory.Exists(d)) yield return d;
        }
    }

    private static void CopyFile(string src, string dst, CancellationToken token, Action<int>? onChunk)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            onChunk?.Invoke(read);
        }
    }

    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{(long)v} {units[i]}" : $"{v:0.#} {units[i]}";
    }
}
