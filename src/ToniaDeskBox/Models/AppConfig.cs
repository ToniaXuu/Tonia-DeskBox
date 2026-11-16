using System.Text.Json.Serialization;

namespace ToniaDeskBox.Models;

/// <summary>
/// 底色由谁决定。非 Custom 时底色被主题接管，色板不可选（盒子自身的 BackColor 保留不动，
/// 切回 Custom 即恢复原色）。
/// </summary>
public enum ThemeMode
{
    Custom = 0,
    Light = 1,
    Dark = 2,
    System = 3,
}

public class AppConfig
{
    public AppSettings Settings { get; set; } = new();
    public List<BoxConfig> Boxes { get; set; } = new();
}

public class AppSettings
{
    public bool Autorun { get; set; }
    public string Hotkey { get; set; } = "";
    public bool TopmostMigrated { get; set; }

    // 盒子文件（含被拖入的真实文件）的根目录。null / 空 = 用默认位置。
    // 盒子可能收纳几百 MB 甚至几 GB 的文件，所以允许挪到数据盘，避免撑爆系统盘。
    public string? DataRoot { get; set; }

    // 写成字符串而不是数字，手工编辑配置时能一眼看懂
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Custom;

    public GlobalBoxSettings? Global { get; set; }
}

public class GlobalBoxSettings
{
    public double Opacity { get; set; } = 0.85;
    public string BackColor { get; set; } = "#1F2A30";
    public bool Rounded { get; set; } = true;
    public double CornerSize { get; set; } = 8;
    public bool Topmost { get; set; }
}

public class BoxConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "新建盒子";
    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Opacity { get; set; } = 0.85;
    public string BackColor { get; set; } = "#1F2A30";
    public bool Rounded { get; set; } = true;
    public double CornerSize { get; set; } = 8;
    public bool Topmost { get; set; }
    public bool Hidden { get; set; }

    // 固定尺寸（DIP）；0 = 自适应。W >= 240 时盒子内嵌显示图标网格
    public double W { get; set; }
    public double H { get; set; }

    public List<string> Order { get; set; } = new();
}
