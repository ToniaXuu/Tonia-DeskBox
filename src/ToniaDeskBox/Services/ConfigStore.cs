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
        DataDir = dataDir ?? Path.Combine(AppContext.BaseDirectory, "boxes");
        Directory.CreateDirectory(DataDir);
    }

    public AppConfig Load()
    {
        var cfg = TryRead(ConfigPath) ?? TryRead(BakPath);
        if (cfg != null) return cfg;

        var def = new AppConfig();
        def.Boxes.Add(new BoxConfig());
        Save(def);
        return def;
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
