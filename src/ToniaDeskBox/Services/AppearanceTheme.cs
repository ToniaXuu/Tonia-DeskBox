using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToniaDeskBox.Models;

namespace ToniaDeskBox.Services;

/// <summary>
/// 全应用外观资源的键名。所有窗口、控件样式、右键菜单只认这一套键，
/// 由 AppearanceTheme 统一赋值，避免各处硬编码白色前景造成「白底白字」。
/// </summary>
public static class ThemeKeys
{
    public const string Surface = "Theme.Surface";
    public const string SurfaceHover = "Theme.SurfaceHover";
    public const string SurfaceDrop = "Theme.SurfaceDrop";
    public const string SurfaceSolid = "Theme.SurfaceSolid";
    public const string ShadowFill = "Theme.ShadowFill";
    public const string Border = "Theme.Border";
    public const string BorderHover = "Theme.BorderHover";
    public const string BorderDrop = "Theme.BorderDrop";
    public const string Text = "Theme.Text";
    public const string TextSecondary = "Theme.TextSecondary";
    public const string TextMuted = "Theme.TextMuted";
    public const string TextDisabled = "Theme.TextDisabled";
    public const string Icon = "Theme.Icon";
    public const string Fill = "Theme.Fill";
    public const string FillHover = "Theme.FillHover";
    public const string FillStrong = "Theme.FillStrong";
    public const string Divider = "Theme.Divider";
    public const string TitleBar = "Theme.TitleBar";
    public const string Accent = "Theme.Accent";
    public const string Action = "Theme.Action";
    public const string AccentHover = "Theme.AccentHover";
    public const string AccentPressed = "Theme.AccentPressed";
    public const string AccentBadge = "Theme.AccentBadge";
    public const string OnAccent = "Theme.OnAccent";
    public const string Danger = "Theme.Danger";
    public const string Ring = "Theme.Ring";
    public const string ScrollThumb = "Theme.ScrollThumb";
    public const string DashStroke = "Theme.DashStroke";
}

/// <summary>
/// 外观样式的单一真源：由底色 + 透明度 + 分类色推导出一整套笔刷。
///
/// - 底色是「半透明纯色」，不做任何 DWM 亚克力/模糊；透明度只作用于底色，
///   窗口级 Opacity 恒为 1，文字与图标保持满不透明度。
/// - 按底色相对亮度自动切换浅色/深色墨色：浅色底用深色前景，深色底用白色前景。
/// - Update() 直接替换窗口根资源字典里的 Theme.* 项，DynamicResource 会自动刷新，
///   不需要重建窗口，也不需要原地改 Freezable（合并字典会把笔刷冻结）。
/// </summary>
public sealed class AppearanceTheme
{
    public const string DefaultBackColor = "#1F2A30";
    public const double DefaultOpacity = 0.85;
    private const byte MinSurfaceAlpha = 28;

    private static readonly string[] SolidKeys =
    {
        ThemeKeys.SurfaceSolid, ThemeKeys.ShadowFill,
        ThemeKeys.Text, ThemeKeys.TextSecondary, ThemeKeys.TextMuted, ThemeKeys.TextDisabled,
        ThemeKeys.Icon, ThemeKeys.Fill, ThemeKeys.FillHover, ThemeKeys.FillStrong,
        ThemeKeys.Divider, ThemeKeys.TitleBar,
        ThemeKeys.Accent, ThemeKeys.Action, ThemeKeys.AccentHover, ThemeKeys.AccentPressed,
        ThemeKeys.AccentBadge, ThemeKeys.OnAccent, ThemeKeys.Danger, ThemeKeys.Ring,
        ThemeKeys.ScrollThumb, ThemeKeys.DashStroke,
    };

    private static readonly string[] GradientKeys =
    {
        ThemeKeys.Surface, ThemeKeys.SurfaceHover, ThemeKeys.SurfaceDrop,
        ThemeKeys.Border, ThemeKeys.BorderHover, ThemeKeys.BorderDrop,
    };

