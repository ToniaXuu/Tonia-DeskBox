using System.Windows;
using Microsoft.Win32;
using ToniaDeskBox.Models;

namespace ToniaDeskBox.Services;

/// <summary>
/// 深浅色主题的唯一决策点。
///
/// 设计要点：主题只接管「底色」，不改写 <c>BoxConfig.BackColor</c>。
/// 渲染时通过 <see cref="ResolveColor"/> 把自定义色解析成实际生效的颜色，
/// 所以从「浅色」切回「自定义」时，每个盒子原来的配色会原样回来，不会丢。
///
/// 四种模式：
/// - Custom  ：用各盒子自己的 BackColor（原来的行为）
/// - Light   ：固定浅色底
/// - Dark    ：固定深色底
/// - System  ：跟随 Windows 的 AppsUseLightTheme，系统切换时实时响应
/// </summary>
public static class ThemeService
{
    // 取 Win11 任务栏的实际表面色：浅色 #F3F3F3、深色 #202020。
    // 这样盒子/面板和系统任务栏是同一档灰，贴在桌面上不会有「另一种白/黑」的割裂感。
    // 注意不要用 #FFFFFF / #000000 —— 那是纯白纯黑，比任务栏更刺眼。
    public const string LightBackColor = "#F3F3F3";
    public const string DarkBackColor = "#202020";

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static bool _initialized;
    private static bool _systemLight = true;

    public static ThemeMode Mode { get; private set; } = ThemeMode.Custom;

    /// <summary>主题发生变化（手动切换模式，或跟随系统时系统改了深浅）。回调已切回 UI 线程。</summary>
    public static event Action? Changed;

    /// <summary>底色是否被主题接管 —— 为 true 时不允许再挑色板。</summary>
    public static bool IsColorLocked => Mode != ThemeMode.Custom;

    /// <summary>系统当前是否浅色（Custom 模式下图标也跟随它）。</summary>
    public static bool SystemIsLight => _systemLight;

    /// <summary>图标该用哪一版：显式浅/深优先，其余（含 Custom）跟随系统。</summary>
    public static bool LightForIcons => Mode switch
    {
        ThemeMode.Light => true,
        ThemeMode.Dark => false,
        _ => _systemLight,
    };

    public static void Initialize(ThemeMode mode)
    {
        if (_initialized) return;
        _initialized = true;
        _systemLight = ReadSystemLight();
        Mode = mode;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _initialized = false;
    }

    /// <summary>切换模式；仅当确实发生变化时返回 true 并广播。</summary>
    public static bool SetMode(ThemeMode mode)
    {
        if (Mode == mode) return false;
        Mode = mode;
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 把盒子的自定义底色解析成实际生效的底色。
    /// Custom 时原样返回（空值退默认深色），其余模式返回主题色。
    /// </summary>
    public static string ResolveColor(string? customHex) => Mode switch
    {
        ThemeMode.Light => LightBackColor,
        ThemeMode.Dark => DarkBackColor,
        ThemeMode.System => _systemLight ? LightBackColor : DarkBackColor,
        _ => string.IsNullOrWhiteSpace(customHex) ? AppearanceTheme.DefaultBackColor : customHex!,
    };

    /// <summary>某个自定义色在当前模式下是否真的会被采用（供 UI 判断是否灰掉色板）。</summary>
    public static bool IsCustomColorActive => !IsColorLocked;

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        var light = ReadSystemLight();
        if (light == _systemLight) return;
        _systemLight = light;
        // 只有 System 模式的底色会跟着变；但 Custom 模式的图标也跟随系统，
        // 所以这里统一广播，由订阅方各自判断要不要刷新。
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        var app = Application.Current;
        if (app == null)
        {
            Changed?.Invoke();
            return;
        }
        // SystemEvents 回调不在 UI 线程，必须切回 Dispatcher 再改 WPF 资源
        app.Dispatcher.Invoke(() => Changed?.Invoke());
    }

    public static bool ReadSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // 读到 0 表示深色主题；键缺失/异常时按浅色处理
            return key?.GetValue("AppsUseLightTheme") is not int i || i != 0;
        }
        catch
        {
            return true;
        }
    }
}
