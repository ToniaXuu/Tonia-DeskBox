using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private PanelWindow? _panel;
    private bool _dragging;

    public event Action<BoxConfig>? BoxDeleted;

    public BoxWindow(BoxConfig box, AppConfig config, ConfigStore store)
    {
        InitializeComponent();
        Box = box;
        _config = config;
        _store = store;

        Left = box.X;
        Top = box.Y;
        ApplyAppearance();
        RefreshBadge();

        LocationChanged += OnLocationChanged;
        Closed += (_, _) => _panel?.Close();
        Loaded += (_, _) => SnapService.ClampIntoScreen(this);
        _baseBorderBrush = RootBorder.BorderBrush;
    }

    private void ApplyAppearance()
    {
        RootBorder.Background = ParseBrush(Box.BackColor);
        RootBorder.CornerRadius = Box.Rounded ? new CornerRadius(8) : new CornerRadius(0);
        Opacity = Box.Opacity;
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

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_dragging) SnapService.SnapToScreenEdges(this);
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
