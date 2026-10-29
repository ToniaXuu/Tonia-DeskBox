using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ToniaDeskBox.Models;

namespace ToniaDeskBox.Services;

public class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string DataDir { get; }

    private string ConfigPath => Path.Combine(DataDir, "config.json");
    private string BakPath => Path.Combine(DataDir, "config.json.bak");

    public ConfigStore(string? dataDir = null)
    {
        DataDir = dataDir ?? ResolveDataDir();
        Directory.CreateDirectory(DataDir);
    }

    private static string ResolveDataDir()
    {
        var exeDir = AppContext.BaseDirectory;
        if (CanWrite(exeDir))
            return Path.Combine(exeDir, "boxes");

        // 放在只读位置（如 Program Files）时退回用户目录，避免数据写不进去
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tonia-DeskBox", "boxes");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".tonia-write-test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public AppConfig Load()
    {
        var cfg = TryRead(ConfigPath) ?? TryRead(BakPath);
        if (cfg == null)
        {
            cfg = new AppConfig();
            cfg.Boxes.Add(new BoxConfig());
            Save(cfg);
        }

        // 一次性迁移：早期版本盒子默认置顶，会盖住所有窗口，统一改回桌面组件语义
        if (!cfg.Settings.TopmostMigrated)
        {
            foreach (var b in cfg.Boxes) b.Topmost = false;
            cfg.Settings.TopmostMigrated = true;
            Save(cfg);
        }

        return cfg;
    }

    private static AppConfig? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(AppConfig cfg)
    {
        try
        {
            if (File.Exists(ConfigPath)) File.Copy(ConfigPath, BakPath, overwrite: true);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch
        {
            // 保存失败不中断运行，下次变更时重试
        }
    }

    public string BoxDir(BoxConfig box) => Path.Combine(DataDir, box.Name);

    public int CountOf(BoxConfig box)
    {
        var dir = BoxDir(box);
        return Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Count() : 0;
    }
}
