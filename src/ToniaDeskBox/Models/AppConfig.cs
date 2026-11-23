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
    /// <summary>呼出全部盒子的全局热键，如 "Ctrl+Alt+D"。空 = 用默认值，"-" = 关闭。</summary>
    public string Hotkey { get; set; } = "";
    /// <summary>收起全部盒子的全局热键。空 = 不占用键位。</summary>
    public string HideHotkey { get; set; } = "";
    public bool TopmostMigrated { get; set; }

    /// <summary>展示方式一次性迁移：老配置没有 DisplayMode，按宽度推断一次。</summary>
    public bool DisplayModeMigrated { get; set; }

    /// <summary>
    /// 右键菜单 / 托盘菜单的不透明度。菜单是文字密集的浮层，默认要比起盒子更实，
    /// 否则叠在卡片或壁纸上就很难读。0.96 在保持一点通透感的同时不牺牲可读性。
    /// </summary>
    public double MenuOpacity { get; set; } = 0.96;

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

/// <summary>
/// 盒子的收纳语义。
/// - Files  ：文件真的被移进 <c>DataRoot\盒子名</c>（原来的唯一行为）；
/// - Portal ：盒子是一扇「窗」，只镜像 <see cref="BoxConfig.PortalPath"/> 指向的目录，
///            里面的一切都在原地不动 —— 拖进来的项目目录、被 IDE 引用的源码不会被搬走。
/// </summary>
public enum BoxKind
{
    Files = 0,
    Portal = 1,
}

/// <summary>
/// 盒子的展示方式。三种都作用于「普通盒」与「映射盒」，行为完全一致。
/// - Preview   ：隐藏内容，标题 + 最多 3 个文件缩略图 + 总数（最初的样式）；
/// - TitleOnly ：隐藏内容，只显示标题（最省地方）；
/// - Content   ：不隐藏内容，盒子内嵌图标网格，点空白处可打开面板。
///
/// 前两种是「收纳盒」语义：盒子只是个入口，点它不会弹面板，搜索与更多菜单
/// 直接挂在盒子上；第三种是「展开面板」语义，保持原有行为。
/// </summary>
public enum BoxDisplayMode
{
    Preview = 0,
    TitleOnly = 1,
    Content = 2,
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

    /// <summary>展示方式。写字符串，手工编辑配置时一眼能看懂。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BoxDisplayMode DisplayMode { get; set; } = BoxDisplayMode.Preview;

    // 固定尺寸（DIP）；0 = 自适应。W >= 240 时盒子内嵌显示图标网格
    public double W { get; set; }
    public double H { get; set; }

    public List<string> Order { get; set; } = new();

    // 写成字符串而不是数字：手工编辑配置时一眼能看出这是映射盒
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BoxKind Kind { get; set; } = BoxKind.Files;

    /// <summary>映射目标目录（绝对路径）。仅 <see cref="BoxKind.Portal"/> 生效。</summary>
    public string? PortalPath { get; set; }
}
