using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;
using ToniaDeskBox.Windows;
using WF = System.Windows.Forms;

namespace ToniaDeskBox;

public partial class App : Application
{
    /// <summary>
    /// 安装包用 `ToniaDeskBox.exe --quit` 请运行中的实例退出。
    /// 走命名信号而不是强杀：退出前能把配置落盘、托盘图标正常移除，
    /// 也不需要安装包去调 taskkill 这类外部程序。
    /// </summary>
    private const string QuitSignalName = @"Local\ToniaDeskBox_Quit";

    private Mutex? _mutex;
    private EventWaitHandle? _quitSignal;
    private Thread? _quitListener;
    private TrayService? _tray;
    private AppearanceTheme? _appTheme;
    private readonly List<BoxWindow> _boxWindows = new();
    private DispatcherTimer? _healTimer;
    private bool _startupCompleted;

    public ConfigStore Store { get; private set; } = null!;
    public AppConfig Config { get; private set; } = null!;
    public IReadOnlyList<BoxWindow> BoxWindows => _boxWindows;

    /// <summary>存储迁移进行中：此时盒子目录正在被搬，自愈逻辑必须让路，否则会重建窗口打断迁移。</summary>
    public bool StorageBusy { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 安装包请现有实例退出：这一步必须早于单实例检查，否则只会弹「已在运行」
        if (e.Args.Any(a => string.Equals(a, "--quit", StringComparison.OrdinalIgnoreCase)))
        {
            SignalExistingInstanceToQuit();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        // Theme.* 全局默认笔刷必须在任何窗口/控件样式解析前挂好。
        // 此刻还没读到配置里的主题模式，等 Config 加载后再按主题重算一次。
        _appTheme = AppearanceTheme.AttachApplication();

        _mutex = new Mutex(true, @"Local\ToniaDeskBox_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Tonia-DeskBox 已在运行。", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        StartQuitListener();

        Store = new ConfigStore();        Config = Store.Load();

        // 主题服务必须早于托盘和任何盒子窗口：图标版本、应用级默认笔刷都依赖它
        ThemeService.Initialize(Config.Settings.ThemeMode);
        ThemeService.Changed += OnThemeChanged;
        AppIconService.Initialize(ThemeService.LightForIcons);
        _appTheme.Update(ThemeService.ResolveColor(null), AppearanceTheme.DefaultOpacity, "新建盒子");

        _tray = new TrayService(Config, () => Store.DataRoot, Config.Settings.Autorun || AutoStartService.IsEnabled());
        _tray.NewBoxRequested += CreateBox;
        _tray.ShowAllRequested += ShowAll;
        _tray.HideAllRequested += HideAll;
        _tray.AutoSortRequested += AutoSortBoxes;
        _tray.AutoStartToggled += OnAutoStartToggled;
        _tray.GlobalSettingsRequested += OpenGlobalSettings;
        _tray.ExportRequested += ExportBackup;
        _tray.UpdateRequested += OpenUpdate;
        _tray.ExitRequested += ExitApp;

        // 首启口径：ConfigStore.Load() 在无配置时产出 1 个空盒（需求 §12 验收清单）。
        // 曾经在这里按 Boxes.Count==0 追加"系统工具/办公工具"并自动排列，但 Load() 已先落盘，
        // 该分支永远为 false；且它与"首启一个空盒"的产品口径冲突，故整段移除。
        foreach (var box in Config.Boxes)
            ShowBox(box);

        _healTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _healTimer.Tick += (_, _) => HealBoxes();
        _healTimer.Start();

        _ = PrefetchIconsAsync();
        _startupCompleted = true;
    }

    // ---------- 被安装包请求退出 ----------

    /// <summary>
    /// 起一个后台线程守着退出信号。信号由 `--quit` 那个短命进程发出，
    /// 收到后回到 UI 线程走正常退出流程（保存配置、移除托盘图标）。
    /// </summary>
    private void StartQuitListener()
    {
        try
        {
            _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName);
        }
        catch
        {
            return; // 建不出来不影响正常使用，只是安装时可能要多等一会儿
        }

        _quitListener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_quitSignal.WaitOne()) continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_startupCompleted)
                    {
                        // 启动还没完成就被要求退出：直接收摊，别把半截界面留给用户
                        Shutdown();
                        return;
                    }
                    ExitApp();
                }));
            }
        })
        {
            IsBackground = true,
            Name = "quit-listener",
        };
        _quitListener.Start();
    }

    /// <summary>向已在运行的实例发退出信号；没有在运行就安静返回。</summary>
    private static void SignalExistingInstanceToQuit()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(QuitSignalName);
            handle.Set();
        }
        catch
        {
            // 没有在运行的实例：这次调用本就无事可做
        }
    }

    // 后台预热图标缓存：启动后先把各盒子的图标提取好，用户点盒子时面板能秒开
    private async Task PrefetchIconsAsync()    {
        await Task.Delay(1000);
        foreach (var box in Config.Boxes)
        {
            var dir = Store.BoxDir(box);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFileSystemEntries(dir))
            {
                try { await IconService.GetAsync(file); }
                catch { }
            }
        }
    }

    private void HealBoxes()
    {
        // 迁移期间目录正在被搬走，盒子不可见是正常的，不要在这里抢着重建
        if (StorageBusy) return;

        bool positionChanged = false;
        foreach (var box in Config.Boxes)
        {
            if (box.Hidden) continue;
            var w = _boxWindows.FirstOrDefault(x => ReferenceEquals(x.Box, box));
            if (w == null || !w.IsLoaded || w.Visibility != Visibility.Visible)
            {
                RecreateBox(box);
                continue;
            }
            // explorer 重启后 Progman 是新窗口，需要重新钉到桌面
            w.EnsureDesktopOwner();
            // 兜底：位置漂移出工作区（如任务栏调整/分辨率变化）自动拉回，
            // 同时把修正后的坐标写回配置，避免重启又弹回旧位置
            if (SnapService.ClampIntoScreen(w))
            {
                box.X = w.Left;
                box.Y = w.Top;
                positionChanged = true;
            }
        }
        if (positionChanged) Store.Save(Config);
    }

    private void AutoSortBoxes()
    {
        var wins = _boxWindows.Where(w => w.IsLoaded && w.ActualWidth > 0).ToList();
        if (wins.Count == 0) return;

        var screen = WF.Screen.PrimaryScreen;
        if (screen == null) return;
        var wa = screen.WorkingArea;
        double dpi = VisualTreeHelper.GetDpi(wins[0]).PixelsPerDip;
        double totalW = wins.Sum(w => w.ActualWidth);
        double x = wa.Left / dpi + (wa.Width / dpi - totalW) / 2;
        double bottom = wa.Bottom / dpi;

        foreach (var w in wins)
        {
            w.Left = x;
            w.Top = bottom - w.ActualHeight;
            w.Box.X = x;
            w.Box.Y = w.Top;
            w.SnapPanel();
            x += w.ActualWidth;
        }
        Store.Save(Config);
    }

    private void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        LogError(args.Exception.ToString());
        MessageBox.Show($"发生错误：{args.Exception.Message}\n\n详细信息见数据目录 error.log",
            "Tonia-DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        args.Handled = true;

        if (_startupCompleted) return;

        // 启动阶段失败必须释放单实例锁并退出：
        // ShutdownMode=OnExplicitShutdown 下，仅标记 Handled 会留下一个无界面却占着 Mutex 的进程，
        // 用户再次双击只会看到"已在运行"，只能手动结束进程。
        try { _mutex?.Dispose(); }
        catch { }
        _mutex = null;
        Shutdown(1);
    }

    private void LogError(string message)
    {
        try
        {
            var dir = Store?.ConfigDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tonia-DeskBox");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n\n");
        }
        catch
        {
            // 日志写不进去也不能让异常吞掉主线程
        }
    }

    private void ShowBox(BoxConfig box)
    {
        var w = new BoxWindow(box, Config, Store);
        w.BoxDeleted += OnBoxDeleted;
        _boxWindows.Add(w);
        w.Show();
        if (box.Hidden) w.Hide();
    }

    public void RecreateBox(BoxConfig box)
    {
        var old = _boxWindows.FirstOrDefault(w => ReferenceEquals(w.Box, box));
        if (old != null)
        {
            _boxWindows.Remove(old);
            old.BoxDeleted -= OnBoxDeleted;
            try { old.Close(); }
            catch (InvalidOperationException) { }
        }
        ShowBox(box);
    }

    /// <summary>
    /// 存储位置迁移完成后调用。所有盒子的物理目录都变了，已打开的窗口和面板
    /// 还连着旧路径，必须整体重建；同时重新预热图标缓存。
    /// </summary>
    public void OnDataRootChanged()
    {
        _healTimer?.Stop();
        var boxes = Config.Boxes.ToList();

        foreach (var w in _boxWindows.ToList())
        {
            w.BoxDeleted -= OnBoxDeleted;
            try { w.Close(); }
            catch (InvalidOperationException) { }
        }
        _boxWindows.Clear();

        foreach (var b in boxes) ShowBox(b);
        _healTimer?.Start();

        _ = PrefetchIconsAsync();
    }

    private void CreateBox()
    {
        var existing = Config.Boxes.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var name = "新建盒子";
        for (int i = 2; existing.Contains(name); i++)
            name = $"新建盒子{i}";

        var box = new BoxConfig { Name = name };
        if (Config.Settings.Global is { } g)
        {
            box.Opacity = g.Opacity;
            box.BackColor = g.BackColor;
            box.Rounded = g.Rounded;
            box.CornerSize = g.CornerSize;
            box.Topmost = g.Topmost;
        }
        Config.Boxes.Add(box);
        Store.Save(Config);
        ShowBox(box);
    }

    private void OpenGlobalSettings()
    {
        var dlg = new GlobalSettingsWindow(Config, Store)
        {
            Owner = _boxWindows.FirstOrDefault(w => w.IsLoaded),
        };
        dlg.ShowDialog();
    }

    /// <summary>
    /// 主题变化：手动切换模式，或「跟随系统」时系统改了深浅。
    /// 底色是渲染时解析出来的、不写回盒子数据，所以这里只刷新，不动文件。
    /// </summary>
    private void OnThemeChanged()
    {
        if (Config.Settings.ThemeMode != ThemeService.Mode)
        {
            Config.Settings.ThemeMode = ThemeService.Mode;
            Store.Save(Config);
        }

        // 应用级默认笔刷（ToolTip / 无父窗口的菜单）跟着一起换
        _appTheme?.Update(ThemeService.ResolveColor(null), AppearanceTheme.DefaultOpacity, "新建盒子");
        AppIconService.SetLight(ThemeService.LightForIcons);

        foreach (var w in _boxWindows.ToList())
            w.ApplySettings();
    }

    public void ApplyGlobalToAllBoxes(GlobalBoxSettings g)
    {
        foreach (var box in Config.Boxes)
        {
            box.Opacity = g.Opacity;
            box.BackColor = g.BackColor;
            box.Rounded = g.Rounded;
            box.CornerSize = g.CornerSize;
            box.Topmost = g.Topmost;
        }
        Store.Save(Config);

        // 底色 / 圆角 / 分类色都是可变笔刷，原地刷新即可，无需重建窗口
        foreach (var w in _boxWindows.ToList())
            w.ApplySettings();
    }

    private void OnBoxDeleted(BoxConfig box)
    {
        var w = _boxWindows.FirstOrDefault(x => ReferenceEquals(x.Box, box));
        if (w != null)
        {
            _boxWindows.Remove(w);
            w.BoxDeleted -= OnBoxDeleted;
            w.Close();
        }
        Config.Boxes.Remove(box);
        Store.Save(Config);
    }

    private void ShowAll()
    {
        foreach (var w in _boxWindows.ToList())
            w.SetVisible(true);
    }

    private void HideAll()
    {
        foreach (var w in _boxWindows.ToList())
            w.SetVisible(false);
    }

    private void OnAutoStartToggled(bool enabled)
    {
        try
        {
            AutoStartService.Set(enabled);
            Config.Settings.Autorun = enabled;
            Store.Save(Config);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置开机自启失败：{ex.Message}", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportBackup()
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出全部盒子备份",
            FileName = $"Tonia-DeskBox-备份-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "Zip 压缩包 (*.zip)|*.zip",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            BackupService.ExportZip(Store.DataRoot, dlg.FileName,
                System.IO.Path.Combine(Store.ConfigDir, "config.json"));
            MessageBox.Show($"备份完成：\n{dlg.FileName}", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"备份失败：{ex.Message}", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 托盘「检查更新」。查询、下载、拉起安装包都由 UpdateWindow 负责；
    /// 只有安装包真的起来了才退出本进程——因为安装包要替换的正是正在运行的 exe。
    /// 盒子的数据目录与 config.json 都在安装包的覆盖范围之外，不受影响。
    /// </summary>
    private void OpenUpdate()
    {
        var dlg = new UpdateWindow(Config, Store);
        dlg.ShowDialog();

        if (!dlg.InstallerLaunched) return;

        // 退出前再落一次盘，避免安装包立刻结束进程时丢掉最后一次改动
        Store.Save(Config);
        ExitApp();
    }

    private void ExitApp()
    {
        foreach (var w in _boxWindows.ToList())
            w.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeService.Changed -= OnThemeChanged;
        ThemeService.Shutdown();
        _tray?.Dispose();
        AppIconService.Shutdown();
        _quitSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