    // 与旧版 BoxWindow / PanelWindow 完全相同的分类调色板，保证改名换色行为不变
    private static readonly Color[] AccentPalette =
    {
        Color.FromRgb(0x8F, 0xA7, 0xFF),
        Color.FromRgb(0xF2, 0xA6, 0x5A),
        Color.FromRgb(0x7F, 0xD1, 0xAE),
        Color.FromRgb(0xC8, 0x9B, 0xFF),
        Color.FromRgb(0xF2, 0x8B, 0x9C),
        Color.FromRgb(0x6F, 0xC4, 0xE8),
    };

    // 注意：必须用目标自己的「根」资源字典。MergedDictionaries 里的字典会被 WPF Seal，
    // 连带把里面所有 Freezable 笔刷冻结，之后就再也改不了颜色了。
    private readonly ResourceDictionary _target;

    public Color AccentColor { get; private set; }
    public bool IsLight { get; private set; }
    public byte SurfaceAlpha { get; private set; }

    private AppearanceTheme(ResourceDictionary target)
    {
        _target = target;
        foreach (var key in SolidKeys)
            _target[key] = new SolidColorBrush(Colors.Transparent);
        foreach (var key in GradientKeys)
            _target[key] = MakeGradient(Colors.Transparent, Colors.Transparent);
    }

    public Brush BrushOf(string key) => (Brush)_target[key];

    /// <summary>挂到某个窗口 / 菜单的根资源字典上，覆盖应用级默认主题。</summary>
    public static AppearanceTheme Attach(FrameworkElement target) => new(target.Resources);

    /// <summary>应用级默认主题：右键菜单、工具提示等脱离窗口树的 Popup 至少能解析到一套笔刷。</summary>
    public static AppearanceTheme AttachApplication()
    {
        var theme = new AppearanceTheme(Application.Current.Resources);
        theme.Update(ThemeService.ResolveColor(null), DefaultOpacity, "新建盒子");
        return theme;
    }

    /// <summary>
    /// 右键菜单跟随所属盒子的外观，而不是应用默认深色。
    /// 菜单的不透明度独立于盒子（<c>AppSettings.MenuOpacity</c>）：文字密集的浮层要更实。
    /// </summary>
    public static AppearanceTheme AttachMenu(ContextMenu menu, BoxConfig box,
        double minAlpha = 0, Window? owner = null)
    {
        var theme = Attach(menu);
        theme.Update(box, minAlpha);
        WatchMenuOverlap(menu, box, minAlpha, owner);
        return theme;
    }

