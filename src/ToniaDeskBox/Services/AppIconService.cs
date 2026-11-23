using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ToniaDeskBox.Services;

/// <summary>
/// 应用图标（窗口标题栏 + 托盘）。用哪一版由 <see cref="ThemeService.LightForIcons"/> 决定：
/// 显式选了浅/深色就跟随主题，自定义与「跟随系统」则看系统深浅。
/// 系统变化的监听统一由 ThemeService 负责，避免两处各订阅一次 UserPreferenceChanged。
/// exe 的文件图标是编译期写入的 Win32 资源，无法随主题变化，固定使用浅色版 app.ico。
/// </summary>
public static class AppIconService
{
    private const string LightUri = "pack://application:,,,/Assets/app-light.ico";
    private const string DarkUri = "pack://application:,,,/Assets/app-dark.ico";
    private const string ResourceKey = "AppIcon";

    private static bool _initialized;
    private static bool _isLight = true;

    /// <summary>图标版本变化后触发（已在 UI 线程），托盘据此重载 GDI+ 图标</summary>
    public static event Action? ThemeChanged;

    public static bool IsLightTheme => _isLight;

    public static string CurrentPackUri => _isLight ? LightUri : DarkUri;

    public static void Initialize(bool light)
    {
        if (_initialized) return;
        _initialized = true;
        SetLight(light, force: true);
    }

    public static void Shutdown() => _initialized = false;

    /// <summary>切换图标版本。force 供首次初始化用：即使值相同也要把资源写进 App.Resources。</summary>
    public static void SetLight(bool light, bool force = false)
    {
        if (!force && light == _isLight) return;
        _isLight = light;
        ApplyWindowIconResource();
        ThemeChanged?.Invoke();
    }

    private static void ApplyWindowIconResource()
    {
        var app = Application.Current;
        if (app == null) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(CurrentPackUri, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            app.Resources[ResourceKey] = bmp;
        }
        catch
        {
            // 图标资源缺失不该影响主流程
        }
    }

    /// <summary>
    /// 托盘和图标的原始 ICO 字节。交给 <see cref="TrayIcon"/> 自己转 HICON，
    /// 这样无需为了一个托盘图标引入 System.Drawing。
    /// </summary>
    public static byte[]? LoadTrayIconBytes()
    {
        try
        {
            var sri = Application.GetResourceStream(new Uri(CurrentPackUri, UriKind.Absolute));
            if (sri == null) return null;
            using var ms = new MemoryStream();
            sri.Stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
