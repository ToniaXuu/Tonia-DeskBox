using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;
using WF = System.Windows.Forms;

namespace ToniaDeskBox.Windows;

public partial class BoxWindow : Window
{
    private const double ClickThreshold = 6;
    private const double InlineMinWidth = 240;
    private const double InlineDragThreshold = 10;

    public BoxConfig Box { get; }

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly Brush _baseBorderBrush;
    private readonly Brush _hoverBorderBrush;
    private readonly bool _acrylic;
    private PanelWindow? _panel;
    private bool _dragging;
    private System.Windows.Vector _grabOffset;
    private System.Drawing.Point _dragStartPx;
    private DispatcherTimer? _dragTimer;

    // 内嵌网格
    private List<InlineItem> _inlineItems = new();
    private bool _inlinePressed;
    private Point _inlineDragStart;

    // 手柄调整大小
    private double _resizeStartW;
    private double _resizeStartH;
    private bool _resizeHeightFixed;

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

        MouseEnter += (_, _) => RootBorder.BorderBrush = _hoverBorderBrush;
        MouseLeave += (_, _) => RootBorder.BorderBrush = _baseBorderBrush;
        DragTimer.Tick += OnDragTick;
        RootBorder.LostMouseCapture += OnBoxLostCapture;
        // 面板实时跟随：盒子每次被系统移动都同步面板（定时器只有 16ms 粒度，会有一拍延迟）
        LocationChanged += (_, _) =>
        {
            if (_dragging) _panel?.FollowOwner();
        };
        Closed += (_, _) => _panel?.Close();
        Loaded += (_, _) => SnapService.ClampIntoScreen(this);
        // SizeToContent 在 Loaded 后才量出真实尺寸，首帧渲染完成后再钳制一次
        ContentRendered += (_, _) => SnapService.ClampIntoScreen(this);
        _baseBorderBrush = RootBorder.BorderBrush;
        var hover = new SolidColorBrush(Color.FromArgb(0x7F, 0xFF, 0xFF, 0xFF));
        hover.Freeze();
        _hoverBorderBrush = hover;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        PinToDesktop(hwnd);

        if (!_acrylic) return;
        if (HwndSource.FromHwnd(hwnd) is HwndSource source && source.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        DwmHelper.ApplyAcrylic(hwnd, Box.Rounded);
    }

    private const int GWL_HWNDPARENT = -8;

    public void EnsureDesktopOwner()
    {
        if (!IsLoaded) return;
        try { PinToDesktop(new WindowInteropHelper(this).Handle); }
        catch { }
    }