    /// <summary>
    /// 菜单压住别的卡片时抬成全不透明。
    /// 两层半透明叠在一起，底下的字会从菜单里透出来，两边都读不清。
    /// 只排除菜单所属的盒子：菜单本来就该盖在它上面。
    /// </summary>
    private static void WatchMenuOverlap(ContextMenu menu, BoxConfig box, double minAlpha, Window? owner)
    {
        void Check(int attempt)
        {
            // Opened 时还没排版；个别情况下弹层的位置/尺寸要等一两轮才定稿，
            // 量到 0 就再等一轮，避免"第一次打开菜单没变实"。
            menu.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (menu.ActualWidth <= 0 && attempt < 3)
                {
                    Check(attempt + 1);
                    return;
                }
                if (!OverlapsOtherCard(menu, owner)) return;
                Attach(menu).Update(box, 1.0);
            }));
        }

        menu.Opened += (_, _) => Check(0);
    }

    /// <summary>菜单（或任意元素）是否与别的卡片/面板在屏幕上有实质重叠。</summary>
    public static bool OverlapsOtherCard(FrameworkElement element, Window? exclude = null)
    {
        try
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;

            double dpi = VisualTreeHelper.GetDpi(element).DpiScaleX;
            if (dpi <= 0) dpi = 1;

            // 菜单在 Popup 自己的 HWND 里：PointToScreen 拿到的是那个 HWND 的客户区坐标，
            // 不含弹层在屏幕上的偏移，直接问窗口矩形才准。
            Rect self;
            if (PresentationSource.FromVisual(element) is System.Windows.Interop.HwndSource src
                && src.Handle != IntPtr.Zero
                && ScreenUtil.WindowRectPx(src.Handle) is { } px)
            {
                self = new Rect(px.X / dpi, px.Y / dpi, px.Width / dpi, px.Height / dpi);
            }
            else
            {
                var origin = element.PointToScreen(new Point(0, 0));
                self = new Rect(origin.X / dpi, origin.Y / dpi,
                    element.ActualWidth, element.ActualHeight);
            }

            foreach (Window win in Application.Current.Windows)
            {
                if (!win.IsVisible || ReferenceEquals(win, exclude)) continue;
                if (win.WindowState == WindowState.Minimized) continue;

                var other = new Rect(win.Left, win.Top, win.ActualWidth, win.ActualHeight);
                var hit = Rect.Intersect(self, other);
                // 只擦到几个像素（圆角、阴影贴边）不算重叠
                if (!hit.IsEmpty && hit.Width > 6 && hit.Height > 6) return true;
            }
        }
        catch
        {
            // 跨 DPI / 窗口正在销毁时 PointToScreen 可能抛，宁可保持原样
        }
        return false;
    }

    public AppearanceTheme Update(BoxConfig box, double minAlpha = 0) =>
        // 主题模式接管底色时忽略盒子自身的 BackColor，但**不修改数据**：
        // 切回「自定义」后每个盒子原来的颜色会自动回来
        Update(ThemeService.ResolveColor(box.BackColor), box.Opacity, box.Name, minAlpha);

    public AppearanceTheme Update(string backHex, double opacity, string name, double minAlpha = 0)
    {
        var color = ParseColor(backHex);
        IsLight = RelativeLuminance(color) >= 0.62;
        AccentColor = AccentOf(name);

        double op = double.IsNaN(opacity) ? DefaultOpacity : Math.Clamp(opacity, 0, 1);
        SurfaceAlpha = (byte)Math.Clamp(
            (int)Math.Round(Math.Max(op, Math.Clamp(minAlpha, 0, 1)) * 255), MinSurfaceAlpha, 255);
        byte a = SurfaceAlpha;

        // 半透明底色：上下微渐变只为层次，不做模糊；hover / drop 只改明度。
        // 幅度刻意压小：主题色取的就是任务栏表面色，渐变过大会让盒子比任务栏"浮"出来。
        int topDelta = IsLight ? 6 : 10;
        int bottomDelta = IsLight ? -6 : -6;
        SetGradient(ThemeKeys.Surface,
            WithAlpha(Lift(color, topDelta), a), WithAlpha(Lift(color, bottomDelta), a));
        SetGradient(ThemeKeys.SurfaceHover,
            WithAlpha(Lift(color, IsLight ? topDelta - 14 : topDelta + 12), a),
            WithAlpha(Lift(color, IsLight ? bottomDelta - 10 : bottomDelta + 8), a));
        SetGradient(ThemeKeys.SurfaceDrop,
            WithAlpha(Lift(color, IsLight ? topDelta - 20 : topDelta + 18), a),
            WithAlpha(Lift(color, IsLight ? bottomDelta - 16 : bottomDelta + 14), a));
        SetSolid(ThemeKeys.SurfaceSolid, WithAlpha(color, a));
        // 投影层是半透明的，否则会变成不透明背板把「透明底色」堵死；
        // 它的 alpha 只用来给 DropShadowEffect 提供轮廓
        SetSolid(ThemeKeys.ShadowFill, WithAlpha(color, (byte)Math.Max(MinSurfaceAlpha, a * 0.55)));

        var ink = IsLight ? Color.FromRgb(0x18, 0x20, 0x2C) : Colors.White;
        Color Ink(byte alpha) => Color.FromArgb(alpha, ink.R, ink.G, ink.B);
        Color Wht(byte alpha) => Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);

        SetSolid(ThemeKeys.Text, IsLight ? Ink(0xFF) : Wht(0xF7));
        SetSolid(ThemeKeys.TextSecondary, IsLight ? Ink(0xD6) : Wht(0xC7));
        SetSolid(ThemeKeys.TextMuted, IsLight ? Ink(0x96) : Wht(0x8C));
        SetSolid(ThemeKeys.TextDisabled, IsLight ? Ink(0x60) : Wht(0x59));
        SetSolid(ThemeKeys.Icon, IsLight ? Ink(0xCC) : Wht(0xCC));
        SetSolid(ThemeKeys.Fill, IsLight ? Ink(0x14) : Wht(0x14));
        SetSolid(ThemeKeys.FillHover, IsLight ? Ink(0x24) : Wht(0x28));
        SetSolid(ThemeKeys.FillStrong, IsLight ? Ink(0x36) : Wht(0x38));
        SetSolid(ThemeKeys.Divider, IsLight ? Ink(0x20) : Wht(0x22));
        SetSolid(ThemeKeys.TitleBar, IsLight ? Ink(0x0D) : Wht(0x14));
        SetSolid(ThemeKeys.ScrollThumb, IsLight ? Ink(0x59) : Wht(0x42));
        SetSolid(ThemeKeys.DashStroke, IsLight ? Ink(0x80) : Wht(0x59));
        SetSolid(ThemeKeys.Ring, IsLight ? Ink(0xFF) : Wht(0xFF));
        SetSolid(ThemeKeys.Danger, Color.FromRgb(0xE8, 0x11, 0x23));
        SetSolid(ThemeKeys.OnAccent, Wht(0xFF));

        SetSolid(ThemeKeys.Accent, AccentColor);
        // 按钮/勾选框这类要放白字的元素用压暗一档的分类色，避免浅色分类色上的白字读不清
        var action = Darken(AccentColor, 0.68);
        SetSolid(ThemeKeys.Action, action);
        SetSolid(ThemeKeys.AccentHover, Lift(action, 16));
        SetSolid(ThemeKeys.AccentPressed, Lift(action, -16));
        // 浅色底上胶囊要压暗分类色，白字才读得清
        SetSolid(ThemeKeys.AccentBadge, IsLight
            ? Color.FromArgb(0xF0,
                (byte)(AccentColor.R * 0.62), (byte)(AccentColor.G * 0.62), (byte)(AccentColor.B * 0.62))
            : WithAlpha(AccentColor, 0x99));

        SetGradient(ThemeKeys.Border,
            IsLight ? Ink(0x40) : Wht(0x59), IsLight ? Ink(0x14) : Wht(0x14));
        SetGradient(ThemeKeys.BorderHover,
            IsLight ? Ink(0x99) : Wht(0xB3), IsLight ? Ink(0x40) : Wht(0x40));
        SetGradient(ThemeKeys.BorderDrop,
            WithAlpha(AccentColor, 0xE6), WithAlpha(AccentColor, 0x59));

        return this;
    }

    // 每次换成新笔刷对象：根字典可写，DynamicResource 会随字典项替换自动刷新
    private void SetSolid(string key, Color color) =>
        _target[key] = new SolidColorBrush(color);

    private void SetGradient(string key, Color top, Color bottom) =>
        _target[key] = MakeGradient(top, bottom);

    private static LinearGradientBrush MakeGradient(Color top, Color bottom)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        b.GradientStops.Add(new GradientStop(top, 0));
        b.GradientStops.Add(new GradientStop(bottom, 1));
        return b;
    }

    // sRGB 相对亮度：浅色色板（#FFFFFF / #F2F2F2 / #DCE3E8）落在 0.75 以上，
    // 深色色板都在 0.1 以下，阈值 0.62 留足余量
    public static double RelativeLuminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static Color AccentOf(string name)
    {
        int h = 17;
        foreach (char c in name) h = unchecked(h * 31 + c);
        return AccentPalette[(h & 0x7FFFFFFF) % AccentPalette.Length];
    }

    public static Color ParseColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch { }
        return Color.FromRgb(0x1F, 0x2A, 0x30);
    }

    private static Color Darken(Color c, double factor) => Color.FromRgb(
        (byte)Math.Clamp((int)(c.R * factor), 0, 255),
        (byte)Math.Clamp((int)(c.G * factor), 0, 255),
        (byte)Math.Clamp((int)(c.B * factor), 0, 255));

    private static Color Lift(Color c, int delta) => Color.FromArgb(
        c.A,
        (byte)Math.Clamp(c.R + delta, 0, 255),
        (byte)Math.Clamp(c.G + delta, 0, 255),
        (byte)Math.Clamp(c.B + delta, 0, 255));

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
