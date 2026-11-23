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
        if (args.Length > 0 && args[0] == "--smoke")
            return Smoke(probeHotkey: args.Length > 1 ? args[1] : null);

        if (args.Length > 0) _outDir = args[0];
        Directory.CreateDirectory(_outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ToniaDeskBox;component/Themes/SharedStyles.xaml"),
        });

        ThemeService.Initialize(ThemeMode.Dark);
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

        window.Hide();

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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tdb-" + Guid.NewGuid().ToString("N")[..4]);

        public (AppConfig Config, ConfigStore Store) Build()
        {
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
