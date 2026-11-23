using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace ToniaDeskBox.Services;

/// <summary>
/// 全局热键。原来 <c>AppSettings.Hotkey</c> 是个死字段（全仓没有 RegisterHotKey），
/// 「想看一眼盒子」只能先把鼠标挪过去 —— 而这是每天几十次的动作。
///
/// 两个热键共用同一套机制：
/// - 呼出：把全部盒子显示出来并提到桌面层最前；
/// - 隐藏：把全部盒子收起来（默认不占用键位，留空即关闭）。
///
/// 用消息专用窗口收 WM_HOTKEY，不依赖任何可见窗口；
/// 注册时带上 MOD_NOREPEAT，避免按住不放把热键刷成一串。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>没配置过时的默认呼出键。Ctrl+Alt+D 与大多数软件冲突概率低。</summary>
    public const string DefaultShow = "Ctrl+Alt+D";

    /// <summary>显式关闭热键的写法（设置界面「清除」写这个）。</summary>
    public const string Disabled = "-";

    private const int WM_HOTKEY = 0x0312;
    private const int ShowId = 0xA1;
    private const int HideId = 0xA2;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    public event Action? ShowRequested;
    public event Action? HideRequested;

    private HwndSource? _source;
    private bool _showRegistered;
    private bool _hideRegistered;

    /// <summary>当前生效的呼出键（已格式化）；空串表示未注册。</summary>
    public string ShowHotkey { get; private set; } = "";
    public string HideHotkey { get; private set; } = "";

    private void EnsureSource()
    {
        if (_source != null) return;
        var parameters = new HwndSourceParameters("ToniaDeskBox.Hotkey")
        {
            ParentWindow = new IntPtr(-3),   // HWND_MESSAGE：无可见窗口，只收消息
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// 应用一组热键配置。返回空串表示成功；否则是给人看的原因
    /// （键位被别的程序占用时会失败，这是最常见的情况）。
    /// </summary>
    public string Apply(string? showSpec, string? hideSpec)
    {
        EnsureSource();
        UnregisterAll();

        ShowHotkey = "";
        HideHotkey = "";

        var show = Normalize(showSpec, DefaultShow);
        if (show.Length > 0)
        {
            if (!TryParse(show, out var mods, out var vk))
                return $"呼出热键格式无法识别：{show}";
            if (!RegisterHotKey(_source!.Handle, ShowId, mods | MOD_NOREPEAT, vk))
                return $"呼出热键 {Format(mods, vk)} 注册失败，可能已被其他程序占用";
            _showRegistered = true;
            ShowHotkey = Format(mods, vk);
        }

        var hide = Normalize(hideSpec, "");
        if (hide.Length > 0)
        {
            if (!TryParse(hide, out var mods, out var vk))
            {
                UnregisterShow();
                return $"隐藏热键格式无法识别：{hide}";
            }
            if (!RegisterHotKey(_source!.Handle, HideId, mods | MOD_NOREPEAT, vk))
            {
                UnregisterShow();
                return $"隐藏热键 {Format(mods, vk)} 注册失败，可能已被其他程序占用";
            }
            _hideRegistered = true;
            HideHotkey = Format(mods, vk);
        }

        return "";
    }

    /// <summary>空 / null → 用默认值；<see cref="Disabled"/> → 不注册。</summary>
    private static string Normalize(string? spec, string fallback)
    {
        if (string.IsNullOrWhiteSpace(spec)) return fallback;
        var trimmed = spec.Trim();
        return string.Equals(trimmed, Disabled, StringComparison.Ordinal) ? "" : trimmed;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case ShowId:
                ShowRequested?.Invoke();
                handled = true;
                break;
            case HideId:
                HideRequested?.Invoke();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void UnregisterShow()
    {
        if (_showRegistered && _source != null) UnregisterHotKey(_source.Handle, ShowId);
        _showRegistered = false;
        ShowHotkey = "";
    }

    private void UnregisterAll()
    {
        if (_source == null) return;
        if (_showRegistered) UnregisterHotKey(_source.Handle, ShowId);
        if (_hideRegistered) UnregisterHotKey(_source.Handle, HideId);
        _showRegistered = false;
        _hideRegistered = false;
    }

    // ---------- 键位串 ↔ 修饰符 / 虚拟键 ----------

    /// <summary>
    /// 解析 "Ctrl+Alt+D" 这类写法。允许 Ctrl/Control、Alt、Shift、Win，键名用 WPF 的 Key 名称
    /// （D、F5、Space、Oem3…），因此与设置界面里录制出来的格式天然一致。
    /// </summary>
    public static bool TryParse(string? spec, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;

        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    continue;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    continue;
            }

            if (!Enum.TryParse<Key>(raw, ignoreCase: true, out var key)) return false;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return false;
            // 只有一个主键，出现第二个说明串写歪了
            if (virtualKey != 0) return false;
            virtualKey = (uint)vk;
        }

        // 没有主键（只有 Ctrl+Alt）或没有修饰符（裸键）都不作为全局热键注册
        return virtualKey != 0 && modifiers != 0;
    }

    /// <summary>把修饰符 + 虚拟键拼成固定顺序的展示串，保证配置里存的和界面显示的一致。</summary>
    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(4);
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");

        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        parts.Add(key == Key.None ? "?" : key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>从按下的键事件里取组合（设置界面录制用）。没有主键时返回 null。</summary>
    public static string? FromKeyEvent(Key key, ModifierKeys modifiers)
    {
        // 修饰键本身不作为主键
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None)
            return null;

        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
        if (mods == 0) return null;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        return vk == 0 ? null : Format(mods, (uint)vk);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Dispose()
    {
        UnregisterAll();
        if (_source == null) return;
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}
