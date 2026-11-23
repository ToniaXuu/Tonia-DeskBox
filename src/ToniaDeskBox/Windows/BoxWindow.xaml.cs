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

namespace ToniaDeskBox.Windows;

public partial class BoxWindow : Window
{
    private const double ClickThreshold = 6;
    private const double InlineDragThreshold = 10;

    public BoxConfig Box { get; }

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    // 全窗口共用的主题笔刷；Update() 会原地改 Color，已引用的笔刷同步刷新
    private readonly AppearanceTheme _theme;
    private bool _dropActive;
    private PanelWindow? _panel;
    private bool _dragging;
    private System.Windows.Vector _grabOffset;
    private PixelPoint _dragStartPx;
    private DispatcherTimer? _dragTimer;
    private readonly BoxWatcher _watcher;

    /// <summary>映射盒看得见的是目标目录，普通盒是自己的数据目录。</summary>
    public bool IsPortal => ConfigStore.IsPortal(Box);
    public string SourceDir => _store.SourceDir(Box);

    // 内嵌网格
    private List<InlineItem> _inlineItems = new();
    private bool _inlinePressed;
    private Point _inlineDragStart;

    // 手柄调整大小
    private double _resizeStartW;
    private double _resizeStartH;
    private PixelPoint _resizeOrigin;   // 开始缩放时的屏幕光标位置
    private bool _resizeHeightFixed;

    public event Action<BoxConfig>? BoxDeleted;

    public BoxWindow(BoxConfig box, AppConfig config, ConfigStore store)
    {
        Box = box;
        _config = config;
        _store = store;

        InitializeComponent();
        // XAML 里已声明 AllowsTransparency=True：底色是半透明纯色，不再使用 DWM 亚克力。
        // 主题挂到窗口资源后，所有 Theme.* 动态资源都从这里解析。
        _theme = AppearanceTheme.Attach(this);

        Left = box.X;
        Top = box.Y;
        ApplyAppearance();
        RefreshBadge();

        // 外部往盒目录里拷文件、或在资源管理器里删掉盒内文件：角标与缩略图必须自己跟上。
        // 否则用户会怀疑"是不是丢文件了"——这是信任问题，不是功能问题。
        _watcher = new BoxWatcher(SourceDir, OnDirectoryChanged);

        MouseEnter += (_, _) => ApplyInteractionBrush(hover: true);
        MouseLeave += (_, _) => ApplyInteractionBrush(hover: false);
        DragTimer.Tick += OnDragTick;
        RootBorder.LostMouseCapture += OnBoxLostCapture;
        // 面板实时跟随：盒子每次被系统移动都同步面板（定时器只有 16ms 粒度，会有一拍延迟）
        LocationChanged += (_, _) =>
        {
            if (_dragging) _panel?.FollowOwner();
        };
        Closed += (_, _) =>
        {
            _panel?.Close();
            _watcher.Dispose();
        };
        Loaded += (_, _) =>
        {
            SnapService.ClampIntoScreen(this);
            _watcher.EnsureStarted();
        };
        // SizeToContent 在 Loaded 后才量出真实尺寸，首帧渲染完成后再钳制一次
        ContentRendered += (_, _) => SnapService.ClampIntoScreen(this);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        PinToDesktop(hwnd);
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
        // 同一套 Theme.* 笔刷驱动表面、描边、文字、分类色；
        // 底色亮度决定前景是白墨还是深墨，白底不会再出现白字。
        _theme.Update(Box);
        // 透明度只作用在底色笔刷上，窗口本身保持不透明，文字永远清晰
        Opacity = 1.0;

        RootBorder.CornerRadius = Box.Rounded ? new CornerRadius(Box.CornerSize) : new CornerRadius(0);
        Topmost = Box.Topmost;
        NameText.Text = Box.Name;
        PortalTag.Visibility = IsPortal ? Visibility.Visible : Visibility.Collapsed;
        PortalTag.ToolTip = IsPortal ? $"映射自：{SourceDir}" : null;
        ApplySizeMode();
        ApplyInteractionBrush(IsMouseOver);
    }

