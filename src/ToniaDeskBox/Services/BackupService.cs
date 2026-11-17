using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ToniaDeskBox.Models;

namespace ToniaDeskBox.Services;

/// <summary>导出/导入进度快照。每次回调都是完整状态，UI 不需要累加。</summary>
public sealed class TransferProgress
{
    /// <summary>统计阶段还没有文件总数，用于区分「正在统计」与「正在读写」。</summary>
    public bool Counting { get; init; }
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public string Current { get; init; } = "";
}

public sealed class TransferResult
{
    public int FilesWritten { get; init; }
    /// <summary>被占用 / 无权限而跳过的文件数，不影响其余文件。</summary>
    public int FilesSkipped { get; init; }
    public long BytesTotal { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>备份包体检结果：导入前先让用户看清里面是什么，再动手覆盖。</summary>
public sealed class BackupManifest
{
    public string ZipPath { get; init; } = "";
    public int FileCount { get; init; }
    public long BytesTotal { get; init; }
    /// <summary>包内是否带可用的盒子配置。没有就只是"一堆文件"，无法还原结构。</summary>
    public bool HasConfig { get; init; }
    public DateTime? CreatedAt { get; init; }
    /// <summary>备份包自带的配置。里面的 DataRoot 属于导出那台机器，导入时不能直接用。</summary>
    public AppConfig? Config { get; init; }
    public string? BackupDataRoot { get; init; }
    public IReadOnlyList<(string Name, int Items)> Boxes { get; init; } = Array.Empty<(string, int)>();
}

/// <summary>
/// 盒子数据的导出 / 导入。
///
/// 关键约束：盒子收纳的是真实文件（本机实测 890 MB / 703 个文件），压缩与解压都必须跑在后台线程
/// —— 早先在 UI 线程同步压缩，26 秒不出消息循环，直接被 Windows 的挂起检测判成 AppHangB1 关掉
/// （事件日志 Application Hang / ID 1002）。因此这里只提供 IProgress + CancellationToken，
/// 调用方负责丢到 Task.Run。
/// </summary>
public static class BackupService
{
    private const int ChunkSize = 128 * 1024;
    /// <summary>进度回调节流：890 MB 按块回调会是几千次，UI 没必要每次都刷。</summary>
    private const int ProgressIntervalMs = 120;
    /// <summary>配置在包内的固定路径；导入靠它还原盒子结构，没有它就只是"一堆文件"。</summary>
    public const string ConfigEntryName = "_config/config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly record struct Entry(string FullPath, string EntryName, long Size);

    /// <summary>同步阻塞版本，仅供后台线程 / 测试使用。</summary>
    public static TransferResult ExportZip(string dataDir, string zipPath, string? configPath = null) =>
        ExportZip(dataDir, zipPath, configPath, null, CancellationToken.None);

    public static TransferResult ExportZip(
        string dataDir, string zipPath, string? configPath,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(dataDir))
            throw new DirectoryNotFoundException($"数据目录不存在：{dataDir}");

        var started = Stopwatch.StartNew();
        var zipFull = Path.GetFullPath(zipPath);

        // ---- 1. 先统计：一边走目录一边把已知量报给 UI，避免统计阶段看起来像卡住 ----
        var entries = new List<Entry>();
        long totalBytes = 0;
        var countingThrottle = Stopwatch.StartNew();

        void ReportCounting()
        {
            if (progress == null) return;
            if (countingThrottle.ElapsedMilliseconds < ProgressIntervalMs) return;
            countingThrottle.Restart();
            progress.Report(new TransferProgress
            {
                Counting = true,
                FilesTotal = entries.Count,
                BytesTotal = totalBytes,
                Current = "正在统计文件…",
            });
        }

        Walk(dataDir, ct, file =>
        {
            var name = Path.GetFileName(file);
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
            if (name.Equals("error.log", StringComparison.OrdinalIgnoreCase)) return;
            if (name.StartsWith("config.corrupt-", StringComparison.OrdinalIgnoreCase)) return;
            // 导出目标若选在数据目录内，惰性枚举会把 zip 自身也读进来
            if (string.Equals(Path.GetFullPath(file), zipFull, StringComparison.OrdinalIgnoreCase)) return;

            long size = 0;
            try { size = new FileInfo(file).Length; } catch { }
            // zip 规范用正斜杠：第三方解压工具才不会把整包摊成一堆带反斜杠的文件名
            entries.Add(new Entry(file, ToEntryName(Path.GetRelativePath(dataDir, file)), size));
            totalBytes += size;
            ReportCounting();
        });

        // 配置与数据根已分离，必须单独带进来，否则换机器恢复后只剩文件、没有盒子结构
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            long csize = 0;
            try { csize = new FileInfo(configPath).Length; } catch { }
            entries.Add(new Entry(configPath, ConfigEntryName, csize));
            totalBytes += csize;
        }

        progress?.Report(new TransferProgress
        {
            Counting = false,
            FilesTotal = entries.Count,
            BytesTotal = totalBytes,
            Current = "",
        });

        // ---- 2. 压缩 ----
        int written = 0, skipped = 0;
        long done = 0;
        var throttle = Stopwatch.StartNew();
        string currentName = "";

        void Report(bool force)
        {
            if (progress == null) return;
            if (!force && throttle.ElapsedMilliseconds < ProgressIntervalMs) return;
            throttle.Restart();
            progress.Report(new TransferProgress
            {
                Counting = false,
                FilesDone = written,
                FilesTotal = entries.Count,
                BytesDone = done,
                BytesTotal = totalBytes,
                Current = currentName,
            });
        }

        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                currentName = e.EntryName;

                // 被别的程序占用（比如盒子里放的是正在运行的便携程序）不该让整份备份失败：
                // FileShare.ReadWrite 能让绝大多数"只读占用"通过，仍失败就记一笔跳过。
                try
                {
                    using var src = new FileStream(
                        e.FullPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, ChunkSize);

                    var entry = zip.CreateEntry(e.EntryName, CompressionLevel.Optimal);
                    TrySetTimestamp(entry, e.FullPath);

                    using var dst = entry.Open();
                    var buffer = new byte[ChunkSize];
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, read);
                        done += read;
                        Report(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    // 跳过时把该文件体积记成已完成，进度条才不会停在半路
                    done += e.Size;
                }

                written++;
                Report(true);
            }
        }

        return new TransferResult
        {
            FilesWritten = written,
            FilesSkipped = skipped,
            BytesTotal = totalBytes,
            Elapsed = started.Elapsed,
        };
    }

    // ---------- 导入侧 ----------

    /// <summary>
    /// 只读体检，不写盘：给导入前的确认窗提供"包里有什么、要不要覆盖"的依据。
    /// </summary>
    public static BackupManifest Inspect(string zipPath)
    {
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        int files = 0;
        long bytes = 0;
        DateTime? newest = null;
        ZipArchiveEntry? configEntry = null;
        var topDirs = new SortedSet<string>(StringComparer.CurrentCulture);

        foreach (var entry in zip.Entries)
        {
            if (IsDirectoryEntry(entry)) continue;
            files++;
            bytes += entry.Length;
            if (newest == null || entry.LastWriteTime.LocalDateTime > newest)
                newest = entry.LastWriteTime.LocalDateTime;

            var name = NormalizeEntryName(entry.FullName);
            if (string.Equals(name, ConfigEntryName, StringComparison.OrdinalIgnoreCase))
                configEntry = entry;
            else if (name.Contains('/'))
                topDirs.Add(name[..name.IndexOf('/')]);
            else
                topDirs.Add("(根目录文件)");
        }

        if (files == 0)
            throw new InvalidDataException("这个压缩包里没有任何文件。");

        AppConfig? config = null;
        if (configEntry != null)
        {
            try
            {
                using var stream = configEntry.Open();
                using var reader = new StreamReader(stream);
                config = JsonSerializer.Deserialize<AppConfig>(reader.ReadToEnd(), JsonOptions);
            }
            catch (JsonException)
            {
                // 包里有配置但读不懂：当作无配置处理，导入会退化成"只有文件"
                config = null;
            }
        }

        var boxes = config?.Boxes is { Count: > 0 } list
            ? list.Select(b => (b.Name, b.Order?.Count ?? 0)).ToList()
            : topDirs.Where(d => d != "(根目录文件)").Select(d => (d, 0)).ToList();

        return new BackupManifest
        {
            ZipPath = zipPath,
            FileCount = files,
            BytesTotal = bytes,
            HasConfig = config?.Boxes is { Count: > 0 },
            CreatedAt = newest,
            Config = config,
            BackupDataRoot = config?.Settings?.DataRoot,
            Boxes = boxes,
        };
    }

    /// <summary>
    /// 把包里的盒子文件解到临时目录（不含 _config/，配置由调用方单独处理）。
    /// 先解压再替换，而不是直接解到数据根上：解压中途失败时现有数据完好无损。
    /// </summary>
    public static TransferResult ImportZip(
        string zipPath, string stagingDir,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        Directory.CreateDirectory(stagingDir);
        var stagingRoot = Path.GetFullPath(stagingDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var fileEntries = zip.Entries.Where(e => !IsDirectoryEntry(e)).ToList();
        long totalBytes = fileEntries.Sum(e => e.Length);
        long done = 0;
        int written = 0, skipped = 0;
        var throttle = Stopwatch.StartNew();
        var buffer = new byte[ChunkSize];

        progress?.Report(new TransferProgress
        {
            FilesTotal = fileEntries.Count,
            BytesTotal = totalBytes,
            Current = "",
        });

        foreach (var entry in fileEntries)
        {
            ct.ThrowIfCancellationRequested();
            var rel = NormalizeEntryName(entry.FullName);

            // 配置不落地：DataRoot 是导出机器的路径，直接用会让盒子指向不存在的目录
            if (string.Equals(rel, ConfigEntryName, StringComparison.OrdinalIgnoreCase)) continue;

            var dest = Path.GetFullPath(Path.Combine(stagingDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            // 防目录穿越：包里写成 ../../ 的条目一律丢弃
            if (!dest.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var src = entry.Open())
                using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize))
                {
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, read);
                        done += read;
                        if (throttle.ElapsedMilliseconds >= ProgressIntervalMs)
                        {
                            throttle.Restart();
                            progress?.Report(new TransferProgress
                            {
                                FilesDone = written,
                                FilesTotal = fileEntries.Count,
                                BytesDone = done,
                                BytesTotal = totalBytes,
                                Current = rel,
                            });
                        }
                    }
                }

                try
                {
                    var t = entry.LastWriteTime.LocalDateTime;
                    if (t.Year >= 1980) File.SetLastWriteTime(dest, t);
                }
                catch
                {
                    // 时间戳恢复失败不影响内容
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }

            written++;
            progress?.Report(new TransferProgress
            {
                FilesDone = written,
                FilesTotal = fileEntries.Count,
                BytesDone = done,
                BytesTotal = totalBytes,
                Current = rel,
            });
        }

        return new TransferResult
        {
            FilesWritten = written,
            FilesSkipped = skipped,
            BytesTotal = totalBytes,
            Elapsed = started.Elapsed,
        };
    }

    /// <summary>包内路径统一用正斜杠；老备份包（反斜杠）也照此归一化后再比较。</summary>
    private static string NormalizeEntryName(string name) => name.Replace('\\', '/').TrimStart('/');

    private static string ToEntryName(string relPath) => relPath.Replace('\\', '/');

    private static bool IsDirectoryEntry(ZipArchiveEntry e) =>
        e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');

    private static void TrySetTimestamp(ZipArchiveEntry entry, string file)
    {
        try
        {
            var t = File.GetLastWriteTime(file);
            // ZIP 的 DOS 时间戳不支持 1980 年之前
            if (t.Year < 1980) t = new DateTime(1980, 1, 1);
            entry.LastWriteTime = t;
        }
        catch
        {
            // 时间戳只是元信息，拿不到就算了
        }
    }

    /// <summary>
    /// 手工递归而不是 Directory.EnumerateFiles(AllDirectories)：后者碰到一个无权限目录就整体抛异常，
    /// 整份备份直接失败。这里逐目录兜底，并跳过符号链接/联接点防止绕圈。
    /// </summary>
    private static void Walk(string root, CancellationToken ct, Action<string> onFile)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = Array.Empty<string>(); }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                onFile(f);
            }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subs)
            {
                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }
                stack.Push(sub);
            }
        }
    }
}
