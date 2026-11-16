using System.IO;
using System.IO.Compression;

namespace ToniaDeskBox.Services;

public static class BackupService
{
    /// <summary>
    /// 导出盒子内容为 zip。配置与数据根已分离，所以配置需要通过 configPath 单独带进来，
    /// 否则换机器恢复后只剩文件、没有盒子结构。
    /// </summary>
    public static void ExportZip(string dataDir, string zipPath, string? configPath = null)
    {
        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var zipFull = Path.GetFullPath(zipPath);
        foreach (var file in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("error.log", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("config.corrupt-", StringComparison.OrdinalIgnoreCase)) continue;
            // 导出目标若选在数据目录内，惰性枚举会把 zip 自身也读进来
            if (string.Equals(Path.GetFullPath(file), zipFull, StringComparison.OrdinalIgnoreCase)) continue;
            zip.CreateEntryFromFile(file, Path.GetRelativePath(dataDir, file), CompressionLevel.Optimal);
        }

        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            try
            {
                zip.CreateEntryFromFile(configPath, "_config/config.json", CompressionLevel.Optimal);
            }
            catch
            {
                // 配置读不到不该让整个备份失败
            }
        }
    }
}