    private void ApplyInteractionBrush(bool hover)
    {
        string bgKey = _dropActive ? ThemeKeys.SurfaceDrop
            : hover ? ThemeKeys.SurfaceHover : ThemeKeys.Surface;
        string borderKey = _dropActive ? ThemeKeys.BorderDrop
            : hover ? ThemeKeys.BorderHover : ThemeKeys.Border;
        RootBorder.Background = _theme.BrushOf(bgKey);
        RootBorder.BorderBrush = _theme.BrushOf(borderKey);
    }

    private void ApplySizeMode()
    {
        bool content = Box.DisplayMode == BoxDisplayMode.Content;

        GridHost.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        // 搜索 / 更多只出现在「显示内容」的盒子上；隐藏内容的两档由点开面板去用
        BoxActions.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        ResizeGrip.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        // 整卡可拖 + 点击开面板，两者共用同一个位移阈值，所以光标始终保持普通箭头
        RootBorder.Cursor = Cursors.Arrow;

        if (content)
        {
            // 宽度吸附到整列：否则拖动改宽时换行点会在两列之间反复跳，视觉上就是盒子在抖
            double wanted = Math.Max(Box.W, BoxMetrics.ContentMinWidth);
            Box.W = BoxMetrics.Snap(wanted, out _);
            Width = Box.W;

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
            // 折叠态一律自适应；配置里的 W/H 原样留着，切回「显示内容」时直接恢复
            SizeToContent = SizeToContent.WidthAndHeight;
            Width = double.NaN;
            Height = double.NaN;
            InlineItems.ItemsSource = null;
            _inlineItems = new List<InlineItem>();
        }

        if (content) RefreshInlineItems();
        RefreshPreview();
    }

    /// <summary>
    /// 切换展示方式。三档对普通盒与映射盒一视同仁。
    /// </summary>
    private void SetDisplayMode(BoxDisplayMode mode)
    {
        if (Box.DisplayMode == mode) return;
        Box.DisplayMode = mode;

        if (mode == BoxDisplayMode.Content)
        {
            // 从折叠态切过来时给一个刚好放三列的宽度，免得出来一个只有两列余白的盒子
            if (Box.W < BoxMetrics.ContentMinWidth) Box.W = BoxMetrics.ContentMinWidth;
        }
        else
        {
            // 折叠态没有"内容视图"这回事，面板留着会变成一个没入口的孤儿窗口
            _panel?.ClosePanel();
        }

        // 先重排再落盘：ApplySizeMode 会把宽度吸附到整列，保存的应该是吸附后的值
        ApplySizeMode();
        RefreshBadge();
        _store.Save(_config);
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
        var dir = SourceDir;
        var names = Directory.Exists(dir)
            ? Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Cast<string>()
                .ToList()
            : new List<string>();

        var ordered = Box.Order.Where(names.Contains)
            .Concat(names.Where(n => !Box.Order.Contains(n)))
            .ToList();
        var display = DisplayNames.Build(ordered);

        _inlineItems = ordered
            .Select(n =>
            {
                var path = Path.Combine(dir, n);
                return new InlineItem
                {
                    FilePath = path,
                    DisplayName = display[n],
                    Icon = IconService.GetCached(path),
                };
            })
            .ToList();

        InlineItems.ItemsSource = _inlineItems;
        InlineEmptyHint.Text = IsPortal
            ? Directory.Exists(dir) ? "这个文件夹是空的" : "映射的文件夹当前不可用"
            : "把桌面图标拖进来收纳";
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
        // 拖出期间由资源管理器真正移除文件，watcher 会收到 DELETED；
        // 这一拍必须压住，否则盒子在拖拽途中重建列表
        using (_watcher.Suspend())
        {
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
    }

    private void OnInlineItemRightDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (InlineItemFrom(sender) is not { } item) return;

        var menu = new ContextMenu();
        AppearanceTheme.AttachMenu(menu, Box, _config.Settings.MenuOpacity, this);

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
        List<string> skipped;
        using (_watcher.Suspend())
        {
            FileMover.MoveBackToDesktop(new[] { item.FilePath }, out skipped);
        }
        Box.Order.Remove(Path.GetFileName(item.FilePath));
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
        _resizeOrigin = ScreenUtil.CursorPosition();
        _resizeHeightFixed = Box.H > 0;
    }

