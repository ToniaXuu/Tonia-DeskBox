using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;
using WF = System.Windows.Forms;

namespace ToniaDeskBox.Windows;

public partial class BoxWindow : Window
{
    private const double ClickThreshold = 6;

    public BoxConfig Box { get; }

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly Brush _baseBorderBrush;
    private readonly bool _acrylic;
    private PanelWindow? _panel;
    private bool _dragging;

    public bool AcrylicActive => _acrylic;

    public event Action<BoxConfig>? BoxDeleted;

    public BoxWindow(BoxConfig box, AppConfig config, ConfigStore store)
    {
        Box = box;
        _config = config;
        _store = store;
        _acrylic = box.Acrylic && DwmHelper.IsAcrylicSupported();

        // AllowsTransparency 与 DWM system backdrop 互斥，按模式在显示前决定
        AllowsTransparency = !_acrylic;
        InitializeComponent();

        Left = box.X;
        Top = box.Y;
        ApplyAppearance();
        RefreshBadge();

        LocationChanged += OnLocationChanged;
        Closed += (_, _) => _panel?.Close();
        Loaded += (_, _) => SnapService.ClampIntoScreen(this);
        _baseBorderBrush = RootBorder.BorderBrush;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (!_acrylic) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(hwnd) is HwndSource source && source.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        DwmHelper.ApplyAcrylic(hwnd, Box.Rounded);
    }

    private void ApplyAppearance()
    {
        var color = ParseColor(Box.BackColor);
        if (_acrylic)
        {
            // 毛玻璃模式：底色作为亚克力上的半透明叠层，透明度映射到底色 alpha
            var a = (byte)Math.Clamp(Box.Opacity * 140, 20, 180);
            var b = new SolidColorBrush(Color.FromArgb(a, color.R, color.G, color.B));
            b.Freeze();
            RootBorder.Background = b;
            Opacity = 1.0;
        }
        else
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            RootBorder.Background = b;
            Opacity = Box.Opacity;
        }
        RootBorder.CornerRadius = Box.Rounded ? new CornerRadius(Box.CornerSize) : new CornerRadius(0);
        Topmost = Box.Topmost;
        NameText.Text = Box.Name;
    }

    private static Brush ParseBrush(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch
        {
            var b = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x30));
            b.Freeze();
            return b;
        }
    }

    private static Color ParseColor(string hex) => ((SolidColorBrush)ParseBrush(hex)).Color;

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!_dragging) return;
        SnapService.SnapToScreenEdges(this);
        if (Application.Current is App app)
            SnapService.SnapToBoxes(this, app.BoxWindows.Where(w => !ReferenceEquals(w, this)));
    }

    private void OnBoxMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var startPx = PointToScreen(e.GetPosition(this));
        _dragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        finally
        {
            _dragging = false;
        }

        var end = WF.Cursor.Position;
        double dist = Math.Abs(end.X - startPx.X) + Math.Abs(end.Y - startPx.Y);
        if (dist < ClickThreshold)
        {
            TogglePanel();
        }
        else
        {
            Box.X = Left;
            Box.Y = Top;
            _store.Save(_config);
        }
    }

    private void OnBoxMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        var miSettings = new MenuItem { Header = "盒子设置…" };
        miSettings.Click += (_, _) => OpenSettings();
        menu.Items.Add(miSettings);

        var miData = new MenuItem { Header = "打开数据目录" };
        miData.Click += (_, _) => Process.Start("explorer.exe", _store.BoxDir(Box));
        menu.Items.Add(miData);

        var miDelete = new MenuItem { Header = "删除盒子" };
        miDelete.Click += (_, _) => DeleteBox();
        menu.Items.Add(miDelete);

        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OpenSettings()
    {
        var dlg = new BoxSettingsWindow(Box, _config, _store) { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Applied) return;

        _store.Save(_config);
        if (dlg.AcrylicWanted != AcrylicActive)
            ((App)Application.Current).RecreateBox(Box);
        else
            ApplySettings();
    }

    public void ApplySettings()
    {
        ApplyAppearance();
        RefreshBadge();
        _panel?.RefreshItems();
    }

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            Show();
        }
        else
        {
            _panel?.Hide();
            Hide();
        }
    }

    private void DeleteBox()
    {
        int count = _store.CountOf(Box);
        string msg = count > 0
            ? $"盒子“{Box.Name}”内有 {count} 个文件，删除后这些文件将放回桌面。\n\n确定删除？"
            : $"确定删除空盒子“{Box.Name}”？";
        var result = MessageBox.Show(this, msg, "删除盒子",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var dir = _store.BoxDir(Box);
        if (Directory.Exists(dir))
        {
            var files = Directory.EnumerateFileSystemEntries(dir).ToList();
            FileMover.MoveBackToDesktop(files, out var skipped);
            if (skipped.Count > 0)
                MessageBox.Show(this,
                    $"以下文件因桌面同名/占用未能放回，已保留在数据目录：\n{string.Join("\n", skipped)}",
                    "Tonia-DeskBox", MessageBoxButton.OK, MessageBoxImage.Information);
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (IOException) { }
        }

        BoxDeleted?.Invoke(Box);
    }

    public void TogglePanel()
    {
        if (_panel != null)
        {
            _panel.Close();
            _panel = null;
            return;
        }
        _panel = new PanelWindow(this, Box, _config, _store);
        _panel.Closed += (_, _) => _panel = null;
        _panel.Show();
    }

    public void RefreshBadge()
    {
        CountText.Text = _store.CountOf(Box).ToString();
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            RootBorder.BorderBrush = Brushes.White;
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        RootBorder.BorderBrush = _baseBorderBrush;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        RootBorder.BorderBrush = _baseBorderBrush;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        MoveIntoBox(files);
    }

    private void MoveIntoBox(string[] files)
    {
        var dir = _store.BoxDir(Box);
        Directory.CreateDirectory(dir);
        bool changed = false;

        foreach (var f in files)
        {
            if (string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(f);
            var target = Path.Combine(dir, name);
            if (FileMover.ExistsConflict(target))
            {
                MessageBox.Show(this, $"目标已存在：{name}\n已取消移动，原文件保留在原位置。",
                    "Tonia-DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            try
            {
                FileMover.MoveTo(f, target);
                if (!Box.Order.Contains(name)) Box.Order.Add(name);
                changed = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"移动失败：{name}\n{ex.Message}\n原文件保留在原位置。",
                    "Tonia-DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        if (changed)
        {
            _store.Save(_config);
            RefreshBadge();
            _panel?.RefreshItems();
        }
    }
}
