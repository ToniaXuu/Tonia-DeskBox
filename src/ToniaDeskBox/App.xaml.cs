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
    private Mutex? _mutex;
    private TrayService? _tray;
    private readonly List<BoxWindow> _boxWindows = new();
    private DispatcherTimer? _healTimer;

    public ConfigStore Store { get; private set; } = null!;
    public AppConfig Config { get; private set; } = null!;
    public IReadOnlyList<BoxWindow> BoxWindows => _boxWindows;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherException;

        _mutex = new Mutex(true, "ToniaDeskBox_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Tonia-DeskBox 已在运行。", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Store = new ConfigStore();
        Config = Store.Load();

        _tray = new TrayService(Store.DataDir, Config.Settings.Autorun || AutoStartService.IsEnabled());
        _tray.NewBoxRequested += CreateBox;
        _tray.ShowAllRequested += ShowAll;
        _tray.HideAllRequested += HideAll;
        _tray.AutoSortRequested += AutoSortBoxes;
        _tray.AutoStartToggled += OnAutoStartToggled;
        _tray.GlobalSettingsRequested += OpenGlobalSettings;
        _tray.ExportRequested += ExportBackup;
        _tray.ExitRequested += ExitApp;

        var firstRun = Config.Boxes.Count == 0;
        if (firstRun)
        {
            Config.Boxes.Add(new BoxConfig { Name = "系统工具" });
            Config.Boxes.Add(new BoxConfig { Name = "办公工具" });
            Store.Save(Config);
        }

        foreach (var box in Config.Boxes)
            ShowBox(box);

        if (firstRun && _boxWindows.Count > 0)
        {
            var w0 = _boxWindows[0];
            void OnFirstRendered(object? s, EventArgs args)
            {
                w0.ContentRendered -= OnFirstRendered;
                AutoSortBoxes();
            }
            w0.ContentRendered += OnFirstRendered;
        }

        _healTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _healTimer.Tick += (_, _) => HealBoxes();
        _healTimer.Start();

        _ = PrefetchIconsAsync();
    }

    // 后台预热图标缓存：启动后先把各盒子的图标提取好，用户点盒子时面板能秒开
    private async Task PrefetchIconsAsync()
    {
        await Task.Delay(1000);
        foreach (var box in Config.Boxes)
        {
            var dir = Store.BoxDir(box);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try { await IconService.GetAsync(file); }
                catch { }
            }
        }
    }

    private void HealBoxes()
    {
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
            // 兜底：位置漂移出工作区（如任务栏调整/分辨率变化）自动拉回
            SnapService.ClampIntoScreen(w);
        }
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
        try
        {
            var dir = Store?.DataDir ?? AppContext.BaseDirectory;
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{args.Exception}\n\n");
        }
        catch
        {
            // 日志写不进去也不能让异常吞掉主线程
        }
        MessageBox.Show($"发生错误：{args.Exception.Message}\n\n详细信息见数据目录 error.log",
            "Tonia-DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        args.Handled = true;
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
            box.Acrylic = g.Acrylic && DwmHelper.IsAcrylicSupported();
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

    public void ApplyGlobalToAllBoxes(GlobalBoxSettings g)
    {
        foreach (var box in Config.Boxes)
        {
            box.Opacity = g.Opacity;
            box.BackColor = g.BackColor;
            box.Acrylic = g.Acrylic;
            box.Rounded = g.Rounded;
            box.CornerSize = g.CornerSize;
            box.Topmost = g.Topmost;
        }
        Store.Save(Config);

        // 毛玻璃模式切换会改变 AllowsTransparency，必须整窗重建；其余原地刷新
        foreach (var w in _boxWindows.ToList())
        {
            bool acrylicNow = w.Box.Acrylic && DwmHelper.IsAcrylicSupported();
            if (w.AcrylicActive != acrylicNow)
                RecreateBox(w.Box);
            else
                w.ApplySettings();
        }
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
            BackupService.ExportZip(Store.DataDir, dlg.FileName);
            MessageBox.Show($"备份完成：\n{dlg.FileName}", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"备份失败：{ex.Message}", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExitApp()
    {
        foreach (var w in _boxWindows.ToList())
            w.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