    private void OnResizeDrag(object sender, DragDeltaEventArgs e)
    {
        if (Box.DisplayMode != BoxDisplayMode.Content) return;

        // 用「光标相对起点的绝对位移」，不用 DragDelta 的增量。
        // 手柄长在窗口右下角，窗口一变宽手柄就跟着右移；鼠标不动时，
        // 相邻两帧算出来的相对位移会一正一负，宽度就在两档之间来回跳。
        // 量绝对距离对这个反馈环免疫。
        var p = ScreenUtil.CursorPosition();
        double dpi;
        try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch { return; }
        if (dpi <= 0) return;

        double dx = (p.X - _resizeOrigin.X) / dpi;
        double dy = (p.Y - _resizeOrigin.Y) / dpi;

        // 吸附到整列：每跨过一整列才变一次，换行点不会在临界宽度上反复跳
        Box.W = BoxMetrics.Snap(_resizeStartW + dx, out _);
        Width = Box.W;

        // 原本高度自适应时，向下拉才进入固定高度模式
        if (_resizeHeightFixed || dy > 4)
        {
            Box.H = Math.Round(Math.Clamp(_resizeStartH + dy, 100, 460));
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
        var p = ScreenUtil.CursorPosition();
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
        var p = ScreenUtil.CursorPosition();
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

        var end = ScreenUtil.CursorPosition();
        double dist = Math.Abs(end.X - _dragStartPx.X) + Math.Abs(end.Y - _dragStartPx.Y);
        if (dist < ClickThreshold)
        {
            // 拖动与点击共用同一个阈值：没挪动就当作点击，三档都打开面板
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
        var menu = BuildContextMenu();
        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>标题栏上的「⋯」：和右键同一份菜单，只是从按钮下方弹出。</summary>
    private void OnBoxMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = BuildContextMenu();
        menu.PlacementTarget = BoxMoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>标题栏上的放大镜：面板没开就开「搜索态面板」，已开就地展开搜索栏。</summary>
    private void OnBoxSearchClick(object sender, RoutedEventArgs e)
    {
        if (_panel != null)
        {
            _panel.ShowSearchBar();
            _panel.Activate();
            return;
        }

        _panel = new PanelWindow(this, Box, _config, _store, PanelOpenMode.Search);
        _panel.Closed += (_, _) => _panel = null;
        _panel.Show();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        AppearanceTheme.AttachMenu(menu, Box, _config.Settings.MenuOpacity, this);

        // 展示方式：三档单选，换到哪一档都由 ApplySizeMode 统一收敛
        var miMode = new MenuItem { Header = "展示方式" };
        miMode.Items.Add(ModeItem("缩略图 + 标题", BoxDisplayMode.Preview, "折叠：标题下面挂 3 个文件缩略图和总数"));
        miMode.Items.Add(ModeItem("仅标题", BoxDisplayMode.TitleOnly, "折叠：只留一行标题，最省地方"));
        miMode.Items.Add(ModeItem("显示内容", BoxDisplayMode.Content, "盒子内嵌图标网格，点空白处打开面板"));
        menu.Items.Add(miMode);

        menu.Items.Add(new Separator());

        var miSettings = new MenuItem { Header = "盒子设置…" };
        miSettings.Click += (_, _) => OpenSettings();
        menu.Items.Add(miSettings);

        if (IsPortal)
        {
            var miOpenPortal = new MenuItem { Header = "打开映射的文件夹" };
            miOpenPortal.Click += (_, _) =>
            {
                try { Process.Start("explorer.exe", SourceDir); }
                catch { }
            };
            menu.Items.Add(miOpenPortal);

            var miRetarget = new MenuItem { Header = "更改映射目标…" };
            miRetarget.Click += (_, _) => ChangePortalTarget();
            menu.Items.Add(miRetarget);
        }
        else
        {
            var miData = new MenuItem { Header = "打开数据目录" };
            miData.Click += (_, _) => Process.Start("explorer.exe", SourceDir);
            menu.Items.Add(miData);
        }

        menu.Items.Add(new Separator());

        var count = _store.CountOf(Box);
        // 映射盒里的文件不归本程序管：删盒子只摘掉这扇"窗"，绝不能删目标目录
        var miDelete = new MenuItem
        {
            Header = IsPortal
                ? "移除这个映射盒子"
                : count > 0 ? $"删除盒子（内有 {count} 个文件）" : "删除盒子",
            IsEnabled = IsPortal || count == 0,
            ToolTip = IsPortal
                ? "只移除盒子，不会删除文件夹里的文件"
                : count > 0 ? "盒子内有文件，请先在面板中移出后再删除" : null,
        };
        miDelete.Click += (_, _) => DeleteBox();
        menu.Items.Add(miDelete);

        return menu;
    }

    private MenuItem ModeItem(string header, BoxDisplayMode mode, string tip)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = Box.DisplayMode == mode,
            ToolTip = tip,
        };
        item.Click += (_, _) => SetDisplayMode(mode);
        return item;
    }

    /// <summary>
    /// 换一个映射目标。目录本身不动，只改指向；因为 watcher 与渲染都绑定在旧目录上，
    /// 改完交给 App 重建窗口最干净（也顺带刷新了面板）。
    /// </summary>
    private void ChangePortalTarget()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要映射到桌面的文件夹",
            InitialDirectory = Directory.Exists(SourceDir) ? SourceDir : "",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;

        var next = dlg.FolderName;
        if (string.Equals(Path.GetFullPath(next), Path.GetFullPath(SourceDir),
                StringComparison.OrdinalIgnoreCase))
            return;

        Box.PortalPath = Path.GetFullPath(next);
        Box.Order = new List<string>();
        _store.Save(_config);

        if (Application.Current is App app) app.RecreateBox(Box);
    }

