using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ToniaDeskBox.Models;
using WF = System.Windows.Forms;

namespace ToniaDeskBox.Services;

/// <summary>
/// 托盘图标 + 右键菜单。
/// 菜单不再用 WinForms ContextMenuStrip（无法套用应用主题），改为 WPF ContextMenu，
/// 与盒子 / 面板 / 设置窗口共用同一套 Theme.* 笔刷。
/// </summary>
public class TrayService : IDisposable
{
    private readonly WF.NotifyIcon _notifyIcon;
    private readonly AppConfig _config;
    // 数据根可在设置里迁移，所以取实时值而不是构造时快照
    private readonly Func<string> _dataRoot;
    private readonly bool _autoStart;
    private System.Drawing.Icon? _appIcon;
    private ContextMenu? _openMenu;
    // 托盘菜单没有父窗口：给 Popup 一个不可见的宿主席位，否则菜单会立刻自行关闭
    private Window? _menuHost;
    private IntPtr _prevForeground;

    public event Action? NewBoxRequested;
    public event Action? ShowAllRequested;
    public event Action? HideAllRequested;
    public event Action? AutoSortRequested;
    public event Action<bool>? AutoStartToggled;
    public event Action? GlobalSettingsRequested;
    public event Action? ExportRequested;
    public event Action? UpdateRequested;
    public event Action? ExitRequested;

    public TrayService(AppConfig config, Func<string> dataRoot, bool autoStart)
    {
        _config = config;
        _dataRoot = dataRoot;
        _autoStart = autoStart;

        _notifyIcon = new WF.NotifyIcon
        {
            Text = "Tonia-DeskBox",
            Icon = LoadAppIcon() ?? System.Drawing.SystemIcons.Application,
            Visible = true,
        };
        _notifyIcon.MouseUp += OnTrayMouseUp;

        // 系统深浅色切换时同步托盘图标（窗口标题栏图标由 DynamicResource 自动刷新）
        AppIconService.ThemeChanged += OnThemeChanged;
    }

    private System.Drawing.Icon? LoadAppIcon()
    {
        _appIcon = AppIconService.LoadTrayIcon();
        return _appIcon;
    }

    private void OnTrayMouseUp(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Right) return;
        ShowMenu();
    }

    // ---------- 托盘菜单（WPF，与其它窗口同一套外观） ----------

    private void ShowMenu()
    {
        // 托盘图标连续点两次时先收掉旧菜单，避免叠两层
        if (_openMenu != null)
        {
            _openMenu.IsOpen = false;
            _openMenu = null;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = GetMenuHost(),
            Placement = PlacementMode.MousePoint,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
        };

        // 托盘菜单没有「所属盒子」，跟随全局默认外观；没配过就取第一个盒子的外观
        // 主题模式下底色由主题接管，所以还要过一层 ResolveColor
        var (back, opacity) = ResolveAppearance();
        AppearanceTheme.Attach(menu).Update(
            ThemeService.ResolveColor(back), opacity, "托盘菜单", minAlpha: 0.96);

        AddItem(menu, "新建盒子", () => NewBoxRequested?.Invoke());
        menu.Items.Add(new Separator());
        AddItem(menu, "显示全部", () => ShowAllRequested?.Invoke());
        AddItem(menu, "隐藏全部", () => HideAllRequested?.Invoke());
        AddItem(menu, "整理盒子", () => AutoSortRequested?.Invoke());
        menu.Items.Add(new Separator());
        AddItem(menu, "全局盒子设置…", () => GlobalSettingsRequested?.Invoke());

        var miAuto = AddItem(menu, "开机自启", null);
        miAuto.IsCheckable = true;
        miAuto.IsChecked = _autoStart;
        miAuto.Click += (_, _) => AutoStartToggled?.Invoke(miAuto.IsChecked);

        AddItem(menu, "打开数据目录", () =>
        {
            var dir = _dataRoot();
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
            catch
            {
                // 外置盘被拔掉等情况：不弹窗打断，用户可在设置里看到实际路径
            }
        });
        AddItem(menu, "导出备份（Zip）…", () => ExportRequested?.Invoke());
        menu.Items.Add(new Separator());
        AddItem(menu, "检查更新…", () => UpdateRequested?.Invoke());
        menu.Items.Add(new Separator());
        AddItem(menu, "退出", () => ExitRequested?.Invoke());

        _openMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMenu, menu)) _openMenu = null;
            RestoreForeground();
        };
        // 先在「菜单还没弹出」时把宿主窗口激活为前台：之后点别处 / Esc 才会交给本线程处理。
        // 反过来（弹完再抢前台）会让 Popup 失去捕获、菜单立刻消失。
        _prevForeground = GetForegroundWindow();
        _menuHost?.Activate();
        menu.IsOpen = true;
    }

    // 1×1 全透明宿主：不出现在任务栏/Alt+Tab，只作为菜单的 PlacementTarget 与宿主窗口
    private Window GetMenuHost()
    {
        if (_menuHost is { IsLoaded: true }) return _menuHost;

        _menuHost = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = true,
            ResizeMode = ResizeMode.NoResize,
            Width = 1,
            Height = 1,
            Left = SystemParameters.WorkArea.Right - 1,
            Top = SystemParameters.WorkArea.Bottom - 1,
        };
        _menuHost.Show();
        return _menuHost;
    }

    private static MenuItem AddItem(ContextMenu menu, string header, Action? action)
    {
        var mi = new MenuItem { Header = header };
        if (action != null) mi.Click += (_, _) => action();
        menu.Items.Add(mi);
        return mi;
    }

    private (string BackColor, double Opacity) ResolveAppearance()
    {
        if (_config.Settings.Global is { } g) return (g.BackColor, g.Opacity);
        if (_config.Boxes.FirstOrDefault() is { } b) return (b.BackColor, b.Opacity);
        return (AppearanceTheme.DefaultBackColor, AppearanceTheme.DefaultOpacity);
    }

    // 菜单收起后把前台还给调用前的窗口，避免焦点停在这个 1×1 透明宿主上
    private void RestoreForeground()
    {
        if (_prevForeground == IntPtr.Zero) return;
        var prev = _prevForeground;
        _prevForeground = IntPtr.Zero;
        try { SetForegroundWindow(prev); }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---------- 图标主题 ----------

    private void OnThemeChanged()
    {
        var next = AppIconService.LoadTrayIcon();
        if (next == null) return;
        var old = _appIcon;
        _appIcon = next;
        _notifyIcon.Icon = next;
        old?.Dispose();
    }

    public void Dispose()
    {
        AppIconService.ThemeChanged -= OnThemeChanged;
        _notifyIcon.MouseUp -= OnTrayMouseUp;
        if (_openMenu != null)
        {
            _openMenu.IsOpen = false;
            _openMenu = null;
        }
        _menuHost?.Close();
        _menuHost = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon?.Dispose();
    }
}
