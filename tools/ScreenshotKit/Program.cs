using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;
using ToniaDeskBox.Windows;

// 只别名真正用到、且与 WPF 同名的绘图类型
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Graphics = System.Drawing.Graphics;
using GraphicsUnit = System.Drawing.GraphicsUnit;
using LinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenshotKit;

/// <summary>
/// README 截图工具：把**真实窗口**离屏渲染成 PNG，再合成到一张背景上。
///
/// 为什么不用屏幕截图：分层窗口 + 半透明底色在别人的桌面上会混进壁纸和桌面图标，
/// 既不可复现也不该把作者的桌面内容带进文档。这里直接对视觉树做 RenderTargetBitmap，
/// 拿到的就是控件真实渲染结果（同一套 Theme.* 笔刷、同一套模板），背景由合成阶段提供。
///
/// 用法：dotnet run --project tools/ScreenshotKit [输出目录]
/// 只写临时目录里的假数据，不碰用户真实的 boxes / config.json。
/// </summary>
internal static class Program
{
    private static string _outDir = "docs/screenshots";

    // 每个窗口错开渲染，避免互相"压住"导致面板被判定为重叠
    private static int _renderSeq;

    [STAThread]
    private static int Main(string[] args)
    {
        // 应用上下文必须先于任何窗口建好：面板的重叠检测会遍历 Application.Current.Windows，
        // 没有它就会 NullReferenceException（--perf / --smoke 也走这条路）
        var app = InitApplication();

        if (args.Length > 0 && args[0] == "--overlaptest")
            return OverlapTest();

        if (args.Length > 0 && args[0] == "--cachetest")
            return CacheTest();

        if (args.Length > 0 && args[0] == "--perf")
            return MeasureAnimation();

        if (args.Length > 0 && args[0] == "--smoke")
            return Smoke(probeHotkey: args.Length > 1 ? args[1] : null);

        if (args.Length > 0 && args[0] == "--closetest")
            return CloseTest();

        if (args.Length > 0 && args[0] == "--closeprof")
            return CloseProfile();

        if (args.Length > 0 && args[0] == "--overlapfast")
            return OverlapFastTest();

        if (args.Length > 0 && args[0] == "--placetest")
            return PlaceTest();

        if (args.Length > 0 && args[0] == "--layout")
            return LayoutProbe();

        if (args.Length > 0 && args[0] == "--restoretest")
            return RestoreProbe();

        if (args.Length > 0 && args[0] == "--restoreui")
            return RestoreUiProbe();

        if (args.Length > 0 && args[0] == "--tidyplace")
            return TidyPlaceProbe();

        if (args.Length > 0 && args[0] == "--iconplace")
            return IconPlaceProbe();

        if (args.Length > 0) _outDir = args[0];
        Directory.CreateDirectory(_outDir);
        AppearanceTheme.AttachApplication();

        using var sandbox = new Sandbox();
        var (config, store) = sandbox.Build();

        var boxes = RenderBoxes(config, store);
        var panel = RenderPanel(config, store);
        var settings = RenderSettings(config, store);
        var tidy = RenderTidy(config, store);
        var search = RenderSearchPanel(config, store);
        var boxSettings = RenderBoxSettings(config, store);
        var overlap = RenderOverlap(config, store);
        var (menu, submenu) = RenderMenu(config, store);
        var openFrames = RenderOpenFrames(config, store);
        RenderBubble(config, store, 0.5, 0.86, "up");    // 盒子靠下 → 面板在上（箭头朝下）
        RenderBubble(config, store, 0.5, 0.08, "down");  // 盒子贴顶 → 面板在下（箭头朝上）
        RenderBubble(config, store, 0.05, 0.5, "side");  // 盒子贴左 → 面板在右侧（箭头朝左）
        RenderBubble(config, store, 0.02, 0.94, "corner"); // 左下角 → 面板在右上方，箭头仍指着盒子
        var closeFrames = RenderCloseFrames(config, store);

        Compose(Path.Combine(_outDir, "boxes.png"), boxes, 1180, 560);
        Compose(Path.Combine(_outDir, "panel.png"), panel, 1080, 620);
        Compose(Path.Combine(_outDir, "settings.png"), settings, 900, 1120);
        Compose(Path.Combine(_outDir, "tidy.png"), tidy, 720, 640);
        Compose(Path.Combine(_outDir, "search.png"), search, 640, 660);
        Compose(Path.Combine(_outDir, "boxsettings.png"), boxSettings, 760, 1000);
        ComposeOverlap(Path.Combine(_outDir, "overlap.png"), overlap, 900, 560);
        Compose(Path.Combine(_outDir, "menu.png"), menu, 320, 240);
        if (submenu != null)
            Compose(Path.Combine(_outDir, "submenu.png"), new List<Shot> { new(submenu, 0, 0) }, 300, 240);
        Compose(Path.Combine(_outDir, "open-frames.png"), openFrames, 1900, 500);
        Compose(Path.Combine(_outDir, "close-frames.png"), closeFrames, 1400, 500);


        Console.WriteLine("截图已写入 " + Path.GetFullPath(_outDir));
        return 0;
    }

    // ---------- 冒烟检查 ----------

    /// <summary>
    /// 只验证两块纯 Win32 的接线（不建窗口、不碰用户数据）：
    /// 托盘 Shell_NotifyIcon 是否真的挂上了、热键是否真的注册成功。
    /// 这两处失败是静默的（图标不出现 / 热键不响应），单测覆盖不到，只能真机跑一次。
    /// </summary>
    /// <param name="probeHotkey">
    /// 额外探测一个组合键是否已被占用。程序自己正在跑时用它反证"热键真的注册上了"
    /// （同一个键不可能被注册两次）。
    /// </param>
    private static int Smoke(string? probeHotkey)
    {
        var ok = true;

        Console.WriteLine("冒烟检查：");
        try
        {
            var icoPath = Path.Combine(RepoRoot() ?? ".", "src", "ToniaDeskBox", "Assets", "app-light.ico");
            var bytes = File.Exists(icoPath) ? File.ReadAllBytes(icoPath) : null;
            using var tray = new TrayIcon("Tonia-DeskBox 冒烟");
            tray.SetIcon(bytes);
            Pump(120);   // 让 shell 处理一次 NIM_ADD
            if (!tray.IsAttached)
                throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) 返回失败");
            Console.WriteLine($"  PASS  托盘图标已挂上（图标字节 {(bytes?.Length ?? 0)}）");
        }
        catch (Exception ex)
        {
            ok = false;
            Console.WriteLine($"  FAIL  托盘图标：{ex.Message}");
        }

        try
        {
            using var hotkeys = new HotkeyService();
            // 用一个不常见的组合，避免和用户正在用的键冲突
            var error = hotkeys.Apply("Ctrl+Alt+F12", "Ctrl+Alt+F11");
            if (error.Length == 0)
            {
                Console.WriteLine($"  PASS  热键已注册：{hotkeys.ShowHotkey} / {hotkeys.HideHotkey}");
            }
            else
            {
                ok = false;
                Console.WriteLine($"  FAIL  热键：{error}");
            }
        }
        catch (Exception ex)
        {
            ok = false;
            Console.WriteLine($"  FAIL  热键：{ex.Message}");
        }

        if (probeHotkey != null)
        {
            using var probe = new HotkeyService();
            var error = probe.Apply(probeHotkey, HotkeyService.Disabled);
            if (error.Length == 0)
                Console.WriteLine($"  INFO  {probeHotkey} 当前是空闲的（没被任何程序占用）");
            else
                Console.WriteLine($"  INFO  {probeHotkey} 已被占用 → 说明有程序正握着它（{error}）");
        }

        Console.WriteLine(ok ? "冒烟检查通过" : "冒烟检查有失败项");
        return ok ? 0 : 1;
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ToniaDeskBox.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // ---------- 渲染 ----------

    private static List<Shot> RenderBoxes(AppConfig config, ConfigStore store)
    {
        var shots = new List<Shot>();

        // 1) 折叠态 + 前 3 个缩略图
        var collapsed = new BoxWindow(config.Boxes[0], config, store);
        shots.Add(new Shot(Render(collapsed), 0, 0));

        // 2) 展开态：内嵌图标网格
        var expanded = new BoxWindow(config.Boxes[1], config, store);
        shots.Add(new Shot(Render(expanded), 0, 0));

        // 3) 映射盒：标题带「映射」标记，内容来自已有文件夹
        var portal = new BoxWindow(config.Boxes[2], config, store);
        shots.Add(new Shot(Render(portal), 0, 0));

        return shots;
    }