    private void OpenSettings()
    {
        var dlg = new BoxSettingsWindow(Box, _config, _store) { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Applied) return;

        _store.Save(_config);
        ApplySettings();
    }

    public void ApplySettings()
    {
        ApplyAppearance();
        RefreshBadge();
        // 设置里把展示方式改回折叠态时，开着的内容面板要跟着收起
        if (Box.DisplayMode != BoxDisplayMode.Content) _panel?.ClosePanel();
        // 面板可能开着：底色/圆角/前景对比色要跟着一起换
        _panel?.RefreshTheme();
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
        if (IsPortal)
        {
            // 说清楚代价：删的是"这扇窗"，不是文件夹
            var confirm = MessageBox.Show(this,
                $"移除映射盒子“{Box.Name}”？\n\n只会移除这个盒子，文件夹里的文件一个都不会删：\n{SourceDir}",
                "移除映射盒子", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            BoxDeleted?.Invoke(Box);
            return;
        }

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
            // 走面板自己的幂等关闭入口，避免与失焦关闭撞在同一帧
            _panel.ClosePanel();
            return;
        }
        _panel = new PanelWindow(this, Box, _config, _store);
        _panel.Closed += (_, _) => _panel = null;
        _panel.Show();
    }

    public void SnapPanel() => _panel?.FollowOwner();

    public void RefreshBadge()
    {
        int count = _store.CountOf(Box);
        CountText.Text = count.ToString();
        BadgeBorder.Opacity = count == 0 ? 0.45 : 1.0;
        // 映射盒的计数可能因为目标目录被外部改动而变化：watcher 兜底，这里顺手补挂
        _watcher?.EnsureStarted();
        if (GridHost.Visibility == Visibility.Visible) RefreshInlineItems();
        RefreshPreview();
    }

    /// <summary>盒目录被外部改动（拷入/删除/改名）后的统一入口。</summary>
    private void OnDirectoryChanged()
    {
        _watcher.EnsureStarted();
        RefreshBadge();
        _panel?.RefreshItems(animate: false);
    }