    // 钉到桌面：owner 设为 Progman，盒子成为"桌面组件"——
    // 不进 Alt+Tab/任务视图、Win+D 显示桌面不影响、显示在壁纸之上普通窗口之下
    private static void PinToDesktop(IntPtr hwnd)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero || hwnd == IntPtr.Zero) return;
        if (GetWindowLongPtr(hwnd, GWL_HWNDPARENT) == progman) return;
        SetWindowLongPtr(hwnd, GWL_HWNDPARENT, progman);
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

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
        ApplySizeMode();
    }

    private void ApplySizeMode()
    {
        bool expanded = Box.W >= InlineMinWidth;
        GridHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ResizeGrip.Visibility = Box.W > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (Box.W > 0)
        {
            Width = Math.Max(Box.W, 160);
            if (Box.H > 0)
            {
                SizeToContent = SizeToContent.Manual;
                Height = Math.Max(Box.H, 80);
            }
            else
            {
                SizeToContent = SizeToContent.Height;
            }
        }
        else
        {
            SizeToContent = SizeToContent.WidthAndHeight;
        }

        if (expanded)
            RefreshInlineItems();
        else
            InlineItems.ItemsSource = null;
    }

    private class InlineItem : INotifyPropertyChanged
    {
        public string FilePath { get; init; } = "";
        public string DisplayName { get; init; } = "";

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public void RefreshInlineItems()
    {
        var dir = _store.BoxDir(Box);
        var names = Directory.Exists(dir)
            ? Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Cast<string>()
                .ToList()
            : new List<string>();

        var ordered = Box.Order.Where(names.Contains)
            .Concat(names.Where(n => !Box.Order.Contains(n)));

        _inlineItems = ordered
            .Select(n =>
            {
                var path = Path.Combine(dir, n);
                return new InlineItem
                {
                    FilePath = path,
                    DisplayName = n,
                    Icon = IconService.GetCached(path),
                };
            })
            .ToList();

        InlineItems.ItemsSource = _inlineItems;
        InlineEmptyHint.Visibility = _inlineItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _ = LoadInlineIconsAsync(_inlineItems);
    }

    private async Task LoadInlineIconsAsync(List<InlineItem> items)
    {
        foreach (var item in items)
        {
            ImageSource? icon;
            try { icon = await IconService.GetAsync(item.FilePath); }
            catch { continue; }
            if (icon != null) item.Icon = icon;
        }
    }

    // ---------- 内嵌图标：单击启动 / 拖出 / 右键 ----------

    private InlineItem? InlineItemFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as InlineItem;

    private void OnInlineItemDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _inlineDragStart = e.GetPosition(this);
        _inlinePressed = true;
        (sender as FrameworkElement)?.CaptureMouse();
    }

    private void OnInlineItemMove(object sender, MouseEventArgs e)
    {
        if (!_inlinePressed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _inlineDragStart.X) + Math.Abs(pos.Y - _inlineDragStart.Y) <= InlineDragThreshold)
            return;
        _inlinePressed = false;
        var cell = (FrameworkElement)sender;
        cell.ReleaseMouseCapture();
        StartInlineDragOut(cell);
    }

    private void OnInlineItemUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_inlinePressed) return;
        _inlinePressed = false;
        ((FrameworkElement)sender).ReleaseMouseCapture();
        if (InlineItemFrom(sender) is { } item)
            ShortcutService.Open(item.FilePath);
    }

    private void StartInlineDragOut(FrameworkElement cell)
    {
        if (InlineItemFrom(cell) is not { } item) return;

        var data = new DataObject();
        var list = new StringCollection { item.FilePath };
        data.SetFileDropList(list);
        using var ms = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Move));
        data.SetData("Preferred DropEffect", ms);
        try
        {
            DragDrop.DoDragDrop(cell, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            RefreshBadge();
            _panel?.RefreshItems();
        }
    }

    private void OnInlineItemRightDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (InlineItemFrom(sender) is not { } item) return;

        var menu = new ContextMenu();

        var miOpen = new MenuItem { Header = "打开" };
        miOpen.Click += (_, _) => ShortcutService.Open(item.FilePath);
        menu.Items.Add(miOpen);

        var miLoc = new MenuItem { Header = "打开所在位置" };
        miLoc.Click += (_, _) => ShortcutService.OpenContainingFolder(item.FilePath);
        menu.Items.Add(miLoc);

        var miProp = new MenuItem { Header = "属性" };
        miProp.Click += (_, _) => ShortcutService.ShowProperties(item.FilePath);
        menu.Items.Add(miProp);

        menu.Items.Add(new Separator());

        var miBack = new MenuItem { Header = "放回桌面" };
        miBack.Click += (_, _) => MoveInlineBack(item);
        menu.Items.Add(miBack);

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void MoveInlineBack(InlineItem item)
    {
        FileMover.MoveBackToDesktop(new[] { item.FilePath }, out var skipped);
        Box.Order.Remove(item.DisplayName);
        _store.Save(_config);
        RefreshBadge();
        _panel?.RefreshItems();

        if (skipped.Count > 0)
            MessageBox.Show(this,
                $"桌面已存在同名文件或文件被占用，未能放回：\n{string.Join("\n", skipped)}",
                "Tonia-DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---------- 手柄调整大小 ----------

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        _resizeStartW = ActualWidth;
        _resizeStartH = ActualHeight;
        _resizeHeightFixed = Box.H > 0;
    }

    private void OnResizeDrag(object sender, DragDeltaEventArgs e)
    {
        Box.W = Math.Round(Math.Clamp(_resizeStartW + e.HorizontalChange, InlineMinWidth, 600));
        Width = Box.W;

        // 原本高度自适应时，向下拉才进入固定高度模式
        if (_resizeHeightFixed || e.VerticalChange > 4)
        {
            Box.H = Math.Round(Math.Clamp(_resizeStartH + e.VerticalChange, 100, 460));
            SizeToContent = SizeToContent.Manual;
            Height = Box.H;
        }
        _panel?.FollowOwner();
    }

    private void OnResizeDone(object sender, DragCompletedEventArgs e)
    {
        SnapService.ClampIntoScreen(this);
        Box.X = Left;
        Box.Y = Top;
        ApplySizeMode();
        _store.Save(_config);
        _panel?.FollowOwner();
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

    private DispatcherTimer DragTimer => _dragTimer ??= new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(16),
    };

    private void OnDragTick(object? sender, EventArgs e)
    {
        if (!_dragging) return;

        double dpi;
        try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch { return; }

        // 期望位置始终从原始光标坐标重算，吸附结果不会累积到下一帧；
        // 吸附→贴盒→防重叠→钳制全部在值上算完，只写一次窗口位置（避免多次 SetWindowPos 抽搐）
        var p = WF.Cursor.Position;
        double x = p.X / dpi - _grabOffset.X;
        double y = p.Y / dpi - _grabOffset.Y;
        var others = Application.Current is App app
            ? app.BoxWindows.Where(w => !ReferenceEquals(w, this))
            : null;
        if (SnapService.ApplyDragPosition(this, x, y, others))
        {
            Box.X = Left;
            Box.Y = Top;
            _panel?.FollowOwner();
        }
    }

    private void OnBoxMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = WF.Cursor.Position;
        try
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _grabOffset = new System.Windows.Vector(p.X / dpi - Left, p.Y / dpi - Top);
        }
        catch { _grabOffset = default; }
        _dragStartPx = p;
        _dragging = true;
        DragTimer.Start();
        RootBorder.CaptureMouse();
    }

    private void OnBoxMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        EndDrag();

        var end = WF.Cursor.Position;
        double dist = Math.Abs(end.X - _dragStartPx.X) + Math.Abs(end.Y - _dragStartPx.Y);
        if (dist < ClickThreshold)
        {
            TogglePanel();
        }
        else
        {
            _store.Save(_config);
        }
    }

    private void OnBoxLostCapture(object? sender, MouseEventArgs e)
    {
        if (_dragging) EndDrag();
    }

    private void EndDrag()
    {
        _dragging = false;
        DragTimer.Stop();
        if (RootBorder.IsMouseCaptured) RootBorder.ReleaseMouseCapture();

        // 松手补一次吸附+防重叠+钳制（最后一帧光标可能晚于定时器）
        var others = Application.Current is App app
            ? app.BoxWindows.Where(w => !ReferenceEquals(w, this))
            : null;
        SnapService.ApplyDragPosition(this, Left, Top, others);
        Box.X = Left;
        Box.Y = Top;
        _panel?.FollowOwner();
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

        menu.Items.Add(new Separator());

        var count = _store.CountOf(Box);
        var miDelete = new MenuItem
        {
            Header = count > 0 ? $"删除盒子（内有 {count} 个文件）" : "删除盒子",
            IsEnabled = count == 0,
            ToolTip = count > 0 ? "盒子内有文件，请先在面板中移出后再删除" : null,
        };
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
        Box.Hidden = !visible;
        _store.Save(_config);
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
        var result = MessageBox.Show(this, $"确定删除空盒子“{Box.Name}”？",
            "删除盒子", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var dir = _store.BoxDir(Box);
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (IOException) { }

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

    public void SnapPanel() => _panel?.FollowOwner();

    public void RefreshBadge()
    {
        CountText.Text = _store.CountOf(Box).ToString();
        if (GridHost.Visibility == Visibility.Visible) RefreshInlineItems();
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