    private static List<Shot> RenderPanel(AppConfig config, ConfigStore store)
    {
        var owner = new BoxWindow(config.Boxes[1], config, store);
        var ownerShot = Render(owner);

        var panel = new PanelWindow(owner, config.Boxes[1], config, store);
        panel.Show();
        Pump(600);   // 打开动效 220ms + 图标异步补齐
        var panelShot = Render(panel);

        return new List<Shot>
        {
            new(ownerShot, 0, 0),
            new(panelShot, 0, 0),
        };
    }

    /// <summary>
    /// 右键菜单 + 子菜单：验证 MenuItem 模板里的 PART_Popup 真的能挂出子项。
    /// 离屏环境下 Popup 会被系统夹回屏幕可用区域，所以这里不叠盒子，只看菜单本身。
    /// </summary>
    private static (List<Shot> Menu, Bitmap? Submenu) RenderMenu(AppConfig config, ConfigStore store)
    {
        var menu = new ContextMenu();
        AppearanceTheme.AttachMenu(menu, config.Boxes[0], 0.96);

        var mode = new MenuItem { Header = "展示方式" };
        mode.Items.Add(new MenuItem { Header = "缩略图 + 标题", IsCheckable = true, IsChecked = true });
        mode.Items.Add(new MenuItem { Header = "仅标题", IsCheckable = true });
        mode.Items.Add(new MenuItem { Header = "显示内容", IsCheckable = true });
        menu.Items.Add(mode);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "盒子设置…" });
        menu.Items.Add(new MenuItem { Header = "打开数据目录" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "删除盒子（内有 8 个文件）", IsEnabled = false });

        // 位置随便给一个值即可：Popup 会被系统夹回可用区域内，这里只看渲染结果
        menu.Placement = PlacementMode.Absolute;
        menu.HorizontalOffset = 40;
        menu.VerticalOffset = 40;
        menu.IsOpen = true;
        Pump(400);

        var menuShot = new Shot(Capture(menu, 208, 146), 0, 0);

        mode.IsSubmenuOpen = true;
        Pump(400);
        var sub = CaptureSubmenu(mode);

        menu.IsOpen = false;

        return (new List<Shot> { menuShot }, sub);
    }

    /// <summary>把已经排好版的元素截成位图（ContextMenu 不是 Window，不能走 Render）。</summary>
    private static Bitmap Capture(FrameworkElement el, double w, double h)
    {
        el.Measure(new Size(w, h));
        el.Arrange(new Rect(0, 0, w, h));
        el.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var decoded = new Bitmap(ms);
        return new Bitmap(decoded);
    }

    private static Bitmap? CaptureSubmenu(MenuItem mode)
    {
        if (mode.Template?.FindName("PART_Popup", mode) is not Popup popup) return null;
        if (popup.Child is not FrameworkElement child) return null;
        child.UpdateLayout();
        double w = child.ActualWidth, h = child.ActualHeight;
        if (w < 8 || h < 8) return null;
        return Capture(child, w, h);
    }

    /// <summary>面板压住盒子的场景：上层面板必须转成不透明，否则两层文字糊在一起。</summary>
    private static List<Shot> RenderOverlap(AppConfig config, ConfigStore store)
    {
        var box = new BoxWindow(config.Boxes[1], config, store);
        var boxShot = Render(box);

        // 让面板真的压在这个盒子上，重叠检测才会把它抬成不透明
        var panel = new PanelWindow(box, config.Boxes[1], config, store);
        panel.Left = box.Left + 40;
        panel.Top = box.Top + 30;
        // 不激活：面板失焦会自己关掉，离屏渲染时没有真实鼠标压在上面
        panel.ShowActivated = false;
        panel.Show();
        Pump(700);          // 200ms 的重叠检测 + 图标异步补齐
        var panelShot = Render(panel);

        return new List<Shot> { new(boxShot, 0, 0), new(panelShot, 0, 0) };
    }

    private static List<Shot> RenderBoxSettings(AppConfig config, ConfigStore store)
    {
        var dlg = new BoxSettingsWindow(config.Boxes[0], config, store);
        return new List<Shot> { new(Render(dlg), 0, 0) };
    }

    private static List<Shot> RenderSearchPanel(AppConfig config, ConfigStore store)
    {
        // 「显示内容」盒子标题栏上的放大镜开出来的搜索态面板：只有搜索栏 + 结果
        var owner = new BoxWindow(config.Boxes[1], config, store);
        // Owner 必须是已经显示过的窗口，否则 set_Owner 会抛
        _ = Render(owner);
        var panel = new PanelWindow(owner, config.Boxes[1], config, store, PanelOpenMode.Search);
        panel.Show();
        Pump(600);
        return new List<Shot> { new(Render(panel), 0, 0) };
    }

    private static List<Shot> RenderTidy(AppConfig config, ConfigStore store)
    {
        var desktop = FakeDesktop.Create();
        var groups = DesktopTidyService.Scan(desktop);
        // ensureBox 只在用户点「整理」后才会被调用，这里给一个空实现即可（仅截图）
        var dlg = new TidyPreviewWindow(groups, store, config, name => config.Boxes[0]);
        return new List<Shot> { new(Render(dlg), 0, 0) };
    }

    private static List<Shot> RenderSettings(AppConfig config, ConfigStore store)
    {
        var dlg = new GlobalSettingsWindow(config, store);
        return new List<Shot> { new(Render(dlg), 0, 0) };
    }

    /// <summary>把窗口渲染成带 alpha 的位图。Show 只是为了触发模板与布局，随后立刻挪出可见区。</summary>
    private static Bitmap Render(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        // 每个窗口错开 900px：都堆在同一个坐标会被"面板重叠检测"判定为互相压住，
        // 那样截出来的面板全是不透明的，和真实效果不符
        double offX = -6000 - (_renderSeq++ % 12) * 900;
        window.Left = offX;
        window.Top = -6000;
        window.Show();
        // Loaded 里的 ClampIntoScreen 会把它拉回屏内，这里再挪出去，只留一帧的闪现
        window.Left = offX;
        window.Top = -6000;
        Pump(500);

        var bmp = Snapshot(window);
        window.Hide();
        return bmp;
    }

    /// <summary>
    private static Application InitApplication()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ToniaDeskBox;component/Themes/SharedStyles.xaml"),
        });
        ThemeService.Initialize(ThemeMode.Dark);
        return app;
    }

    /// <summary>
    /// 验证 BitmapCache 在真实合成路径下是否会让图标失效。
    /// 上一轮用 RenderTargetBitmap 判成"图标变白块"，但它对带 CacheMode 的元素本就渲染不出内容，
    /// 结论不可信。这里改用 PrintWindow（走 DWM 的窗口位图）再判一次。
    /// </summary>
    /// <summary>
    /// 复现"快速切换盒子时面板透明消失"：
    /// A 开始收起（150ms 缩回）时立刻打开 B，两者必然重叠。
    /// B 不能因为 A 的残影被判成"被压住"而转实心。
    /// </summary>
    private static int OverlapTest()
    {
        using var sandbox = new Sandbox();
        var (config, store) = sandbox.Build();
        var boxes = config.Boxes.Where(b => b.DisplayMode == BoxDisplayMode.Content).ToList();
        if (boxes.Count < 2)
        {
            var extra = new BoxConfig { Name = "第二个", DisplayMode = BoxDisplayMode.Content, W = 344 };
            config.Boxes.Add(extra);
            boxes = new List<BoxConfig> { boxes[0], extra };
        }

        var hostA = new BoxWindow(boxes[0], config, store);
        var hostB = new BoxWindow(boxes[1], config, store);
        _ = Render(hostA);
        _ = Render(hostB);

        var a = new PanelWindow(hostA, boxes[0], config, store) { ShowActivated = false };
        a.Show();
        Pump(420);                                  // 等 A 完全打开
        Console.WriteLine($"  A 打开后 opaque={a.IsOpaqueForOverlap}（期望 False）");

        a.ClosePanel();                             // A 开始 150ms 收缩
        var b = new PanelWindow(hostB, boxes[1], config, store) { ShowActivated = false };
        b.Show();                                   // B 紧接着打开，必然与收缩中的 A 重叠
        Pump(700);

        bool opaque = b.IsOpaqueForOverlap;
        Console.WriteLine($"  B 与收缩中的 A 重叠时 opaque={opaque}（期望 False）");

        b.ClosePanel();
        Pump(400);
        Console.WriteLine(opaque ? "FAIL：透明被残影面板吃掉了" : "PASS：新面板保持半透明");
        return opaque ? 1 : 0;
    }

    /// <summary>
    /// 收起流程的回归测试：ClosePanel() 必须「立刻返回、动画照播、播完才 Hide、再延后销毁」。
    ///
    /// 之前的实现是 ClosePanel → Close() → OnClosing 里 e.Cancel → 播动画 → 定时器再 Close()，
    /// 关闭请求要先穿过一遍 WPF 的关闭流程（去激活、重新激活 Owner）才开始动，
    /// 用户感受就是"关闭比打开迟半拍"。这里断言三件事：
    /// 1) ClosePanel 的同步耗时极小（没把关闭流程算进来）；
    /// 2) 140ms 动画期间窗口仍在屏上（没有一调就消失）；
    /// 3) 之后确实销毁（不会只 Hide 而永远不 Close，那会每开一次漏一个 HwndSource）。
    /// </summary>
    private static int CloseTest()
    {
        using var sandbox = new Sandbox();
        var (config, store) = sandbox.Build();
        var target = config.Boxes.First(b => b.DisplayMode == BoxDisplayMode.Content);

        var host = new BoxWindow(target, config, store);
        _ = Render(host);

        var sw = new System.Diagnostics.Stopwatch();
        for (int i = 0; i < 3; i++)
        {
            var panel = new PanelWindow(host, target, config, store) { ShowActivated = false };
            bool closed = false;
            panel.Closed += (_, _) => closed = true;

            // 帧间距序列。打开与关闭各取一段，对照着看才知道尖峰是不是关闭独有的
            var gaps = new List<double>();
            double lastTick = 0;
            EventHandler onFrame = (_, _) =>
            {
                double t = sw.Elapsed.TotalMilliseconds;
                gaps.Add(t - lastTick);
                lastTick = t;
            };

            sw.Restart();
            CompositionTarget.Rendering += onFrame;
            panel.Show();
            Pump(300);                                  // 展开动画 200ms 走完
            int openFrames = gaps.Count;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            panel.ClosePanel();
            double callMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                            / System.Diagnostics.Stopwatch.Frequency;

            // 60ms 处：动画（140ms）一定还在跑。Pump 自己是 Background 优先级的定时器，
            // 渲染帧密集时会偏晚（实测 110ms 的请求要 134ms 才返回），所以这里留足余量
            Pump(60);
            bool midAir = panel.IsVisible && !closed;
            Pump(480);                                  // 等 Completed → Hide → 延后的 Close
            CompositionTarget.Rendering -= onFrame;

            // 只看动画那一段：Pump 的尾巴是静止帧，间隔没有意义
            var open = gaps.Take(Math.Min(openFrames, 16)).ToList();
            var close = gaps.Skip(openFrames).Take(16).ToList();
            bool destroyed = closed;
            bool stillListed = Application.Current.Windows.Cast<Window>().Contains(panel);

            Console.WriteLine($"  第 {i + 1} 轮：ClosePanel 同步耗时 {callMs:0.00}ms、60ms 处仍在屏上={midAir}、已销毁={destroyed}、仍在窗口列表={stillListed}");
            Console.WriteLine($"    打开 {open.Count} 帧 最大间隔 {open.DefaultIfEmpty(0).Max():0.0}ms  [{Fmt(open)}]");
            Console.WriteLine($"    关闭 {close.Count} 帧 最大间隔 {close.DefaultIfEmpty(0).Max():0.0}ms  [{Fmt(close)}]");
            if (callMs > 40 || !midAir || !destroyed || stillListed) return 1;
        }

        Console.WriteLine("PASS：开关 3 轮，收起即动、播完销毁、无残留窗口");
        return 0;
    }

    /// <summary>
    /// 收起流程的细粒度剖析：把"动画掉帧"和"Hide/Close 的同步开销"分开量化。
    ///
    /// 逐帧采样透明度与缩放，能看出最后一帧是不是还停在半透明上被 Hide 硬切 ——
    /// 那种情况用户看到的就是"淡出没好就没了"，跟 Win11 原生面板的顺滑差得最远。
    /// </summary>
    private static int CloseProfile()
    {
        using var sandbox = new Sandbox();
        var (config, store) = sandbox.Build();
        var target = config.Boxes.First(b => b.DisplayMode == BoxDisplayMode.Content);

        var host = new BoxWindow(target, config, store);
        _ = Render(host);

        var panel = new PanelWindow(host, target, config, store) { ShowActivated = false };
        bool closed = false;
        panel.Closed += (_, _) => closed = true;

        var sw = new System.Diagnostics.Stopwatch();
        var frames = new List<(double T, double Opacity, double Scale)>();

        EventHandler onFrame = (_, _) =>
            frames.Add((sw.Elapsed.TotalMilliseconds, panel.ProfOpacity, panel.ProfScale));

        sw.Restart();
        CompositionTarget.Rendering += onFrame;
        panel.Show();
        Pump(340);                       // 打开动画 200ms + 余量

        int openFrames = frames.Count;
        var closeCalledAt = sw.Elapsed.TotalMilliseconds;
        var closeCalledWall = DateTime.UtcNow;
        panel.ClosePanel();
        Pump(700);
        CompositionTarget.Rendering -= onFrame;

        var close = frames.Skip(openFrames).ToList();
        var gaps = new List<double>();
        for (int i = 1; i < close.Count; i++) gaps.Add(close[i].T - close[i - 1].T);

        double animMs = panel.ProfFinalizeAt == default
            ? -1
            : (panel.ProfFinalizeAt - closeCalledWall).TotalMilliseconds;

        Console.WriteLine($"  关闭动画：{close.Count} 帧，前 10 帧间隔 [{Fmt(gaps.Take(10).ToList())}]");
        Console.WriteLine($"  前 3 帧累计 {(close.Count > 3 ? close[3].T - close[0].T : 0):0}ms、前 6 帧累计 {(close.Count > 6 ? close[6].T - close[0].T : 0):0}ms");
        Console.WriteLine($"  帧间隔 [{Fmt(gaps)}]  最大 {gaps.DefaultIfEmpty(0).Max():0.0}ms");
        Console.WriteLine($"  逐帧透明度 [{string.Join(" ", close.Take(12).Select(f => f.Opacity.ToString("0.00")))}]");
        Console.WriteLine($"  逐帧缩放   [{string.Join(" ", close.Take(12).Select(f => f.Scale.ToString("0.000")))}]");
        Console.WriteLine($"  动画实际时长 {animMs:0}ms（设定 150ms）");
        Console.WriteLine($"  Hide 时透明度 = {panel.ProfOpacityAtHide:0.000}（0 才是淡完了）");
        Console.WriteLine($"  Hide() 同步耗时 {panel.ProfHideMs:0.00}ms、Close() 同步耗时 {panel.ProfCloseMs:0.00}ms");
        Console.WriteLine($"  已隐藏={!panel.IsVisible}、已销毁={closed}");
        return 0;
    }

    private static string Fmt(List<double> gaps) =>
        string.Join(" ", gaps.Select(g => g.ToString("0.0")));

    /// <summary>
    /// 只读探测：能不能拿到桌面图标的位置。这是「一键恢复」的地基，
    /// 跨进程读 explorer 的 ListView，失败原因必须能分清是"没找到桌面"还是"没权限"。
    /// </summary>
    private static int LayoutProbe()
    {
        if (!DesktopLayoutService.TryCapture(out var layout, out string error))
        {
            Console.WriteLine($"FAIL：{error}");
            return 1;
        }

        Console.WriteLine($"自动排列={layout.AutoArrange}、图标 {layout.Icons.Count} 个、屏幕矩形 {layout.ScreenBounds.Count} 个");
        Console.WriteLine($"  实际状态：{DesktopLayoutService.DescribeState()}");
        foreach (var icon in layout.Icons.Take(12))
            Console.WriteLine($"  ({icon.X,5},{icon.Y,5})  {icon.Name}   [{(icon.Path.Length > 0 ? icon.Path : "无路径")}]");
        if (layout.Icons.Count > 12)
            Console.WriteLine($"  …等 {layout.Icons.Count} 个");

        Console.WriteLine("  屏幕矩形（DIP，盒子避让用）：");
        foreach (var b in layout.ScreenBounds.Take(6))
            Console.WriteLine($"    「{b.Name}」({b.X:0},{b.Y:0}) {b.W:0}×{b.H:0}");
        if (layout.ScreenBounds.Count > 6)
            Console.WriteLine($"    …等 {layout.ScreenBounds.Count} 个");

        bool boundsOk = layout.ScreenBounds.Count == layout.Icons.Count;
        Console.WriteLine(boundsOk
            ? "PASS：桌面图标位置与屏幕矩形都读到了"
            : $"WARN：{layout.Icons.Count - layout.ScreenBounds.Count} 个图标没拿到屏幕矩形");
        return boundsOk ? 0 : 1;
    }

    /// <summary>
    /// 一键恢复窗的渲染检查：XAML 里引用的样式/主题笔刷一旦找不到，运行时才会炸，
    /// 所以这里真的把窗口建出来截一张，顺带确认三种来源的标签都在。
    /// </summary>
    /// <summary>
    /// 实机验证「整理时新增盒子找空地」。
    ///
    /// 单测只能断言纯几何；这里用**真实窗口尺寸**跑一遍同一条规则，用来确认
    /// 自适应盒子的估算值（210×96）不会把新盒子摆到别人身上，以及估算偏小时
    /// 按实测尺寸重排之后仍然互不重叠。
    /// </summary>
    /// <summary>
    /// 实机验证「新盒子避开桌面图标」：把真实图标矩形当障碍，排 4 个盒子，
    /// 断言它们不压住任何一个图标。桌面图标多到放不下时会明确说明压住了几个。
    /// </summary>
    private static int IconPlaceProbe()
    {
        if (!DesktopLayoutService.TryCapture(out var layout, out string error))
        {
            Console.WriteLine($"SKIP：读不到桌面图标（{error}）");
            return 0;
        }

        if (layout.ScreenBounds.Count == 0)
        {
            Console.WriteLine("SKIP：桌面上没有图标，无从验证");
            return 0;
        }

        var wa = SystemParameters.WorkArea;
        var work = new BoxArea(wa.X, wa.Y, wa.Width, wa.Height);
        var icons = layout.ScreenBounds
            .Where(b => b.W > 0 && b.H > 0)
            .Select(b => new BoxArea(b.X, b.Y, b.W, b.H))
            .ToList();

        double iconArea = icons.Sum(i => i.W * i.H);
        Console.WriteLine($"  工作区 ({work.X:0},{work.Y:0}) {work.W:0}×{work.H:0}、桌面图标 {icons.Count} 个"
            + $"（占屏 {iconArea / (work.W * work.H) * 100:0.0}%）");

        var placed = BoxPlacementService.Arrange(work, icons,
            new (double, double)[] { (210, 96), (210, 96), (210, 96), (210, 96) });

        int fail = 0;
        foreach (var area in placed)
        {
            int hits = icons.Count(i => area.Overlaps(i, BoxPlacementService.Gap));
            if (hits > 0) fail++;
            Console.WriteLine($"  {(hits == 0 ? "PASS" : "FAIL")} 盒子 {area}"
                + (hits > 0 ? $" ← 压住 {hits} 个图标" : ""));
        }

        Console.WriteLine(fail == 0
            ? "PASS：4 个新盒子全部避开了桌面图标"
            : $"{fail} 个盒子仍压住图标（桌面放不下时会退化为重叠最小的位置）");
        return fail == 0 ? 0 : 1;
    }

    private static int TidyPlaceProbe()
    {
        using var sandbox = new Sandbox();
        var (config, store) = sandbox.Build();

        var wa = SystemParameters.WorkArea;
        var work = new BoxArea(wa.X, wa.Y, wa.Width, wa.Height);
        Console.WriteLine($"  工作区 ({work.X:0},{work.Y:0}) {work.W:0}×{work.H:0}");

        var obstacles = new List<BoxArea>();
        var existing = new List<BoxWindow>();
        foreach (var box in config.Boxes.ToList())
        {
            var w = new BoxWindow(box, config, store) { ShowActivated = false };
            w.Show();
            Pump(140);
            existing.Add(w);
            obstacles.Add(new BoxArea(w.Left, w.Top, w.ActualWidth, w.ActualHeight));
            Console.WriteLine($"  已有盒子「{box.Name}」({w.Left:0},{w.Top:0}) {w.ActualWidth:0}×{w.ActualHeight:0}");
        }
        int existingCount = obstacles.Count;

        var names = new[] { "文档", "图片", "视频", "压缩包" };
        var placed = new List<BoxWindow>();
        int fail = 0;
        double maxDelta = 0;

        foreach (var name in names)
        {
            var box = new BoxConfig { Name = name };
            var (x, y) = BoxPlacementService.FindFreeSpot(work,
                BoxPlacementService.DefaultWidth, BoxPlacementService.DefaultHeight, obstacles);
            box.X = x;
            box.Y = y;

            var estimate = new BoxArea(x, y, BoxPlacementService.DefaultWidth, BoxPlacementService.DefaultHeight);
            obstacles.Add(estimate);       // 与 App 的 PlaceTidyBox 一致：先按估算占位

            Console.WriteLine($"    估算位置 ({x:0},{y:0})");
            var win = new BoxWindow(box, config, store) { ShowActivated = false };
            win.Show();
            Pump(140);
            placed.Add(win);

            maxDelta = Math.Max(maxDelta, Math.Max(
                win.ActualWidth - BoxPlacementService.DefaultWidth,
                win.ActualHeight - BoxPlacementService.DefaultHeight));

            obstacles.Remove(estimate);
            var actual = new BoxArea(win.Left, win.Top, win.ActualWidth, win.ActualHeight);
            if (obstacles.Any(o => actual.Overlaps(o, BoxPlacementService.Gap)))
            {

                // 与 App 的 RefineTidyBoxPlacement 一致：实测压到别人就按真实尺寸重排
                var (nx, ny) = BoxPlacementService.FindFreeSpot(
                    work, win.ActualWidth, win.ActualHeight, obstacles, (win.Left, win.Top));
                win.Left = nx;
                win.Top = ny;
                Pump(80);
                actual = new BoxArea(win.Left, win.Top, win.ActualWidth, win.ActualHeight);
                Console.WriteLine($"    → 估算偏小，按实测 {win.ActualWidth:0}×{win.ActualHeight:0} 重排到 ({nx:0},{ny:0})");
            }

            bool clashExisting = obstacles.Take(existingCount).Any(o => actual.Overlaps(o, BoxPlacementService.Gap));
            bool clashNew = placed.Take(placed.Count - 1).Any(p =>
                actual.Overlaps(new BoxArea(p.Left, p.Top, p.ActualWidth, p.ActualHeight), BoxPlacementService.Gap));
            bool inside = actual.X >= work.X - 0.5 && actual.Y >= work.Y - 0.5
                && actual.Right <= work.Right + 0.5 && actual.Bottom <= work.Bottom + 0.5;

            if (clashExisting || clashNew || !inside) fail++;
            Console.WriteLine($"  {(clashExisting || clashNew || !inside ? "FAIL" : "PASS")} 新建「{name}」"
                + $"({win.Left:0},{win.Top:0}) 实测 {win.ActualWidth:0}×{win.ActualHeight:0}"
                + (clashExisting ? " ← 压住已有盒子" : clashNew ? " ← 压住本次新建" : !inside ? " ← 越出工作区" : ""));

            obstacles.Add(actual);
        }

        foreach (var w in placed) w.Close();
        foreach (var w in existing) w.Close();
        Pump(160);

        Console.WriteLine($"  实测比估算最多大 {maxDelta:0} DIP");
        Console.WriteLine(fail == 0
            ? "PASS：新盒子互不重叠、不压已有盒子、都在工作区内"
            : $"FAIL：{fail} 个盒子落位不对");
        return fail == 0 ? 0 : 1;
    }

    private static int RestoreUiProbe()
    {
        var config = new AppConfig();
        config.Boxes.Clear();

        var root = Path.Combine(Path.GetTempPath(), "tdb-restoreui");
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        var desktop = Path.Combine(root, "desktop");
        var box = Path.Combine(root, "box");
        var backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(box);
        Directory.CreateDirectory(backup);

        File.WriteAllText(Path.Combine(box, "报告.pdf"), "x");
        File.WriteAllText(Path.Combine(backup, "截图.png"), "y");
        // 「丢了.txt」哪里都没有 → 第三态

        var state = new TidyState
        {
            CreatedAt = DateTime.Now.AddHours(-2),
            LayoutCaptured = true,
            AutoArrange = false,
            Icons = new List<DesktopIconPos>
            {
                new() { Name = "报告.pdf", Path = Path.Combine(desktop, "报告.pdf"), X = 26, Y = 173 },
            },
            BackupDir = backup,
            BackupFiles = 1,
            BackupBytes = 1024 * 300,
            Items = new List<TidyRestoreItem>
            {
                RestoreItem(desktop, box, "报告.pdf"),
                RestoreItem(desktop, box, "截图.png"),
                RestoreItem(desktop, box, "丢了.txt"),
            },
        };

        var plan = TidyRestoreService.BuildPlan(state, desktop);
        var window = new TidyRestoreWindow(state, plan, config)
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 100,
            Top = 100,
        };
        window.Show();
        Pump(420);

        var shot = Snapshot(window);
        Directory.CreateDirectory("tmp-shots");
        shot.Save("tmp-shots/restore.png", ImageFormat.Png);
        window.Close();
        Pump(120);

        Console.WriteLine($"restore.png {shot.Width}×{shot.Height}");
        Console.WriteLine(plan.MissingCount == 1 && plan.FromBackupCount == 1
            ? "PASS：恢复窗渲染正常，三种来源都标识出来了"
            : $"FAIL：计划分类不对（缺失 {plan.MissingCount}、备份 {plan.FromBackupCount}）");
        return plan.MissingCount == 1 && plan.FromBackupCount == 1 ? 0 : 1;
    }

    private static TidyRestoreItem RestoreItem(string desktop, string box, string name) => new()
    {
        Name = name,
        OriginalPath = Path.Combine(desktop, name),
        BoxPath = Path.Combine(box, name),
        BoxName = "文档",
    };

    /// <summary>
    /// 真机验证「写位置」这条路是通的：把一个桌面图标挪开 60px、读回确认、再还原。
    ///
    /// 为什么必须实测：`LVM_SETITEMPOSITION` 在「自动排列」开着时会被 Explorer 立刻重排回去，
    /// 而 `SetWindowLongPtr` 改样式、跨进程 SendMessage 这些环节任何一处静默失效，
    /// 表现都是"恢复完了图标没动"，从代码上看不出来。用完一定会摆回原位（finally 里兜底）。
    /// </summary>
    private static int RestoreProbe()
    {
        if (!DesktopLayoutService.TryCapture(out var before, out string error))
        {
            Console.WriteLine($"FAIL：{error}");
            return 1;
        }
        if (before.Icons.Count == 0)
        {
            Console.WriteLine("SKIP：桌面没有图标，无法验证");
            return 0;
        }

        // 系统图标（回收站/此电脑…）的位置由 shell 单独管理，LVM_SETITEMPOSITION 对它们可能无效，
        // 所以优先挑一个普通快捷方式来验证
        var systemNames = new[] { "回收站", "此电脑", "网络", "控制面板", "用户文件夹", "Recycle Bin" };
        var target = before.Icons.FirstOrDefault(i => !systemNames.Contains(i.Name))
                     ?? before.Icons[0];
        Console.WriteLine($"  目标图标：{target.Name} 原位置 ({target.X},{target.Y})、自动排列={before.AutoArrange}");

        bool moveOk = false;
        try
        {
            var moved = new DesktopLayout
            {
                AutoArrange = before.AutoArrange,
                Icons = new List<DesktopIconPos>
                {
                    new()
                    {
                        Name = target.Name,
                        Path = target.Path,
                        X = target.X + 60,
                        Y = target.Y + 60,
                    },
                },
            };

            if (!DesktopLayoutService.TryRestore(moved, out string setError, out _, out _))
            {
                // 位置设不了有两种原因：实现错了，或者系统根本不允许程序摆图标。
                // 后者是平台限制，不该让脚本红着 —— 说清楚就行。
                if (setError.Contains("不允许程序设置"))
                {
                    Console.WriteLine($"  SKIP：{setError}");
                    return 0;
                }

                Console.WriteLine($"FAIL：设置位置失败：{setError}");
                return 1;
            }

            // 立刻读一次（不抽消息循环）：能区分"消息没生效"和"生效了但被 shell 立刻同步回去"
            if (DesktopLayoutService.TryCapture(out var immediate, out _))
            {
                var now0 = immediate.Icons.FirstOrDefault(i => i.Name == target.Name);
                Console.WriteLine($"  设置后立刻读：({now0?.X},{now0?.Y})");
            }

            // 每 80ms 读一次，看位置是被"立刻回滚"还是"过一会儿被重排"
            for (int step = 1; step <= 6; step++)
            {
                Pump(80);
                if (DesktopLayoutService.TryCapture(out var sample, out _))
                {
                    var s = sample.Icons.FirstOrDefault(i => i.Name == target.Name);
                    Console.WriteLine($"  +{step * 80}ms：({s?.X},{s?.Y})");
                }
            }

            if (DesktopLayoutService.TryCapture(out var after, out _))
            {
                var now = after.Icons.FirstOrDefault(i => i.Name == target.Name);
                Console.WriteLine($"  移动后：({now?.X},{now?.Y})");
                moveOk = now != null
                    && Math.Abs(now.X - (target.X + 60)) <= 4
                    && Math.Abs(now.Y - (target.Y + 60)) <= 4;
                Console.WriteLine($"  移动生效={moveOk}");
            }
        }
        finally
        {
            // 不管成功失败都摆回原位
            var back = new DesktopLayout
            {
                AutoArrange = before.AutoArrange,
                Icons = new List<DesktopIconPos> { target },
            };
            DesktopLayoutService.TryRestore(back, out _, out _, out _);
            Pump(320);
        }

        DesktopLayoutService.TryCapture(out var final, out _);
        var restored = final.Icons.FirstOrDefault(i => i.Name == target.Name);
        bool backOk = restored != null
            && Math.Abs(restored.X - target.X) <= 4
            && Math.Abs(restored.Y - target.Y) <= 4;
        Console.WriteLine($"  还原后：({restored?.X},{restored?.Y}) 还原生效={backOk}");

        Console.WriteLine(moveOk && backOk ? "PASS：图标位置可写、可精确还原" : "FAIL：位置没有真正改变");
        return moveOk && backOk ? 0 : 1;
    }

    /// <summary>
    /// 气泡避让 + 指向关系的回归测试。
    ///
    /// 规则是「面板朝离开边缘的方向让开」：靠左 → 面板放右，靠右 → 放左，
    /// 靠上 → 放下，靠下 → 放上；正中保持「优先上方」的老习惯。
    /// 但避让只决定**纵向**，横向必须覆盖盒子中心 —— 否则盒子在角落时面板被推到横向
    /// 错开很远的位置，箭头只能夹在边角上指一片空白（2026-11-24 反馈的那个问题）。
    /// 所以这里除了方位，还断言「箭头锚点落在盒子中心上」。
    /// </summary>
    private static int PlaceTest()
    {
        using var sandbox = new Sandbox();
        var (config, store) = sandbox.Build();
        var target = config.Boxes.First(b => b.DisplayMode == BoxDisplayMode.Content);

        var wa = SystemParameters.WorkArea;
        Console.WriteLine($"  工作区 {wa.Width:0}×{wa.Height:0} @({wa.X:0},{wa.Y:0})");

        // Fx/Fy：盒子在工作区里的相对位置（0 = 贴左/上，1 = 贴右/下）
        // WantY：D = 面板在下方，U = 上方，C = 纵向居中（侧放）
        // WantX：只在侧放时有意义（R/L）
        var cases = new (string Name, double Fx, double Fy, string WantX, string WantY)[]
        {
            ("左上", 0.0, 0.0, "C", "D"),
            ("上中", 0.5, 0.0, "C", "D"),
            ("右上", 1.0, 0.0, "C", "D"),
            ("左中", 0.0, 0.5, "R", "C"),
            ("正中", 0.5, 0.5, "C", "U"),
            ("右中", 1.0, 0.5, "L", "C"),
            ("左下", 0.0, 1.0, "C", "U"),
            ("下中", 0.5, 1.0, "C", "U"),
            ("右下", 1.0, 1.0, "C", "U"),
        };

        const double M = 18;
        // 只验「盒子中心落在面板可见范围内、且离边不小于箭头半宽」。
        // 更严的"避开圆角"由 UpdatePlacement 的 pad 保证，这里不去猜盒子的 CornerSize
        const double Pad = 12;
        int fail = 0;
        foreach (var c in cases)
        {
            var host = new BoxWindow(target, config, store)
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            host.Show();
            Pump(120);
            // 必须在 Show 之后摆位：Loaded 里的 ClampIntoScreen 会把盒子拉回工作区
            host.Left = wa.X + c.Fx * Math.Max(0, wa.Width - host.ActualWidth);
            host.Top = wa.Y + c.Fy * Math.Max(0, wa.Height - host.ActualHeight);
            Pump(100);

            var panel = new PanelWindow(host, target, config, store) { ShowActivated = false };
            panel.Show();
            Pump(420);

            // 用「可见边」比对：Left/Top 含 18px 投影留白
            double pL = panel.Left + M, pT = panel.Top + M;
            double pR = panel.Left + panel.ActualWidth - M, pB = panel.Top + panel.ActualHeight - M;
            double bL = host.Left, bT = host.Top;
            double bR = bL + host.ActualWidth, bB = bT + host.ActualHeight;
            double bCx = (bL + bR) / 2, bCy = (bT + bB) / 2;

            string gotX = pL >= bR - 1 ? "R" : pR <= bL + 1 ? "L" : "C";
            string gotY = pT >= bB - 1 ? "D" : pB <= bT + 1 ? "U" : "C";

            // 指向关系：上下放时盒子中心必须落在面板横向范围内（留出箭头半宽），
            // 侧放时反过来看纵向。这一条才是「箭头明确指着盒子」的实质。
            bool aim = gotY != "C"
                ? pL + Pad <= bCx && bCx <= pR - Pad
                : pT + Pad <= bCy && bCy <= pB - Pad;

            bool ok = gotX == c.WantX && gotY == c.WantY && aim;
            if (!ok) fail++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {c.Name}：盒子=({bL:0},{bT:0} {host.ActualWidth:0}×{host.ActualHeight:0}) "
                + $"面板=({panel.Left:0},{panel.Top:0} {panel.ActualWidth:0}×{panel.ActualHeight:0}) → {gotX}{gotY}、指向={(aim ? "准" : "偏")}（期望 {c.WantX}{c.WantY}、指向准）");

            panel.ClosePanel();
            host.Close();
            Pump(260);
        }

        Console.WriteLine(fail == 0
            ? "PASS：九个方位的避让全部正确"
            : $"FAIL：{fail} 个方位不对");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 「重叠时反应慢半拍」的回归测试。
    ///
    /// 旧逻辑要等三件事凑齐才把面板抬成不透明：展开动画 200ms 收尾 → 定时器启动 →
    /// 再等一整个 interval（200ms）。合起来最快也要 400ms 以上，用户看到的就是
    /// 「先透明地铺开，过一会儿才变实心」。
    /// 现在首帧之前（SourceInitialized 里位置已定）就判一次，Show() 返回时就该是实心的。
    /// </summary>
    private static int OverlapFastTest()
    {
        using var sandbox = new Sandbox();
        var (config, store) = sandbox.Build();
        var boxes = config.Boxes.Where(b => b.DisplayMode == BoxDisplayMode.Content).ToList();
        while (boxes.Count < 2)
        {
            var extra = new BoxConfig { Name = "第二个", DisplayMode = BoxDisplayMode.Content, W = 344 };
            config.Boxes.Add(extra);
            boxes.Add(extra);
        }

        // 两个盒子贴近摆放：面板会各自竖向开在盒子上方，必然大幅重叠
        var hostA = new BoxWindow(boxes[0], config, store)
        { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        hostA.Show();
        var hostB = new BoxWindow(boxes[1], config, store)
        { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        hostB.Show();
        // Loaded 里的 ClampIntoScreen 会改位置，摆完再定一次
        hostA.Left = 160; hostA.Top = 380;
        hostB.Left = 210; hostB.Top = 400;
        Pump(200);

        var a = new PanelWindow(hostA, boxes[0], config, store) { ShowActivated = false };
        a.Show();
        Pump(420);
        bool aAlone = a.IsOpaqueForOverlap;

        var b = new PanelWindow(hostB, boxes[1], config, store) { ShowActivated = false };
        b.Show();
        // Show() 会同步触发 SourceInitialized，那里已经 force 判过一次 —— 这里读到的
        // 就是首帧的取值，不需要再抽消息循环
        bool bFirstFrame = b.IsOpaqueForOverlap;
        Pump(300);

        Console.WriteLine($"  A 面板 ({a.Left:0},{a.Top:0} {a.ActualWidth:0}×{a.ActualHeight:0}) 单独时 opaque={aAlone}（期望 False）");
        Console.WriteLine($"  B 面板 ({b.Left:0},{b.Top:0} {b.ActualWidth:0}×{b.ActualHeight:0}) 压住 A");
        Console.WriteLine($"    Show() 返回即 opaque={bFirstFrame}（期望 True —— 这就是「不慢半拍」）");
        Console.WriteLine($"    展开完仍 opaque={b.IsOpaqueForOverlap}");

        b.ClosePanel();
        a.ClosePanel();
        Pump(500);

        bool ok = !aAlone && bFirstFrame && b.IsOpaqueForOverlap;
        Console.WriteLine(ok
            ? "PASS：重叠时首帧即实心，不用等动画收尾 + 定时器"
            : "FAIL：仍然是「先透明后变实」");
        return ok ? 0 : 1;
    }

    private static int CacheTest()
    {
        using var sandbox = new Sandbox();
        var (config, store) = sandbox.Build();
        var target = config.Boxes.First(b => b.DisplayMode == BoxDisplayMode.Content);

        var box = new BoxWindow(target, config, store);
        _ = Render(box);

        var panel = new PanelWindow(box, target, config, store) { ShowActivated = false };
        panel.Show();

        // 抓三帧：动画中段 / 后段 / 结束。若缓存把 opacity 固化了，前两帧会跟结束帧一样亮，
        // 甚至内容全空 —— 这一步就是专门验这个的。
        foreach (var (delay, name) in new[] { (55, "mid"), (90, "late"), (400, "done") })
        {
            Pump(delay);
            var shot = CaptureWindow(panel);
            shot.Save(Path.Combine(_outDir, $"cachetest-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);

            double sum = 0;
            for (int y = 0; y < shot.Height; y += 4)
                for (int x = 0; x < shot.Width; x += 4)
                {
                    var c = shot.GetPixel(x, y);
                    sum += (c.R + c.G + c.B) / 3.0;
                }
            double samples = (shot.Width / 4 + 1) * (shot.Height / 4 + 1);
            Console.WriteLine($"  cachetest-{name}.png  平均亮度={sum / samples:0.0}");
        }

        panel.Close();
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>用 PrintWindow 抓窗口位图：走 DWM 合成结果，能反映真实的 CacheMode 效果。</summary>
    private static Bitmap CaptureWindow(Window w)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        GetWindowRect(hwnd, out var r);
        int width = Math.Max(1, r.Right - r.Left);
        int height = Math.Max(1, r.Bottom - r.Top);
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr hdc = g.GetHdc();
        PrintWindow(hwnd, hdc, 2);   // PW_RENDERFULLCONTENT：分层窗口必须带这个
        g.ReleaseHdc(hdc);
        return bmp;
    }

    /// <summary>
    /// 量动画的真实帧间隔。静态截图能证明"在缩放"，证明不了"流不流畅"——
    /// 分层窗口 + 高斯模糊的掉帧只有在帧节奏上才看得见（60fps ≈ 16.7ms）。
    /// </summary>
    private static int MeasureAnimation()
    {
        using var sandbox = new Sandbox();
        var (config, store) = sandbox.Build();
        var target = config.Boxes.First(b => b.DisplayMode == BoxDisplayMode.Content);

        var box = new BoxWindow(target, config, store);
        _ = Render(box);

        var panel = new PanelWindow(box, target, config, store);
        panel.ShowActivated = false;

        // WPF 渲染层级：Tier 0/1 = 软件渲染，所有动画都是 CPU 画，调参数救不回来
        int tier = RenderCapability.Tier >> 16;
        Console.WriteLine($"  RenderCapability.Tier = {tier}（2=硬件加速，0/1=软件渲染）");
        Console.WriteLine($"  进程渲染模式 = {RenderOptions.ProcessRenderMode}");

        var stamps = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EventHandler onRender = (_, _) => stamps.Add(sw.Elapsed.TotalMilliseconds);
        CompositionTarget.Rendering += onRender;

        panel.Show();
        Pump(420);          // 200ms 动画 + 余量，够它跑完并补完图标

        CompositionTarget.Rendering -= onRender;
        panel.Close();

        var gaps = new List<double>();
        for (int i = 1; i < stamps.Count; i++) gaps.Add(stamps[i] - stamps[i - 1]);
        gaps.Sort();
        if (gaps.Count < 3)
        {
            Console.WriteLine($"  帧数不足：{gaps.Count}");
            return 1;
        }

        double avg = gaps.Average();
        double p95 = gaps[(int)Math.Min(gaps.Count - 1, gaps.Count * 0.95)];
        int dropped = gaps.Count(g => g > 33);
        Console.WriteLine($"  帧数={gaps.Count} 平均={avg:0.0}ms " +
            $"p95={p95:0.0}ms 最大={gaps[^1]:0.0}ms 掉帧(>33ms)={dropped}");
        return 0;
    }

    /// <summary>抓窗口当前的视觉树（不隐藏窗口）。动画中间帧要用它：
    /// RenderTargetBitmap 会把 RenderTransform / Opacity 一并应用到结果里，
    /// 所以抓出来就是「那一刻的缩放与透明度」，不是最终态。
    /// </summary>
    private static Bitmap Snapshot(Window window)
    {
        // RenderTargetBitmap 画不出 CacheMode 的内容（会得到空白块）。
        // 动画期间面板有多个元素挂着位图缓存，所以抓帧前递归摘掉、抓完挂回。
        var cached = new List<UIElement>();
        CollectCached(window, cached);
        if (cached.Count == 0) return SnapshotCore(window);

        var saved = cached.Select(e => e.CacheMode).ToList();
        foreach (var e in cached) e.CacheMode = null;
        window.UpdateLayout();
        var shot = SnapshotCore(window);
        for (int i = 0; i < cached.Count; i++) cached[i].CacheMode = saved[i];
        return shot;
    }

    private static void CollectCached(DependencyObject root, List<UIElement> found)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement el && el.CacheMode != null) found.Add(el);
            CollectCached(child, found);
        }
    }

    private static Bitmap SnapshotCore(Window window)
    {
        window.UpdateLayout();

        double dpiX = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int w = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpiX));
        int h = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpiX));

        var rtb = new RenderTargetBitmap(w, h, 96 * dpiX, 96 * dpiX, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        // Bitmap(Stream) 会引用原流，流一关就废；拷一份独立位图出来
        using var decoded = new Bitmap(ms);

        // 高 DPI 下像素数 = DIP × 缩放比。统一缩回 100% 再做合成，
        // 否则 175% 缩放的本机会把画布撑爆，截图里的字号也会和文档其他图不一致
        int logicalW = Math.Max(1, (int)Math.Round(window.ActualWidth));
        int logicalH = Math.Max(1, (int)Math.Round(window.ActualHeight));
        return decoded.Width == logicalW && decoded.Height == logicalH
            ? new Bitmap(decoded)
            : Resize(decoded, logicalW, logicalH);
    }

    /// <summary>
    /// 唤出动画的四个时间点：刚 Show（近乎不可见 + 已缩小）、55ms、110ms、结束。
    /// 用来证明"从盒子那一角缩放展开"确实在发生，而不是一句代码层面的自述。
    /// </summary>
    private static List<Shot> RenderOpenFrames(AppConfig config, ConfigStore store)
    {
        var box = new BoxWindow(config.Boxes[1], config, store);
        _ = Render(box);   // Owner 必须是已显示过的窗口

        var panel = new PanelWindow(box, config.Boxes[1], config, store);
        panel.ShowActivated = false;
        panel.Show();

        var shots = new List<Shot>();
        // Show() 返回时 ContentRendered 还没跑；先让它触发一次，动画才真正开始。
        // 首帧完全不可见（opacity=0），所以这里多等一拍，抓"已经能看到但还很小"的状态
        Pump(30);
        shots.Add(new Shot(Snapshot(panel), 0, 0));
        foreach (var ms in new[] { 35, 45, 110 })
        {
            Pump(ms);
            shots.Add(new Shot(Snapshot(panel), 0, 0));
        }

        panel.Close();
        Pump(200);
        return shots;
    }

    private static void DumpState(Window w, int t)
    {
        if (w.Content is not FrameworkElement root) return;
        double sc = root.RenderTransform is TransformGroup g && g.Children[0] is ScaleTransform st
            ? st.ScaleX : -1;
        Console.WriteLine($"  [close t={t}ms] opacity={root.Opacity:0.000} scale={sc:0.000} vis={w.Visibility}");
    }

    /// <summary>
    /// 气泡布局示意：盒子在下、面板在上，箭头指向盒子。
    /// 这里必须用真实屏幕坐标 —— 面板的落位完全取决于盒子在哪、工作区还有多少空间，
    /// 离屏摆位置会让 UpdatePlacement 把结果钳到工作区里，算出来的相对关系是假的。
    /// </summary>
    private static List<Shot> RenderBubble(AppConfig config, ConfigStore store, double fx, double fy, string name)
    {
        // 盒子相对工作区的位置（0 = 贴左/上，1 = 贴右/下）决定面板往哪边让开
        var wa = SystemParameters.WorkArea;
        var box = new BoxWindow(config.Boxes[1], config, store)
        { WindowStartupLocation = WindowStartupLocation.Manual };
        box.Show();
        Pump(160);
        box.Left = wa.X + fx * Math.Max(0, wa.Width - box.ActualWidth);
        box.Top = wa.Y + fy * Math.Max(0, wa.Height - box.ActualHeight);
        Pump(160);

        var panel = new PanelWindow(box, config.Boxes[1], config, store) { ShowActivated = false };
        panel.Show();
        Pump(500);          // 等展开动画与图标补全走完

        var panelShot = Snapshot(panel);
        var boxShot = Snapshot(box);

        const double M = 18;
        double pL = panel.Left + M, pT = panel.Top + M;
        double pR = panel.Left + panel.ActualWidth - M, pB = panel.Top + panel.ActualHeight - M;
        double bR = box.Left + box.ActualWidth, bB = box.Top + box.ActualHeight;
        string where = pL >= bR - 1 ? "右侧"
            : pR <= box.Left + 1 ? "左侧"
            : pB <= box.Top + 1 ? "上方" : "下方";

        Console.WriteLine($"  [bubble-{name}] 盒子=({box.Left:0},{box.Top:0}) 面板=({panel.Left:0},{panel.Top:0}) → 面板在盒子{where}");

        panel.Close();
        box.Hide();
        Pump(150);

        // Snapshot 已经把位图缩回逻辑尺寸（DIP），这里的偏移也必须用 DIP，不能再乘 DPI
        int px = (int)Math.Round(panel.Left - box.Left);
        int py = (int)Math.Round(panel.Top - box.Top);

        int minX = Math.Min(0, px), minY = Math.Min(0, py);
        int maxX = Math.Max(boxShot.Width, px + panelShot.Width);
        int maxY = Math.Max(boxShot.Height, py + panelShot.Height);

        int cw = maxX - minX + 120, ch = maxY - minY + 120;
        // 不能用 using：位图要交给 Shot 一直活到写文件
        var canvas = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingMode = CompositingMode.SourceOver;
            PaintWallpaper(g, cw, ch);
            g.DrawImage(boxShot, 60 - minX, 60 - minY);
            g.DrawImage(panelShot, 60 - minX + px, 60 - minY + py);
        }
        canvas.Save(Path.Combine(_outDir, $"bubble-{name}.png"), ImageFormat.Png);
        Console.WriteLine($"  bubble-{name}.png  {cw}×{ch}");
        return new List<Shot> { new(canvas, 0, 0) };
    }

    /// <summary>收起动画的时间点：完整态 → 60ms → 140ms。</summary>
    private static List<Shot> RenderCloseFrames(AppConfig config, ConfigStore store)
    {
        var box = new BoxWindow(config.Boxes[1], config, store);
        _ = Render(box);

        var panel = new PanelWindow(box, config.Boxes[1], config, store);
        panel.ShowActivated = false;
        panel.Show();
        Pump(400);        // 等打开动画走完

        var shots = new List<Shot> { new(Snapshot(panel), 0, 0) };
        panel.ClosePanel();
        int acc = 0;
        foreach (var ms in new[] { 14, 14, 18 })
        {
            Pump(ms);
            acc += ms;
            shots.Add(new Shot(Snapshot(panel), 0, 0));
        }
        return shots;
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(target);
        // SourceCopy 才能保住 alpha，SourceOver 会把半透明底色叠成黑底
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        using var attr = new ImageAttributes();
        attr.SetWrapMode(WrapMode.TileFlipXY);   // 避免缩放时边缘出现半透明黑边
        g.DrawImage(source, new Rectangle(0, 0, width, height),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attr);
        return target;
    }

    /// <summary>跑一小段消息循环：图标是异步提取的，不抽时间它们不会出现。</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // ---------- 合成 ----------

    private readonly record struct Shot(Bitmap Image, int X, int Y);

    /// <summary>
    /// 把若干窗口截图摆到一张"桌面感"的渐变背景上：横向依次排开、垂直居中，
    /// 画布按内容自适应（有下限、避免两三个小盒子被塞进一张巨图）。
    /// </summary>
    private static void Compose(string path, List<Shot> shots, int minWidth, int minHeight)
    {
        const int gap = 44;
        const int padding = 40;

        int contentW = shots.Sum(s => s.Image.Width) + gap * Math.Max(0, shots.Count - 1);
        int contentH = shots.Max(s => s.Image.Height);
        int canvasW = Math.Max(minWidth, contentW + padding * 2);
        int canvasH = Math.Max(minHeight, contentH + padding * 2);

        using var canvas = new Bitmap(canvasW, canvasH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingMode = CompositingMode.SourceOver;

            PaintWallpaper(g, canvasW, canvasH);

            // 内容整体居中；各图以自己的 Y 为基线对齐（都给了 Y 就按 Y，没给就垂直居中）
            int x = (canvasW - contentW) / 2;
            int centeredTop = (canvasH - contentH) / 2;
            foreach (var shot in shots)
            {
                int y = shot.Y > 0 ? shot.Y : centeredTop;
                g.DrawImage(shot.Image, new Rectangle(x, y, shot.Image.Width, shot.Image.Height));
                x += shot.Image.Width + gap;
            }
        }

        canvas.Save(path, ImageFormat.Png);
        Console.WriteLine($"  {Path.GetFileName(path)}  {canvasW}×{canvasH}");
    }

    /// <summary>重叠演示：第一张铺底下，后面的按偏移叠上去。</summary>
    private static void ComposeOverlap(string path, List<Shot> shots, int canvasW, int canvasH)
    {
        using var canvas = new Bitmap(canvasW, canvasH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingMode = CompositingMode.SourceOver;
            PaintWallpaper(g, canvasW, canvasH);

            // 底层盒子靠左上，上层面板故意压过去
            int[][] spots = { new[] { 60, 50 }, new[] { 150, 120 } };
            for (int i = 0; i < shots.Count; i++)
            {
                var spot = spots[Math.Min(i, spots.Length - 1)];
                g.DrawImage(shots[i].Image, new Rectangle(spot[0], spot[1],
                    shots[i].Image.Width, shots[i].Image.Height));
            }
        }

        canvas.Save(path, ImageFormat.Png);
        Console.WriteLine($"  {Path.GetFileName(path)}  {canvasW}×{canvasH}");
    }

    /// <summary>一块低饱和的深色渐变，接近"干净壁纸"而不是纯色块。</summary>
    private static void PaintWallpaper(Graphics g, int w, int h)
    {
        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, w, h),
            Color.FromArgb(255, 0x1B, 0x22, 0x2C),
            Color.FromArgb(255, 0x2A, 0x33, 0x40),
            LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(brush, 0, 0, w, h);

        // 右上角一团柔光，让画面不至于死板
        using var glow = new GraphicsPath();
        glow.AddEllipse(w * 0.55f, -h * 0.35f, w * 0.9f, h * 0.9f);
        using var pathBrush = new PathGradientBrush(glow)
        {
            CenterColor = Color.FromArgb(46, 0x8F, 0xA7, 0xFF),
            SurroundColors = new[] { Color.FromArgb(0, 0x8F, 0xA7, 0xFF) },
        };
        g.FillPath(pathBrush, glow);
    }

    // ---------- 假数据 ----------

    /// <summary>临时 sandbox：一套 config + 几个真实的盒子目录，全部在 Temp 下。</summary>
    private sealed class Sandbox : IDisposable
    {
        // 固定目录名而不是随机名：设置窗口的截图里会显示存储路径，
        // 每次重跑都换一个随机名就会产生一张只差几个字符的无意义 diff。
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tdb-shotkit");

        public (AppConfig Config, ConfigStore Store) Build()
        {
            // 上一轮残留（比如上次中途崩了）先清掉，保证每次生成的图一致
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }

            var configDir = Path.Combine(_root, "cfg");
            var dataRoot = Path.Combine(_root, "boxes");
            var store = new ConfigStore(configDir, dataRoot);

            var config = new AppConfig();
            config.Boxes.Clear();
            config.Settings.Global = new GlobalBoxSettings
            {
                Opacity = 0.9,
                BackColor = "#1F2A30",
                Rounded = true,
                CornerSize = 8,
            };

            // 方式 1：隐藏内容 + 缩略图
            var work = new BoxConfig
            {
                Name = "工作",
                X = 120, Y = 120, Opacity = 0.9, BackColor = "#1F2A30", CornerSize = 8,
                DisplayMode = BoxDisplayMode.Preview,
            };

            // 方式 3：不隐藏内容（内嵌网格）
            var tools = new BoxConfig
            {
                Name = "常用工具",
                X = 420, Y = 120, Opacity = 0.9, BackColor = "#202A44", CornerSize = 8,
                W = 344,
                DisplayMode = BoxDisplayMode.Content,
            };

            // 方式 2：隐藏内容 + 仅标题（映射盒同样是三档，行为完全一致）
            var mapped = new BoxConfig
            {
                Name = "项目 · travel-guide",
                X = 120, Y = 420, Opacity = 0.92, BackColor = "#2D3B2D", CornerSize = 8,
                DisplayMode = BoxDisplayMode.TitleOnly,
                Kind = BoxKind.Portal,
                PortalPath = MakePortalFolder(),
            };

            config.Boxes.Add(work);
            config.Boxes.Add(tools);
            config.Boxes.Add(mapped);

            FillBox(store.BoxDir(work), new (string Name, string? Source)[]
            {
                ("季度汇报.pptx", "C:/Windows/System32/mspaint.exe"),
                ("报价单.xlsx", "C:/Windows/System32/notepad.exe"),
                ("桌面整理方案.pdf", "C:/Windows/System32/charmap.exe"),
            });
            FillBox(store.BoxDir(tools), new (string Name, string? Source)[]
            {
                ("记事本.exe", "C:/Windows/System32/notepad.exe"),
                ("画图.exe", "C:/Windows/System32/mspaint.exe"),
                ("远程桌面.exe", "C:/Windows/System32/mstsc.exe"),
                ("字符映射表.exe", "C:/Windows/System32/charmap.exe"),
                ("写字板.exe", "C:/Windows/System32/write.exe"),
                ("资源管理器.exe", "C:/Windows/explorer.exe"),
                ("截图说明.png", "assets/icons/preview.png"),
                ("备忘.txt", null),
            });

            foreach (var box in config.Boxes)
            {
                if (box.Kind == BoxKind.Portal) continue;
                box.Order = Directory.EnumerateFileSystemEntries(store.BoxDir(box))
                    .Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();
            }

            store.Save(config);
            return (config, store);
        }

        private string MakePortalFolder()
        {
            var dir = Path.Combine(_root, "D盘", "项目", "tonia-travel-guide");
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "index.html", "yantai", "zibo", "assets", "README.md" })
            {
                var path = Path.Combine(dir, name);
                if (name.Contains('.')) File.WriteAllText(path, "x");
                else Directory.CreateDirectory(path);
            }
            return dir;
        }

        /// <summary>把系统 exe 复制一份并改名，图标就是真实的，截图才有说服力。</summary>
        private static void FillBox(string dir, IEnumerable<(string Name, string? Source)> files)
        {
            Directory.CreateDirectory(dir);
            var repo = FindRepoRoot();
            foreach (var (name, source) in files)
            {
                var target = Path.Combine(dir, name);
                try
                {
                    var src = source == null ? null
                        : source.StartsWith("assets/") && repo != null ? Path.Combine(repo, source) : source;
                    if (src != null && File.Exists(src))
                    {
                        File.Copy(src, target, overwrite: true);
                        continue;
                    }
                }
                catch
                {
                    // 复制失败就退化成空文件：图标会变成通用文档图标，不影响布局验证
                }
                File.WriteAllText(target, "sample");
            }
        }

        private static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ToniaDeskBox.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }
}