    /// <summary>
    /// 抑制目录监听。自身造成的文件变动（拖入/拖出/放回桌面/整理）不该反过来触发刷新，
    /// 否则会打断正在进行的拖拽。调用方用 using 包住操作。
    /// </summary>
    public IDisposable SuppressWatcher() => _watcher.Suspend();

    /// <summary>把盒子提到桌面层最前（全局热键「呼出」用）。</summary>
    public void BringToFront()
    {
        if (!IsLoaded) return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOP */, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch
        {
            // 提不上来最多是叠放顺序不理想，不值得打断热键处理
        }
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    // 折叠态内容：缩略图模式下显示最多 3 个文件图标 + 总数；仅标题模式什么都不显示；
    // 空盒与拖拽经过时显示一行提示。
    private void RefreshPreview()
    {
        if (Box.DisplayMode == BoxDisplayMode.Content)
        {
            PreviewHost.Visibility = Visibility.Collapsed;
            PreviewHint.Visibility = Visibility.Collapsed;
            return;
        }

        var names = ListEntries();

        // 拖拽经过：不管盒里有没有东西都给一句"松开即可收纳"，比只有边框变色明确
        if (_dropActive)
        {
            PreviewHost.Visibility = Visibility.Collapsed;
            PreviewHint.Text = "松开鼠标即可收纳";
            PreviewHint.Visibility = Visibility.Visible;
            return;
        }

        if (names.Count == 0)
        {
            PreviewHost.Visibility = Visibility.Collapsed;
            PreviewHint.Text = "拖放文件到这里";
            PreviewHint.Visibility = Visibility.Visible;
            return;
        }

        PreviewHint.Visibility = Visibility.Collapsed;

        if (Box.DisplayMode == BoxDisplayMode.TitleOnly)
        {
            PreviewHost.Visibility = Visibility.Collapsed;
            PreviewIcons.ItemsSource = null;
            return;
        }

        // Preview：按 Box.Order 的顺序取前 3 个
        var ordered = Box.Order.Where(names.Contains)
            .Concat(names.Where(n => !Box.Order.Contains(n)))
            .ToList();
        var display = DisplayNames.Build(ordered);
        var items = ordered.Take(3).Select(n =>
        {
            var path = Path.Combine(SourceDir, n);
            return new InlineItem
            {
                FilePath = path,
                DisplayName = display[n],
                Icon = IconService.GetCached(path),
            };
        }).ToList();

        PreviewMore.Text = $"共 {names.Count} 项";
        PreviewIcons.ItemsSource = items;
        PreviewHost.Visibility = Visibility.Visible;
        _ = LoadInlineIconsAsync(items);
    }

    private List<string> ListEntries()
    {
        var dir = SourceDir;
        if (!Directory.Exists(dir)) return new List<string>();
        try
        {
            return Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Cast<string>()
                .ToList();
        }
        catch
        {
            // 映射到网络盘 / 拔掉的移动硬盘时枚举会抛，界面当作空盒处理
            return new List<string>();
        }
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _dropActive = true;
            ApplyInteractionBrush(IsMouseOver);
            RefreshPreview();
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
        _dropActive = false;
        ApplyInteractionBrush(IsMouseOver);
        RefreshPreview();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _dropActive = false;
        ApplyInteractionBrush(IsMouseOver);
        RefreshPreview();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        MoveIntoBox(files);
    }

    private void MoveIntoBox(string[] files)
    {
        var dir = SourceDir;

        // 映射盒：文件进的是那个"真实存在的目录"，目录不在就先说清楚，别让人以为搬丢了
        if (IsPortal && !Directory.Exists(dir))
        {
            MessageBox.Show(this,
                $"映射的文件夹当前不可用：\n{dir}\n\n可能被改名、删除，或所在磁盘未连接。\n" +
                "可在盒子右键菜单里「更改映射目标…」。",
                "Tonia-DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(dir);
        bool changed = false;

        // 自己搬自己：watcher 收到的那批事件是本次操作造成的，压住别刷
        using (_watcher.Suspend())
        {
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
        }

        if (changed)
        {
            _store.Save(_config);
            RefreshBadge();
            _panel?.RefreshItems();
        }
    }
}
