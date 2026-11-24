using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;
using ToniaDeskBox.Windows;

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
    private HotkeyService? _hotkeys;
    private AppearanceTheme? _appTheme;
    private readonly List<BoxWindow> _boxWindows = new();
    private DispatcherTimer? _healTimer;
    private bool _startupCompleted;

    /// <summary>
    /// 「一键整理」期间的落位会话：非 null 时，新建盒子按这份障碍清单找空地。
    /// 清单里的已有盒子坐标**只读不改**，新盒子只是追加进来，所以一次整理建出的
    /// 多个盒子彼此也不会叠在一起。对话框关闭后置回 null，普通新建仍走原来的级联摆放。
    /// </summary>
    private List<BoxArea>? _tidyObstacles;

    /// <summary>本轮整理是否已把「未搬走的桌面图标」加进障碍集（避免重复叠加）。</summary>
    private bool _tidyIconsAdded;
    /// <summary>导出/导入进行中：托盘菜单可以反复点，必须挡住重复任务（两者都会动数据目录）。</summary>
    private bool _transferBusy;

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
        WireTray();
        SetupHotkeys();

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

    /// <summary>
    /// 托盘事件接线。导入备份会整体换掉 <see cref="Config"/>（托盘持有的是对象引用），
    /// 所以重建托盘时要能重复调用。
    /// </summary>
    private void WireTray()
    {
        if (_tray == null) return;
        _tray.NewBoxRequested += CreateBox;
        _tray.MapFolderRequested += MapFolder;
        _tray.ShowAllRequested += ShowAll;
        _tray.HideAllRequested += HideAll;
        _tray.TidyRequested += TidyDesktop;
        _tray.RestoreRequested += RestoreDesktop;
        _tray.AutoSortRequested += AutoSortBoxes;
        _tray.AutoStartToggled += OnAutoStartToggled;
        _tray.GlobalSettingsRequested += OpenGlobalSettings;
        _tray.ExportRequested += ExportBackup;
        _tray.ImportRequested += ImportBackup;
        _tray.UpdateRequested += OpenUpdate;
        _tray.ExitRequested += ExitApp;
    }

    // ---------- 全局热键 ----------

    /// <summary>
    /// 建立热键通道。键位来自配置：呼出默认 Ctrl+Alt+D（与托盘双击同一套动作），
    /// 隐藏默认不占用键位。注册失败最常见的原因是键位被别的程序占了 —— 写日志，
    /// 用户可以在全局设置里换一个，并在设置窗口里看到明确失败原因。
    /// </summary>
    private void SetupHotkeys()
    {
        _hotkeys ??= new HotkeyService();
        _hotkeys.ShowRequested -= ShowAllAndFront;
        _hotkeys.HideRequested -= HideAll;
        _hotkeys.ShowRequested += ShowAllAndFront;
        _hotkeys.HideRequested += HideAll;
        ApplyHotkeys(reportToUser: false);
    }

    /// <summary>把配置里的键位应用到系统。返回注册失败原因（空串表示成功）。</summary>
    public string ApplyHotkeys(bool reportToUser)
    {
        if (_hotkeys == null) return "";
        var error = _hotkeys.Apply(Config.Settings.Hotkey, Config.Settings.HideHotkey);
        if (error.Length > 0)
        {
            Store.Log($"热键注册失败：{error}");
            if (reportToUser)
                MessageBox.Show(error, "全局热键", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return error;
    }

    public string CurrentShowHotkey => _hotkeys?.ShowHotkey ?? "";
    public string CurrentHideHotkey => _hotkeys?.HideHotkey ?? "";

    /// <summary>呼出全部盒子并提到桌面层最前。托盘双击与热键共用。</summary>
    private void ShowAllAndFront()
    {
        foreach (var w in _boxWindows.ToList())
        {
            if (!w.IsLoaded) continue;
            if (w.Box.Hidden) w.SetVisible(true);
        }
        // 逐窗提层放在显示之后：刚 Show 的窗口此刻才拿到有效 HWND
        foreach (var w in _boxWindows.ToList())
            w.BringToFront();
    }

    // 后台预热图标缓存：启动后先把各盒子的图标提取好，用户点盒子时面板能秒开
    private async Task PrefetchIconsAsync()
    {
        await Task.Delay(1000);
        foreach (var box in Config.Boxes)
        {
            string dir;
            try { dir = Store.SourceDir(box); }
            catch { continue; }
            if (!Directory.Exists(dir)) continue;

            // 映射盒可能指向一个几百项的目录：预热只取前 60 个，
            // 别为了"秒开"把启动阶段的 IO 拖长
            List<string> entries;
            try
            {
                var limit = box.Kind == BoxKind.Portal ? 60 : 400;
                entries = Directory.EnumerateFileSystemEntries(dir).Take(limit).ToList();
            }
            catch
            {
                continue;   // 网络盘 / 无权限：跳过，点开面板时再按需提取
            }

            foreach (var file in entries)
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

        var wa = ScreenUtil.PrimaryWorkAreaPx();
        double dpi = VisualTreeHelper.GetDpi(wins[0]).PixelsPerDip;
        double totalW = wins.Sum(w => w.ActualWidth);
        double x = wa.X / dpi + (wa.Width / dpi - totalW) / 2;
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

    private BoxWindow ShowBox(BoxConfig box)
    {
        var w = new BoxWindow(box, Config, Store);
        w.BoxDeleted += OnBoxDeleted;
        _boxWindows.Add(w);
        w.Show();
        if (box.Hidden) w.Hide();
        return w;
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
    public void OnDataRootChanged() => RebuildBoxWindows();

    /// <summary>
    /// 关掉全部盒子窗口（连同事件订阅）。存储迁移、导入备份这类"底层数据整体换了"的操作都走这里，
    /// 否则旧窗口还连着旧目录/旧配置对象。
    /// </summary>
    private void CloseAllBoxWindows()
    {
        foreach (var w in _boxWindows.ToList())
        {
            w.BoxDeleted -= OnBoxDeleted;
            try { w.Close(); }
            catch (InvalidOperationException) { }
        }
        _boxWindows.Clear();
    }

    /// <summary>按当前 <see cref="Config"/> 重建所有盒子窗口，并重启自愈与图标预热。</summary>
    private void RebuildBoxWindows()
    {
        _healTimer?.Stop();
        var boxes = Config.Boxes.ToList();
        CloseAllBoxWindows();
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
        ApplyBoxDefaults(box);
        PlaceNewBox(box);
        Config.Boxes.Add(box);
        Store.Save(Config);
        ShowBox(box);
    }

    /// <summary>新盒子继承全局默认外观（没配过全局设置就用模型默认值）。</summary>
    private void ApplyBoxDefaults(BoxConfig box)
    {
        if (Config.Settings.Global is not { } g) return;
        box.Opacity = g.Opacity;
        box.BackColor = g.BackColor;
        box.Rounded = g.Rounded;
        box.CornerSize = g.CornerSize;
        box.Topmost = g.Topmost;
    }

    /// <summary>把新盒子错开摆放，避免完全叠在一起；出界就拉回工作区。</summary>
    private void PlaceNewBox(BoxConfig box)
    {
        int n = Config.Boxes.Count;
        box.X = 120 + n * 26;
        box.Y = 120 + n * 20;
        ClampIntoWorkArea(box);
    }

    // ---------- 整理时的新盒子落位 ----------

    /// <summary>进入「一键整理」的落位会话：抓一份当前桌面占用情况当障碍。</summary>
    private void BeginTidyPlacement()
    {
        _tidyObstacles = CollectOccupiedAreas();
        _tidyIconsAdded = false;
    }

    private void EndTidyPlacement()
    {
        _tidyObstacles = null;
        _tidyIconsAdded = false;
    }

    /// <summary>
    /// 把「整理后仍留在桌面」的图标也加入障碍集 —— 新盒子不该盖在用户还要点的快捷方式上。
    ///
    /// 只在第一次建盒子前生效：这份矩形来自整理前的桌面布局，文件搬走之后它们就不代表现状了；
    /// 而且一轮整理里要连续建多个盒子，重复叠加会让障碍集膨胀。
    /// </summary>
    private void AddKeptIconObstacles(IReadOnlyList<BoxArea> keepIcons)
    {
        var obstacles = _tidyObstacles;
        if (obstacles == null || _tidyIconsAdded) return;
        _tidyIconsAdded = true;

        foreach (var area in keepIcons)
        {
            if (area.W <= 0 || area.H <= 0) continue;
            obstacles.Add(area);
        }
    }

    /// <summary>主屏工作区（已排除任务栏），换成不依赖 WPF 的矩形给落位服务用。</summary>
    private static BoxArea WorkAreaArea()
    {
        var wa = SystemParameters.WorkArea;
        return new BoxArea(wa.X, wa.Y, wa.Width, wa.Height);
    }

    /// <summary>
    /// 当前桌面上已经被占住的地方：可见盒子的实际矩形（没打开窗口的按配置坐标 + 估算尺寸），
    /// 以及已展开的面板 —— 面板是盒子的一部分，压住它同样会挡住内容。
    ///
    /// 隐藏的盒子不计入：它此刻不在屏幕上，为它留空位只会把新盒子挤到奇怪的地方。
    /// </summary>
    private List<BoxArea> CollectOccupiedAreas()
    {
        var areas = new List<BoxArea>();

        foreach (var box in Config.Boxes)
        {
            if (box.Hidden) continue;

            var win = _boxWindows.FirstOrDefault(x => ReferenceEquals(x.Box, box));
            if (win is { IsVisible: true } && win.ActualWidth > 0 && win.ActualHeight > 0)
            {
                areas.Add(new BoxArea(win.Left, win.Top, win.ActualWidth, win.ActualHeight));
                continue;
            }

            // 窗口还没建/刚被隐藏：用配置坐标 + 估算尺寸。估宽略大于实际常见值，
            // 宁可多留一点缝，也不要因为估小了而和其他盒子贴上
            areas.Add(new BoxArea(box.X, box.Y,
                box.W > 0 ? box.W : BoxPlacementService.DefaultWidth,
                box.H > 0 ? box.H : BoxPlacementService.DefaultHeight));
        }

        foreach (var win in Windows.OfType<PanelWindow>())
        {
            if (!win.IsVisible || win.ActualWidth <= 0 || win.ActualHeight <= 0) continue;
            areas.Add(new BoxArea(win.Left, win.Top, win.ActualWidth, win.ActualHeight));
        }

        return areas;
    }

    /// <summary>给整理新建的盒子挑一块空地，并把它记进障碍集（下一个新盒子会避开它）。</summary>
    private BoxArea PlaceTidyBox(BoxConfig box)
    {
        var obstacles = _tidyObstacles ?? new List<BoxArea>();
        double w = box.W > 0 ? box.W : BoxPlacementService.DefaultWidth;
        double h = box.H > 0 ? box.H : BoxPlacementService.DefaultHeight;

        var (x, y) = BoxPlacementService.FindFreeSpot(WorkAreaArea(), w, h, obstacles);
        box.X = x;
        box.Y = y;

        var area = new BoxArea(x, y, w, h);
        _tidyObstacles?.Add(area);
        return area;
    }

    /// <summary>
    /// 窗口显示后用**实测尺寸**复核一次位置。
    ///
    /// 自适应盒子（SizeToContent）在创建前量不到真实宽高，估算值只够"先摆对大致位置"；
    /// 标题一长盒子就变宽，可能刚好压到隔壁。这一步只挪**这个新盒子**，已有盒子依旧不动。
    /// </summary>
    private void RefineTidyBoxPlacement(BoxConfig box, BoxWindow? window, BoxArea estimate)
    {
        var obstacles = _tidyObstacles;
        if (obstacles == null || window == null) return;

        double w = window.ActualWidth > 0 ? window.ActualWidth : box.W;
        double h = window.ActualHeight > 0 ? window.ActualHeight : box.H;
        if (w <= 0 || h <= 0) return;

        // 先把自己先前占的估算矩形摘掉（按引用值精确匹配），否则会和自己相撞
        obstacles.RemoveAll(a => a == estimate);

        var actual = new BoxArea(window.Left, window.Top, w, h);
        bool clash = obstacles.Any(o => actual.Overlaps(o, BoxPlacementService.Gap));

        if (clash)
        {
            // 就近重排：实测比估算大是常事，此时只该在原地附近挪一点，
            // 而不是跳回屏幕另一头的空位
            var (x, y) = BoxPlacementService.FindFreeSpot(
                WorkAreaArea(), w, h, obstacles, (window.Left, window.Top));
            box.X = x;
            box.Y = y;
            try
            {
                window.Left = x;
                window.Top = y;
            }
            catch (InvalidOperationException)
            {
                // 窗口正在关闭：位置记进配置即可，下次打开会落在正确的地方
            }
            actual = new BoxArea(x, y, w, h);
            try { Store.Save(Config); }
            catch (Exception ex) { Store.Log($"整理后保存盒子位置失败：{ex.Message}"); }
        }
        else
        {
            // Loaded 里的 ClampIntoScreen 可能微调过窗口，以实际位置为准
            box.X = window.Left;
            box.Y = window.Top;
        }

        obstacles.Add(actual);
    }

    // ---------- 文件夹映射（Folder Portal） ----------

    /// <summary>
    /// 创建一个"映射盒"：盒子里显示的是某个已有目录的内容，文件一个都不搬。
    /// 这是本项目从「只能搬」到「也能镜像」的关键一步 —— 以前把 D:\项目\xxx 拖进来
    /// 等于真搬家，IDE 里的路径全失效，所以用户只敢收纳快捷方式。
    /// </summary>
    private void MapFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择要映射到桌面的文件夹（文件仍留在原位置）",
            Multiselect = false,
        };
        if (dlg.ShowDialog() != true) return;

        string folder;
        try { folder = Path.GetFullPath(dlg.FolderName); }
        catch (Exception ex)
        {
            MessageBox.Show($"路径无效：{ex.Message}", "映射文件夹", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 同一目录映射两次 = 桌面上两个窗看同一堆文件，只会让人困惑
        var dup = Config.Boxes.FirstOrDefault(b =>
            ConfigStore.IsPortal(b) && SamePath(b.PortalPath, folder));
        if (dup != null)
        {
            MessageBox.Show($"这个文件夹已经映射为盒子“{dup.Name}”。", "映射文件夹",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CreatePortalBox(folder);
    }

    private static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private BoxConfig CreatePortalBox(string folder)
    {
        var seedName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(seedName)) seedName = "映射文件夹";

        var box = new BoxConfig
        {
            Name = DesktopTidyService.UniqueBoxName(seedName, Config.Boxes.Select(b => b.Name)),
            Kind = BoxKind.Portal,
            PortalPath = folder,
            // 映射盒默认展开一点：让人一建出来就看见文件夹里有什么
            W = 340,
        };
        ApplyBoxDefaults(box);
        PlaceNewBox(box);
        Config.Boxes.Add(box);
        Store.Save(Config);
        ShowBox(box);
        Store.Log($"新建映射盒：{box.Name} -> {folder}");
        return box;
    }

    // ---------- 一键整理 ----------

    /// <summary>
    /// 托盘「一键整理桌面…」：扫桌面 → 预览勾选 → 批量搬进按分类命名的盒子。
    /// 预览这一步就是试运行，点确认之前不动任何文件。
    /// </summary>
    private void TidyDesktop()
    {
        // 数据根 / 配置目录可能就在桌面上（绿色版），不能把自己搬进自己
        var groups = DesktopTidyService.Scan(
            DesktopTidyService.DesktopDir(), new[] { Store.DataRoot, Store.ConfigDir });

        var dlg = new TidyPreviewWindow(groups, Store, Config, EnsureBox, AddKeptIconObstacles);
        // 只在这次对话期间启用「找空地」落位：普通新建盒子仍走原来的级联摆放，
        // 不改变用户平时点「新建盒子」的手感
        BeginTidyPlacement();
        try { dlg.ShowDialog(); }
        finally { EndTidyPlacement(); }
        if (!dlg.Applied) return;

        RefreshAfterTidy();

        if (dlg.Result is not { } result) return;
        var lines = new List<string> { $"已整理 {result.Moved} 项到对应的盒子。" };
        if (result.Skipped > 0) lines.Add($"{result.Skipped} 项因盒内已有同名条目跳过。");
        if (result.Failed > 0) lines.Add($"{result.Failed} 项移动失败，原文件仍在桌面。");

        // 告诉用户"能撤回"比说"整理完了"更重要 —— 这是敢点这个按钮的前提
        lines.Add("");
        lines.Add("文件去向与桌面图标布局已记录，托盘菜单「一键恢复桌面」可还原。");

        var detail = result.Problems.Count > 0
            ? "\n\n" + string.Join("\n", result.Problems.Take(6)) +
              (result.Problems.Count > 6 ? $"\n…等 {result.Problems.Count} 条" : "")
            : "";

        MessageBox.Show(string.Join("\n", lines) + detail, "一键整理",
            MessageBoxButton.OK,
            result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    // ---------- 一键恢复 ----------

    /// <summary>
    /// 托盘「一键恢复桌面…」：把上次整理搬走的文件放回原位，再还原图标布局。
    ///
    /// 只认**最近一次**整理的快照（<c>tidy-state.json</c>）。做多层历史栈意味着
    /// "恢复"要选恢复到哪一次，而每次整理后桌面上又会有新东西 —— 那个心智模型比"撤回上一步"难懂得多。
    /// </summary>
    private void RestoreDesktop()
    {
        var state = TidyStateStore.TryLoad(Store.ConfigDir, out string loadError);
        if (state == null)
        {
            MessageBox.Show(
                loadError.Length > 0
                    ? loadError + "\n\n如果整理记录已损坏，可以删除配置目录下的 tidy-state.json 后重新整理一次。"
                    : "还没有整理记录。先用「一键整理桌面」整理一次，之后就能恢复到整理前的状态。",
                "一键恢复", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (state.Items.Count == 0 && !state.LayoutCaptured)
        {
            MessageBox.Show("上一条整理记录里没有任何文件或布局信息，无法恢复。",
                "一键恢复", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var plan = TidyRestoreService.BuildPlan(state, DesktopTidyService.DesktopDir());
        var dlg = new TidyRestoreWindow(state, plan, Config);
        dlg.ShowDialog();

        if (!dlg.Applied) return;

        // 先摘 Order、再删空盒子，最后才刷新窗口：反过来会把刚删的盒子又写回配置
        ApplyRestoreBookkeeping(dlg);
        var removedBoxes = RemoveEmptiedTidyBoxes(state, dlg.Result);
        RefreshAfterTidy();

        var lines = new List<string>();
        if (dlg.Result is { } result)
        {
            lines.Add($"已把 {result.Restored} 项放回桌面。");
            if (result.Skipped > 0) lines.Add($"{result.Skipped} 项因桌面已有同名条目跳过（原文件留在盒子里）。");
            if (result.Failed > 0) lines.Add($"{result.Failed} 项无法恢复。");
        }
        else
        {
            lines.Add("已取消恢复。");
        }

        if (removedBoxes.Count > 0)
        {
            lines.Add(removedBoxes.Count == 1
                ? $"已删除恢复后变空的盒子「{removedBoxes[0]}」。"
                : $"已删除恢复后变空的 {removedBoxes.Count} 个盒子：{string.Join("、", removedBoxes)}。");
        }

        if (dlg.LayoutAttempted)
        {
            if (dlg.LayoutError.Length > 0)
                lines.Add($"图标布局未还原：{dlg.LayoutError}");
            else
                lines.Add($"已按整理前的位置摆回 {dlg.LayoutApplied} 个图标"
                    + (dlg.LayoutMissing > 0 ? $"，{dlg.LayoutMissing} 个图标在桌面上已不存在。" : "。"));
        }
        else if (state.LayoutCaptured)
        {
            lines.Add("本次没有需要还原的图标布局。");
        }

        var problems = dlg.Result?.Problems ?? Array.Empty<string>();
        var detail = problems.Count > 0
            ? "\n\n" + string.Join("\n", problems.Take(6)) +
              (problems.Count > 6 ? $"\n…等 {problems.Count} 条" : "")
            : "";

        MessageBox.Show(string.Join("\n", lines) + detail, "一键恢复",
            MessageBoxButton.OK,
            (dlg.Result?.Failed > 0 || dlg.LayoutError.Length > 0)
                ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>
    /// 恢复后的账目：把已经回桌面的文件名从盒子 <c>Order</c> 里摘掉并落盘。
    /// 不做这一步，盒子里会留下指向已不存在文件的排序项，界面上的计数也会对不上。
    /// </summary>
    private void ApplyRestoreBookkeeping(TidyRestoreWindow dlg)
    {
        if (dlg.Result is not { RestoredItems.Count: > 0 } result) return;

        bool changed = false;
        foreach (var item in result.RestoredItems)
        {
            var box = Config.Boxes.FirstOrDefault(b =>
                !ConfigStore.IsPortal(b) &&
                string.Equals(b.Name, item.BoxName, StringComparison.OrdinalIgnoreCase));
            if (box == null) continue;
            if (box.Order.Remove(item.Name)) changed = true;
        }

        if (changed)
        {
            try { Store.Save(Config); }
            catch (Exception ex) { Store.Log($"恢复后保存配置失败：{ex.Message}"); }
        }
    }

    /// <summary>
    /// 恢复完成后，把「本次整理建出来、现在已被搬空」的盒子一并收走。
    ///
    /// 判据刻意收窄：
    /// - 只认快照里出现过的盒子名（本次恢复涉及的），用户自己建的空盒子不碰；
    /// - 必须目录存在且里面真的一个条目都没有 —— 还有文件（比如恢复时因桌面同名被跳过）
    ///   就说明盒子仍在使用，保留；
    /// - 目录删不掉（被占用）不影响配置已移除，只记一条日志。
    /// </summary>
    private IReadOnlyList<string> RemoveEmptiedTidyBoxes(TidyState state, TidyRestoreResult? result)
    {
        if (result is not { RestoredItems.Count: > 0 }) return Array.Empty<string>();

        var involved = state.Items
            .Select(i => i.BoxName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (involved.Count == 0) return Array.Empty<string>();

        BoxConfig? FindBox(string name) => Config.Boxes.FirstOrDefault(b =>
            !ConfigStore.IsPortal(b) &&
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

        string? DirOf(string name)
        {
            var box = FindBox(name);
            return box == null ? null : Store.BoxDir(box);
        }

        var emptied = TidyBoxCleanupService.FindEmptiedBoxes(involved, DirOf);
        if (emptied.Count == 0) return Array.Empty<string>();

        var removed = new List<string>();
        foreach (var name in emptied)
        {
            var box = FindBox(name);
            if (box == null) continue;

            var dir = Store.BoxDir(box);
            var displayName = box.Name;

            try { OnBoxDeleted(box); }   // 关窗口 + 从配置移除 + 落盘
            catch (Exception ex)
            {
                Store.Log($"移除空盒子「{displayName}」失败：{ex.Message}");
                continue;
            }
            removed.Add(displayName);

            // 只有系统占位文件时也把目录收掉（IsEmpty 已把 desktop.ini / Thumbs.db 当空）
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFileSystemEntries(dir))
                    {
                        var fileName = Path.GetFileName(file);
                        bool placeholder = fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                            || fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
                        if (!placeholder) continue;

                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        catch { /* 删不掉就留着，下面的 Delete 会失败并记日志 */ }
                    }

                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
            }
            catch (Exception ex)
            {
                Store.Log($"删除空盒子目录失败（{dir}）：{ex.Message}");
            }
        }

        return removed;
    }

    /// <summary>整理结束后刷新全部盒子的角标、缩略图与已打开的面板。</summary>
    public void RefreshAfterTidy()
    {
        foreach (var w in _boxWindows.ToList())
            w.ApplySettings();
    }

    /// <summary>
    /// 一键整理的目标盒子：同名（非映射盒）直接复用，否则新建。
    /// 重复整理不会越堆越多 —— 第二次点「一键整理」还是落进那几个盒子。
    ///
    /// 复用的盒子**位置一个像素都不动**；只有本次新建的盒子会按桌面占用情况找空地。
    /// </summary>
    public BoxConfig EnsureBox(string name)
    {
        var existing = Config.Boxes.FirstOrDefault(b =>
            !ConfigStore.IsPortal(b) && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var box = new BoxConfig
        {
            Name = DesktopTidyService.UniqueBoxName(name, Config.Boxes.Select(b => b.Name)),
        };
        ApplyBoxDefaults(box);

        bool tidying = _tidyObstacles != null;
        BoxArea estimate = default;
        if (tidying) estimate = PlaceTidyBox(box);
        else PlaceNewBox(box);

        Config.Boxes.Add(box);
        Store.Save(Config);   // 先落盘：万一后续搬运中断，盒子结构不会丢
        var win = ShowBox(box);

        // 自适应盒子要等窗口真正布局完才知道多宽，实测后可能还要微调一次
        if (tidying) RefineTidyBoxPlacement(box, win, estimate);
        return box;
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

    /// <summary>
    /// 导出全部盒子备份。
    ///
    /// 这里过去是「保存对话框 → 同步 ExportZip → MessageBox」，盒子数据本机实测 890 MB / 703 个文件，
    /// 同步压缩要 26 秒，UI 线程整整 26 秒不出消息循环，Windows 直接按 AppHangB1 把它关掉
    /// （事件日志 Application Hang / ID 1002）。所以现在：
    /// 1) 等托盘菜单收起后再弹对话框（否则保存框刚出来就被 RestoreForeground 抢走焦点）；
    /// 2) 压缩丢到后台线程，进度窗负责显示进度 + 取消；
    /// 3) 同一时刻只允许一个导出任务。
    /// </summary>
    private void ExportBackup()
    {
        if (_transferBusy) return;
        _transferBusy = true;
        Dispatcher.BeginInvoke(new Action(OnExportMenuInvoked),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void OnExportMenuInvoked()
    {
        try
        {
            var dlg = new SaveFileDialog
            {
                Title = "导出全部盒子备份",
                FileName = $"Tonia-DeskBox-备份-{DateTime.Now:yyyyMMdd-HHmm}.zip",
                Filter = "Zip 压缩包 (*.zip)|*.zip",
            };
            if (dlg.ShowDialog() != true)
            {
                _transferBusy = false;
                return;
            }

            await ExportToAsync(dlg.FileName);
        }
        catch (Exception ex)
        {
            _transferBusy = false;
            LogError($"导出备份失败：{ex}");
            MessageBox.Show($"备份失败：{ex.Message}", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExportToAsync(string zipPath)
    {
        using var cts = new CancellationTokenSource();
        var win = new TransferProgressWindow(TransferMode.Export, zipPath);
        win.CancelRequested += () =>
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        // 进度窗关掉之后才允许下一次导出
        win.Closed += (_, _) => _transferBusy = false;

        win.Show();
        win.Activate();

        var progress = new Progress<TransferProgress>(win.Report);
        try
        {
            var dataRoot = Store.DataRoot;
            var configPath = System.IO.Path.Combine(Store.ConfigDir, "config.json");

            var result = await Task.Run(
                () => BackupService.ExportZip(dataRoot, zipPath, configPath, progress, cts.Token),
                cts.Token);

            win.Complete(result);
        }
        catch (OperationCanceledException)
        {
            TryDeletePartial(zipPath);
            win.Cancelled();
        }
        catch (Exception ex)
        {
            // 半截的 zip 留着只会让人以为备份成功了
            TryDeletePartial(zipPath);
            LogError($"导出备份失败：{ex}");
            win.Failed(ex.Message);
        }
    }

    private static void TryDeletePartial(string zipPath)
    {
        try
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
        catch
        {
            // 被占用删不掉可以接受，用户能自己删
        }
    }

    // ---------- 导入备份 ----------

    /// <summary>
    /// 导入整套盒子备份（导出的反操作）。
    ///
    /// 语义是「恢复」而不是「合并」：导入后盒子结构与文件跟备份里一致。
    /// 三道保险：
    /// 1) 先只读体检包内容，确认是带配置的本程序备份，把清单摆给用户看；
    /// 2) 先解压到数据根旁边的临时目录，解压失败时现有数据毫发无伤；
    /// 3) 替换只做整目录改名，旧数据留在 *.before-import-时间戳，不删除。
    /// </summary>
    private void ImportBackup()
    {
        if (_transferBusy) return;
        _transferBusy = true;
        // 与导出同理：等托盘菜单彻底收起再弹窗，否则文件对话框刚出来就被抢焦点
        Dispatcher.BeginInvoke(new Action(OnImportMenuInvoked),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void OnImportMenuInvoked()
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "导入盒子备份",
                Filter = "Zip 压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true)
            {
                _transferBusy = false;
                return;
            }

            var zipPath = dlg.FileName;

            // 体检只读包目录，不写盘；坏包/Fake zip 在这一步就会失败
            BackupManifest manifest;
            try
            {
                manifest = await Task.Run(() => BackupService.Inspect(zipPath));
            }
            catch (Exception ex)
            {
                LogError($"读取备份包失败：{ex}");
                MessageBox.Show($"这个文件不是可用的备份包：{ex.Message}", "Tonia-DeskBox",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                _transferBusy = false;
                return;
            }

            if (!manifest.HasConfig)
            {
                MessageBox.Show(
                    "这个 zip 里没有盒子配置（_config/config.json），只能还原文件、无法还原盒子结构。\n\n" +
                    "请确认选的是本程序「导出备份」生成的压缩包（0.2.0 起才会把配置一起打包）。",
                    "Tonia-DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
                _transferBusy = false;
                return;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dataRoot = Store.DataRoot;
            var confirm = new ImportConfirmWindow(
                manifest, Config.Boxes.Select(b => b.Name).ToList(), CountCurrentItems(), dataRoot,
                ImportService.PreviewBackupPath(dataRoot, stamp));

            confirm.ShowDialog();
            if (!confirm.Confirmed)
            {
                _transferBusy = false;
                return;
            }

            await ImportFromAsync(zipPath, manifest, stamp, confirm.Mode);
        }
        catch (Exception ex)
        {
            _transferBusy = false;
            LogError($"导入备份失败：{ex}");
            MessageBox.Show($"导入失败：{ex.Message}", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ImportFromAsync(
        string zipPath, BackupManifest manifest, string stamp, ImportMode mode)
    {
        using var cts = new CancellationTokenSource();
        var win = new TransferProgressWindow(TransferMode.Import, zipPath);
        win.CancelRequested += () =>
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        // 进度窗关掉之后才允许下一次导入/导出
        win.Closed += (_, _) => _transferBusy = false;

        win.Show();
        win.Activate();

        string? staging = null;
        var progress = new Progress<TransferProgress>(win.Report);
        try
        {
            var boxCount = manifest.Config?.Boxes.Count ?? manifest.Boxes.Count;
            staging = ImportService.CreateStagingDir(Store.DataRoot);
            var stagingDir = staging;

            // 两种模式都要先解压到临时目录：解压失败时现有数据毫发无伤
            var result = await Task.Run(
                () => BackupService.ImportZip(zipPath, stagingDir, progress, cts.Token),
                cts.Token);

            // 解压完才动真数据；取消请求在这之前仍然有效
            cts.Token.ThrowIfCancellationRequested();
            win.SetPhase(mode == ImportMode.Replace ? "正在替换数据…" : "正在合并到现有盒子…");

            var dataRoot = Store.DataRoot;

            if (mode == ImportMode.Replace)
            {
                var configDir = Store.ConfigDir;
                var applied = await Task.Run(
                    () => ImportService.Apply(stagingDir, dataRoot, configDir, boxCount, stamp),
                    cts.Token);
                staging = null;   // 已被改名为数据根，别再当临时目录删

                ApplyImportedConfig(manifest.Config, applied);

                var note = applied.ReplacedDataPath != null
                    ? $"已替换 {applied.Boxes} 个盒子；导入前的数据已改名保留"
                    : $"已导入 {applied.Boxes} 个盒子";
                if (result.FilesSkipped > 0) note += $" · {result.FilesSkipped} 个文件未能写入";
                // 第三个参数让"打开"按钮直接跳去旧数据目录（长路径留在文本里只会被截断）
                win.Complete(result, note, applied.ReplacedDataPath);
            }
            else
            {
                // 合并：给每个备份盒子安排一个不与现有盒子重名的落地名，再整目录搬进来
                var plan = ImportService.BuildMergePlan(manifest, Config.Boxes.Select(b => b.Name).ToList());
                var merged = await Task.Run(
                    () => ImportService.MergeStaging(stagingDir, dataRoot, plan), cts.Token);

                // 搬完了，staging 只剩空壳，清掉
                ImportService.TryDeleteStaging(stagingDir);
                staging = null;

                foreach (var box in merged.AddedBoxes)
                {
                    ClampIntoWorkArea(box);
                    Config.Boxes.Add(box);
                }
                Store.Save(Config);
                RebuildBoxWindows();

                var note = merged.RenamedForConflict > 0
                    ? $"已合并 {merged.AddedBoxes.Count} 个盒子（其中 {merged.RenamedForConflict} 个因重名自动改名），现有盒子未改动"
                    : $"已合并 {merged.AddedBoxes.Count} 个盒子，现有盒子未改动";
                if (result.FilesSkipped > 0) note += $" · {result.FilesSkipped} 个文件未能写入";
                win.Complete(result, note);

                LogError($"合并导入完成：新增 {merged.AddedBoxes.Count} 个盒子，" +
                         $"{merged.FoldersMoved} 个目录，重名改名 {merged.RenamedForConflict} 个");
            }
        }
        catch (OperationCanceledException)
        {
            ImportService.TryDeleteStaging(staging);
            win.Cancelled();
        }
        catch (Exception ex)
        {
            ImportService.TryDeleteStaging(staging);
            LogError($"导入备份失败：{ex}");
            win.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 备份里记的坐标是导出那台机器的。合并进来的盒子如果落在屏幕外，
    /// 用户会以为"导入成功但什么都没出现"，所以先挪回可见区域。
    /// </summary>
    private static void ClampIntoWorkArea(BoxConfig box)
    {
        var wa = SystemParameters.WorkArea;
        const double margin = 16;
        // 盒子默认宽度按 120 DIP 估，够判断"整块跑到屏幕外"即可
        double maxX = wa.Right - 120;
        double maxY = wa.Bottom - 80;

        if (double.IsNaN(box.X) || box.X < wa.Left || box.X > maxX)
            box.X = wa.Left + margin + 40;
        if (double.IsNaN(box.Y) || box.Y < wa.Top || box.Y > maxY)
            box.Y = wa.Top + margin + 40;
    }

    /// <summary>
    /// 导入后接管配置：盒子结构与外观用备份里的，数据根和自启状态保持本机值
    /// —— 备份里的 DataRoot 属于导出那台机器，照搬会让所有盒子指向不存在的目录。
    /// </summary>
    private void ApplyImportedConfig(AppConfig? imported, ImportApplyResult applied)
    {
        var next = imported ?? new AppConfig();
        next.Settings ??= new AppSettings();
        next.Boxes ??= new List<BoxConfig>();

        next.Settings.DataRoot = Store.DataRoot;
        next.Settings.Autorun = AutoStartService.IsEnabled();
        next.Settings.TopmostMigrated = true;

        // 备份里一个盒子都没有时不留空窗：和首启口径一致，给一个空盒
        if (next.Boxes.Count == 0) next.Boxes.Add(new BoxConfig());

        Config = next;
        Store.Save(Config);

        // 主题可能换了：SetMode 会广播到各窗口与应用级笔刷
        ThemeService.SetMode(Config.Settings.ThemeMode);
        _appTheme?.Update(ThemeService.ResolveColor(null), AppearanceTheme.DefaultOpacity, "新建盒子");
        AppIconService.SetLight(ThemeService.LightForIcons);

        // 托盘持有的是旧配置对象，必须重建，否则菜单里的外观与自启勾选还是旧的
        _tray?.Dispose();
        _tray = null;
        _tray = new TrayService(Config, () => Store.DataRoot,
            Config.Settings.Autorun || AutoStartService.IsEnabled());
        WireTray();

        // 备份里可能带一套不同的热键配置，重新注册一次
        ApplyHotkeys(reportToUser: false);

        RebuildBoxWindows();

        LogError($"导入完成：{applied.Boxes} 个盒子，数据根 {applied.DataRoot}，" +
                 $"旧数据 {(applied.ReplacedDataPath ?? "(无)")}");
    }

    /// <summary>当前所有盒子里的文件总数，用于导入确认窗里"会失去多少"的对比。</summary>
    private int CountCurrentItems()
    {
        int total = 0;
        foreach (var box in Config.Boxes)
        {
            try { total += Store.CountOf(box); }
            catch { }
        }
        return total;
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
        _hotkeys?.Dispose();
        _tray?.Dispose();
        AppIconService.Shutdown();
        _quitSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
