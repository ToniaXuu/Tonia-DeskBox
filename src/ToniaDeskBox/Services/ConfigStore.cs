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

    // Windows 保留设备名：即使不含非法字符，也不能当作目录名
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string DevDataDirName = "boxes-dev";
    private const string PortableDataDirName = "boxes";

    /// <summary>
    /// 配置与日志的固定位置。刻意不跟随数据根移动：配置只有几 KB，
    /// 放在稳定位置才能保证「数据根被改到别的盘后，下次启动仍找得到配置」。
    /// </summary>
    public string ConfigDir { get; }

    /// <summary>
    /// 盒子文件的根目录（每个盒子是它的一个子目录），可在设置里更改。
    /// 盒子会收纳真实文件、可能很大，所以允许指向数据盘。
    /// </summary>
    public string DataRoot { get; private set; }

    /// <summary>「恢复默认」时的目标位置。</summary>
    public string DefaultDataRoot { get; }

    /// <summary>本次运行是否因配置的数据根不可用而回退（仅内存状态，不回写配置）。</summary>
    public bool UsedFallbackRoot { get; private set; }

    private string ConfigPath => Path.Combine(ConfigDir, "config.json");
    private string BakPath => Path.Combine(ConfigDir, "config.json.bak");

    // 旧版把 config.json 放在数据目录里；升级时复制到 ConfigDir，并把数据根指回原目录。
    private string? _legacyRoot;

    public ConfigStore(string? configDir = null, string? dataRoot = null)
    {
        if (configDir is null)
        {
            ConfigDir = ResolveConfigDir(out var resolvedRoot);
            DefaultDataRoot = resolvedRoot;
        }
        else
        {
            ConfigDir = configDir;
            DefaultDataRoot = Path.Combine(configDir, DevDataDirName);
        }

        Directory.CreateDirectory(ConfigDir);
        MigrateLegacyConfig();

        DataRoot = dataRoot ?? DefaultDataRoot;
        Directory.CreateDirectory(DataRoot);
    }

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string ResolveConfigDir(out string defaultDataRoot)
    {
        var exeDir = AppContext.BaseDirectory;

        // 开发构建输出目录会被 dotnet clean / 删除 bin / git clean -xdf 清空，
        // 配置与被收纳的文件都不能留在这里。
        if (IsBuildOutputDir(exeDir))
        {
            var dir = Path.Combine(LocalAppData, "Tonia-DeskBox");
            defaultDataRoot = Path.Combine(dir, DevDataDirName);
            return dir;
        }

        // 绿色版：配置与默认数据都在 exe 同级，整体拷贝即可迁移
        if (CanWrite(exeDir))
        {
            defaultDataRoot = Path.Combine(exeDir, PortableDataDirName);
            return exeDir;
        }

        // exe 处在只读位置（如 Program Files）时退回用户目录
        var fallback = Path.Combine(LocalAppData, "Tonia-DeskBox");
        defaultDataRoot = Path.Combine(fallback, PortableDataDirName);
        return fallback;
    }

    private static bool IsBuildOutputDir(string dir)
    {
        var parts = Path.GetFullPath(dir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                (parts[i + 1].Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
                 parts[i + 1].Equals("Release", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 旧版把 config.json 与 error.log 放在数据目录内。拆分后配置固定到 ConfigDir，
    /// 首次升级时把旧配置复制过来（不删除原文件），并把数据根指向原目录，用户无感。
    /// </summary>
    private void MigrateLegacyConfig()
    {
        if (File.Exists(ConfigPath)) return;

        var candidates = new[]
        {
            Path.Combine(ConfigDir, DevDataDirName),
            Path.Combine(ConfigDir, PortableDataDirName),
            Path.Combine(AppContext.BaseDirectory, PortableDataDirName),
        };

        foreach (var root in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var legacy = Path.Combine(root, "config.json");
            if (!File.Exists(legacy)) continue;
            try
            {
                File.Copy(legacy, ConfigPath, overwrite: false);
                var legacyBak = Path.Combine(root, "config.json.bak");
                if (File.Exists(legacyBak)) File.Copy(legacyBak, BakPath, overwrite: true);
                _legacyRoot = Path.GetFullPath(root);
                Log($"已把旧配置从 {root} 迁到 {ConfigDir}；数据根继续指向原目录。");
                return;
            }
            catch (Exception ex)
            {
                Log($"迁移旧配置失败（{root}）：{ex.Message}");
            }
        }
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
        var cfg = TryRead(ConfigPath, out bool mainCorrupt);
        // 主文件损坏时先隔离，避免之后 Save 把它当成"旧配置"复制成 bak
        if (mainCorrupt) Quarantine(ConfigPath);

        bool needSave = false;
        if (cfg == null)
        {
            cfg = TryRead(BakPath, out _);
            if (cfg != null)
            {
                Log("config.json 不可读，已从 config.json.bak 恢复。");
                needSave = true;   // 重建主文件，避免每次启动都走恢复路径
            }
        }

        if (cfg == null)
        {
            Log("config.json 与 config.json.bak 均不可读，已重建为 1 个空盒的默认配置。");
            cfg = new AppConfig();
            cfg.Boxes.Add(new BoxConfig());
            needSave = true;
        }

        // JSON 里可能出现显式 null，反序列化后字段会是 null
        cfg.Settings ??= new AppSettings();
        cfg.Boxes ??= new List<BoxConfig>();
        if (NormalizeBoxes(cfg.Boxes)) needSave = true;

        // 数据根取值优先级：配置里指定的 > 旧配置迁来的原目录 > 默认位置
        var wanted = !string.IsNullOrWhiteSpace(cfg.Settings.DataRoot)
            ? cfg.Settings.DataRoot
            : _legacyRoot;

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            if (TryUseDataRoot(wanted!, out var err))
            {
                // 旧配置没有 DataRoot 字段，补写一次，之后不再依赖迁移逻辑
                if (string.IsNullOrWhiteSpace(cfg.Settings.DataRoot))
                {
                    cfg.Settings.DataRoot = DataRoot;
                    needSave = true;
                }
            }
            else
            {
                // 盘没插 / 目录被删：保住原配置值，仅本次回退，插回来就能继续用
                UsedFallbackRoot = true;
                Log($"数据根 {wanted} 不可用（{err}），本次回退到 {DefaultDataRoot}；配置未改动。");
            }
        }

        // 一次性迁移：早期版本盒子默认置顶，会盖住所有窗口，统一改回桌面组件语义
        if (!cfg.Settings.TopmostMigrated)
        {
            foreach (var b in cfg.Boxes) b.Topmost = false;
            cfg.Settings.TopmostMigrated = true;
            needSave = true;
        }

        if (needSave) Save(cfg);
        return cfg;
    }

    // config.json 可被手工编辑，盒子名/ID 不能无条件当作目录名使用
    private static bool NormalizeBoxes(List<BoxConfig> boxes)
    {
        bool changed = false;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in boxes)
        {
            if (!IsSafeSegment(b.Id)) { b.Id = Guid.NewGuid().ToString("N")[..8]; changed = true; }
            if (!IsSafeSegment(b.Name)) { b.Name = "盒子-" + b.Id; changed = true; }
            if (b.Order == null) { b.Order = new List<string>(); changed = true; }

            // 同名盒子会共用同一数据目录，必须消歧
            var name = b.Name;
            for (int i = 2; !used.Add(name); i++)
                name = $"{b.Name}-{i}";
            if (!string.Equals(name, b.Name, StringComparison.Ordinal))
            {
                b.Name = name;
                changed = true;
            }
        }
        return changed;
    }

    public static bool IsSafeSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name is "." or "..") return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0 || name.IndexOf(':') >= 0) return false;
        if (Path.IsPathRooted(name)) return false;
        if (ReservedNames.Contains(name.Split('.')[0].Trim())) return false;
        return true;
    }

    private AppConfig? TryRead(string path, out bool corrupt)
    {
        corrupt = false;
        try
        {
            if (!File.Exists(path)) return null;
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
            if (cfg == null) corrupt = true;
            return cfg;
        }
        catch (Exception ex)
        {
            corrupt = true;
            Log($"读取配置失败：{path}\n{ex}");
            return null;
        }
    }

    private void Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var dst = Path.Combine(ConfigDir, $"config.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(path, dst, overwrite: false);
            File.Delete(path);
            Log($"已隔离损坏配置：{dst}（bak 保持不动）");
        }
        catch (Exception ex)
        {
            Log($"隔离损坏配置失败：{ex.Message}");
        }
    }

    public void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            // 只有旧主文件本身是合法配置才更新 bak，绝不把损坏内容覆盖到唯一备份上
            if (File.Exists(ConfigPath))
            {
                try
                {
                    if (JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions) != null)
                        File.Copy(ConfigPath, BakPath, overwrite: true);
                }
                catch
                {
                    // 旧文件损坏/读不到：保留 bak 原样
                }
            }
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"保存配置失败：{ex}");
        }
    }

    /// <summary>
    /// 切换数据根。调用方负责先把文件搬过去；这里只做校验、建目录并落地内存值。
    /// path 传 null / 空表示恢复默认位置。
    /// </summary>
    public bool TrySetDataRoot(string? path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            try
            {
                Directory.CreateDirectory(DefaultDataRoot);
                if (!CanWrite(DefaultDataRoot))
                {
                    error = "默认目录不可写";
                    return false;
                }
                DataRoot = DefaultDataRoot;
                UsedFallbackRoot = false;
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        if (!Path.IsPathRooted(path))
        {
            error = "需要完整路径（例如 D:\\ToniaData\\boxes）";
            return false;
        }

        if (!TryUseDataRoot(path, out error)) return false;
        UsedFallbackRoot = false;
        return true;
    }

    private bool TryUseDataRoot(string path, out string error)
    {
        error = "";
        try
        {
            var full = Path.GetFullPath(path.Trim());
            Directory.CreateDirectory(full);
            if (!CanWrite(full))
            {
                error = "目录不可写";
                return false;
            }
            DataRoot = full;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool IsUsingDefaultRoot =>
        string.Equals(
            Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(DefaultDataRoot).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    public string BoxDir(BoxConfig box)
    {
        var segment = IsSafeSegment(box.Name) ? box.Name : box.Id;
        var dir = Path.GetFullPath(Path.Combine(DataRoot, segment));
        var root = Path.GetFullPath(DataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"盒子“{box.Name}”的数据目录越界，已拒绝访问：{dir}");
        return dir;
    }

    public int CountOf(BoxConfig box)
    {
        var dir = BoxDir(box);
        return Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Count() : 0;
    }

    public void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.AppendAllText(Path.Combine(ConfigDir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n\n");
        }
        catch
        {
            // 日志写不进去也不能让主流程中断
        }
    }
}
