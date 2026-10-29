using System.IO;
using System.IO.Compression;

namespace ToniaDeskBox.Services;

public static class BackupService
{
    public static void ExportZip(string dataDir, string zipPath)
    {
        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            zip.CreateEntryFromFile(file, Path.GetRelativePath(dataDir, file), CompressionLevel.Optimal);
        }
    }
}
