using System.Threading;
using System.Windows;
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

        _tray = new TrayService(Store.DataDir);
        _tray.NewBoxRequested += CreateBox;
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
        var w = _boxWindows.FirstOrDefault(x => x.Box == box);
        if (w != null)
        {
            _boxWindows.Remove(w);
            w.BoxDeleted -= OnBoxDeleted;
            w.Close();
        }
        Config.Boxes.Remove(box);
        Store.Save(Config);
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
