using System.Threading;
using System.Windows;
using Microsoft.Win32;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;
using ToniaDeskBox.Windows;

namespace ToniaDeskBox;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayService? _tray;
    private readonly List<BoxWindow> _boxWindows = new();

    public ConfigStore Store { get; private set; } = null!;
    public AppConfig Config { get; private set; } = null!;
    public IReadOnlyList<BoxWindow> BoxWindows => _boxWindows;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        _tray.AutoStartToggled += OnAutoStartToggled;
        _tray.ExportRequested += ExportBackup;
        _tray.ExitRequested += ExitApp;

        foreach (var box in Config.Boxes)
            ShowBox(box);
    }

    private void ShowBox(BoxConfig box)
    {
        var w = new BoxWindow(box, Config, Store);
        w.BoxDeleted += OnBoxDeleted;
        _boxWindows.Add(w);
        w.Show();
    }

    public void RecreateBox(BoxConfig box)
    {
        var old = _boxWindows.FirstOrDefault(w => ReferenceEquals(w.Box, box));
        if (old != null)
        {
            _boxWindows.Remove(old);
            old.BoxDeleted -= OnBoxDeleted;
            old.Close();
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
        Config.Boxes.Add(box);
        Store.Save(Config);
        ShowBox(box);
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
