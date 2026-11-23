using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Windows;

/// <summary>
/// 面板的打开方式。
/// - Browse：完整面板（标题 + 网格 + 底部提示），点盒子空白处打开；
/// - Search：只做搜索用的小面板，由折叠态盒子上的放大镜打开，搜索栏常驻。
/// </summary>
public enum PanelOpenMode
{
    Browse = 0,
    Search = 1,
}

public partial class PanelWindow : Window
{
    private class PanelItem : INotifyPropertyChanged
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

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly BoxWindow _owner;
    private readonly BoxConfig _box;
    private readonly AppConfig _config;
    private readonly ConfigStore _store;

    /// <summary>面板排序方式。None = 保持 <c>Box.Order</c> 的收纳顺序（默认）。</summary>
    private enum SortMode { None, Name, Added }

    private List<PanelItem> _items = new();
    // 全量（按 Box.Order 排列）。_items 是它经关键字过滤后的显示集合，
    // 框选/拖拽/右键/打开都只作用在 _items 上。
    private List<PanelItem> _allItems = new();
    private string _filterText = "";
    private SortMode _sortMode = SortMode.None;
    private bool _searchOpen;
    private readonly PanelOpenMode _openMode;
    // 被别的卡片压住时把表面调成全不透明；记一个状态位，避免每 200ms 重刷笔刷
    private bool _opaqueForOverlap;
    private DispatcherTimer? _overlapTimer;
    private ContextMenu? _openMenu;
    private bool _suppressDeactivate;

    private Point _dragStart;
    private bool _draggingItem;
    private bool _actionTaken;

    // 面板内排序
    private bool _sorting;
    private int _sortIndex;
    private int _visualIndex;
    private Rect[] _origRects = Array.Empty<Rect>();
    private FrameworkElement? _sortCell;

    // 橡皮筋框选
    private bool _banding;
    private bool _bandMoved;
    private Point _bandStart;
    private HashSet<PanelItem> _bandBase = new();

    private const double DragThreshold = 10;
    private const double BandThreshold = 6;
    private const double DragOutMargin = 14;

    private readonly AppearanceTheme _theme;
    private bool _dropActive;
    private bool _openAnimated;
    // 从面板内拖出文件期间，不要把面板自己的 DragOver 当成外部拖入
    private bool _internalDrag;

    public PanelWindow(BoxWindow owner, BoxConfig box, AppConfig config, ConfigStore store,
        PanelOpenMode mode = PanelOpenMode.Browse)
    {
        InitializeComponent();
        // 必须在 InitializeComponent() 之后挂：XAML 有自己的 Window.Resources，
        // 提前挂会被加载过程替换掉
        _theme = AppearanceTheme.Attach(this);
        _owner = owner;
        _box = box;
        _config = config;
        _store = store;
        _openMode = mode;

        PanelTitle.Text = box.Name;
        ApplyTheme();
        Owner = owner;
        RefreshItems();

        // 显示前预量尺寸并定位，避免窗口先在默认位置闪现一下
        if (Content is FrameworkElement fe)
        {
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _designSize = fe.DesiredSize;
        }

        // —— 消除「打开瞬间闪一下」的关键一步 ——
        // 首帧在窗口 Show 后立刻就被 DWM 合成到屏幕上，而 ContentRendered 是在
        // 「首帧已经画完」之后才触发的。原实现把「透明化 + 定位」都放在 ContentRendered 里，
        // 于是第一帧必然是「默认位置 + 完全不透明」，看起来就是先闪一下再归位。
        // 因此这里在 Show 之前就把内容设为不可见、位移预置好。
        OuterGrid.Opacity = 0;
        OuterGrid.RenderTransform = new TranslateTransform(12, -6);
        WindowStartupLocation = WindowStartupLocation.Manual;

        SourceInitialized += (_, _) =>
        {
            // 分层窗口不显式把 DWM 背景设成透明时，首帧会先铺一层不透明白底
            // （这就是截图里那块白色矩形的来源）。
            var hwnd = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
                target.BackgroundColor = Colors.Transparent;

            // HWND 已建、窗口尚未显示：此刻定位才是真正意义上的「一次到位」
            UpdatePlacement();
        };

        if (_openMode == PanelOpenMode.Search)
        {
            // 搜索态面板只干一件事：找东西。放大镜与「更多」在这里是冗余的，
            // 底部那条"拖入收纳"提示也不是搜索场景的文案
            SearchButton.Visibility = Visibility.Collapsed;
            MoreButton.Visibility = Visibility.Collapsed;
            FooterHint.Visibility = Visibility.Collapsed;
            SearchBar.Visibility = Visibility.Visible;
            _searchOpen = true;
        }

        ContentRendered += (_, _) =>
        {
            // SizeToContent 下实际尺寸此时才定稿，再校一次位置（此时内容仍不可见）
            UpdatePlacement();
            PlayOpenAnimation();

            UpdateOverlapOpacity();

            // 键盘焦点必须等首帧画完再抢：提前 Focus 会被窗口激活流程盖掉
            if (_openMode == PanelOpenMode.Search)
            {
                SearchBox.Focus();
                QueuePlacement();
            }
        };

        UpdatePlacement();
        StartOverlapWatch();
    }

    /// <summary>
    /// 面板与别的卡片重叠时转成不透明。
    ///
    /// 用定时器而不是事件：盒子可以被拖到任意位置、别的面板也可能挪过来，
    /// 逐个订阅各窗口的 LocationChanged 覆盖面还不如每 200ms 量一次矩形来得可靠，
    /// 而且这里只是几次 Rect 求交，代价可以忽略。
    /// </summary>
    private void StartOverlapWatch()
    {
        _overlapTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _overlapTimer.Tick += (_, _) => UpdateOverlapOpacity();
        _overlapTimer.Start();
        Closed += (_, _) => _overlapTimer?.Stop();
    }

    private void UpdateOverlapOpacity()
    {
        bool overlap = OverlapsOtherWindow();
        if (overlap == _opaqueForOverlap) return;
        _opaqueForOverlap = overlap;
        ApplyTheme();
    }

    private bool OverlapsOtherWindow()
    {
        if (!IsLoaded || !IsVisible) return false;

        // 外圈 18px 是留给投影的透明边，被它擦到不算"压住"
        const double margin = 18;
        double w = ActualWidth - margin * 2;
        double h = ActualHeight - margin * 2;
        if (w <= 0 || h <= 0) return false;
        var self = new Rect(Left + margin, Top + margin, w, h);

        foreach (Window win in Application.Current.Windows)
        {
            if (ReferenceEquals(win, this) || !win.IsVisible) continue;
            if (win.WindowState == WindowState.Minimized) continue;

            var other = new Rect(win.Left, win.Top, win.ActualWidth, win.ActualHeight);
            var hit = Rect.Intersect(self, other);
            // 只压住几像素（圆角、阴影贴边）不该让面板突然变实
            if (!hit.IsEmpty && hit.Width > 6 && hit.Height > 6) return true;
        }
        return false;
    }

    private void ApplyTheme()
    {
        // 与盒子共用同一套 Theme.* 笔刷：白底自动切深色前景。
        // 分类色也走 Theme.Accent（每次 Update 换新笔刷对象），
        // 不再原地改 XAML 资源里的 brush —— 它可能已被 WPF 冻结。
        // 被压住时抬到全不透明：两层半透明叠在一起，谁的字都读不清
        _theme.Update(_box, _opaqueForOverlap ? 1.0 : 0);

        double r = _box.Rounded ? Math.Clamp(_box.CornerSize + 2, 10, 18) : 2;
        Root.CornerRadius = new CornerRadius(r);
        ShadowCaster.CornerRadius = new CornerRadius(r);
    }

    /// <summary>盒子设置改动后让已打开的面板跟随刷新（底色 / 圆角 / 前景对比色）。</summary>
    public void RefreshTheme() => ApplyTheme();

    // 失焦 / Esc / × / 点盒子切换可能在关闭过程中再次触发关闭；
    // WPF 在 Closing 期间再调 Close() 会抛 InvalidOperationException（会弹全局错误框）。
    private bool _closing;

    public void ClosePanel()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); }
        catch (InvalidOperationException) { }
    }

    // ---------- 打开动效 ----------

    private void PlayOpenAnimation()
    {
        if (_openAnimated) return;
        _openAnimated = true;

        // 初始的「透明 + 位移」已在构造函数里预置好了（首帧就必须是不可见的），
        // 这里只负责播放。若 RenderTransform 被意外替换，则兜底重建一次。
        if (OuterGrid.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform(12, -6);
            OuterGrid.RenderTransform = tt;
            OuterGrid.Opacity = 0;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(220);
        tt.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(12, 0, dur) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-6, 0, dur) { EasingFunction = ease });
        OuterGrid.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private void AnimateItems()
    {
        if (_items.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (Items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe)
                    continue;
                fe.Opacity = 0;
                fe.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(i * 40),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
            }
        }));
    }

    // ---------- 拖入高亮与收纳 ----------

    private void ApplyDropVisual(bool active)
    {
        _dropActive = active;
        if (active)
        {
            Root.BorderBrush = _theme.BrushOf(ThemeKeys.BorderDrop);
            FooterHint.Text = "松开鼠标即可收纳";
            FooterHint.Foreground = _theme.BrushOf(ThemeKeys.Accent);
        }
        else
        {
            Root.BorderBrush = _theme.BrushOf(ThemeKeys.Border);
            FooterHint.Text = "拖入收纳 · 拖出放回桌面";
            FooterHint.Foreground = _theme.BrushOf(ThemeKeys.TextMuted);
        }
    }

    private void OnPanelDragEnter(object sender, DragEventArgs e)
    {
        if (_internalDrag || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        ApplyDropVisual(true);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnPanelDragOver(object sender, DragEventArgs e)
    {
        if (_internalDrag || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        // DragLeave 穿过子元素时会反复触发，DragOver 幂等地把高亮补回来
        if (!_dropActive) ApplyDropVisual(true);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnPanelDragLeave(object sender, DragEventArgs e) => ApplyDropVisual(false);

    private void OnPanelDrop(object sender, DragEventArgs e)
    {
        ApplyDropVisual(false);
        if (_internalDrag) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var dir = _store.SourceDir(_box);
        if (ConfigStore.IsPortal(_box))
        {
            // 映射盒写的是别人的目录：不替它创建，路径不在就直说
            if (!Directory.Exists(dir))
            {
                ShowMsg($"映射的文件夹当前不可用：\n{dir}");
                e.Handled = true;
                return;
            }
        }
        else
        {
            Directory.CreateDirectory(dir);
        }

        // 自己搬自己：watcher 收到的事件不该反过来重建列表
        using (_owner.SuppressWatcher())
        {
            foreach (var src in paths)
            {
                var name = Path.GetFileName(src);
                if (string.IsNullOrEmpty(name)) continue;
                var target = Path.Combine(dir, name);
                if (FileMover.ExistsConflict(target)) continue;
                try { FileMover.MoveTo(src, target); }
                catch { }
            }
        }
        _store.Save(_config);
        RefreshItems();
        // 拖入后条数变了，这条必须无条件刷
        _owner.RefreshBadge();
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => ClosePanel();

    private Size _designSize;

    private void UpdatePlacement()
    {
        // SizeToContent 在 Loaded 时可能还未量出尺寸：用渲染前预量的尺寸兜底
        double w = ActualWidth > 0 ? ActualWidth : _designSize.Width;
        double h = ActualHeight > 0 ? ActualHeight : _designSize.Height;
        if (w <= 0 || h <= 0 || !_owner.IsLoaded) return;

        var hwnd = new WindowInteropHelper(_owner).Handle;
        var wa = ScreenUtil.WorkAreaPx(hwnd);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double waL = wa.X / dpi, waT = wa.Y / dpi;
        double waR = wa.Right / dpi, waB = wa.Bottom / dpi;
        const double M = 18; // 窗口外圈投影边距

        // 水平：默认盒子右侧，越界翻到左侧
        double x = _owner.Left + _owner.ActualWidth;
        if (x + w - M > waR)
            x = _owner.Left - w + M;
        x = ClampSafe(x, waL - M, waR - w + M);

        // 垂直：默认与盒子顶对齐；下方放不下则弹到盒子上方
        double y = _owner.Top;
        if (y + h - M > waB)
            y = _owner.Top - h + M - 6;
        y = ClampSafe(y, waT - M, waB - h);

        Left = x;
        Top = y;
    }

    // 面板比工作区还宽/高时 min>max 会让 Math.Clamp 抛异常，这里退化为贴左/贴上
    private static double ClampSafe(double v, double min, double max) =>
        min > max ? min : Math.Clamp(v, min, max);

    public void FollowOwner() => UpdatePlacement();

    public void RefreshItems(bool animate = true)
    {
        var dir = _store.SourceDir(_box);
        var names = Directory.Exists(dir)
            ? Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var orderedNames = _box.Order
            .Where(names.Contains)
            .Concat(names.Where(n => !_box.Order.Contains(n)))
            .ToList();

        // .lnk 去掉扩展名显示（会被挤成两行），重名时自动保留全名
        var display = DisplayNames.Build(orderedNames);

        _allItems = orderedNames
            .Select(n =>
            {
                var path = Path.Combine(dir, n);
                return new PanelItem
                {
                    FilePath = path,
                    DisplayName = display[n],
                    Icon = IconService.GetCached(path),
                };
            })
            .ToList();

        // 宽度基线只在「重新扫描目录」时确定。搜索过滤时刻意不动它，
        // 否则每敲一个字面板就变宽变窄一次，位置也跟着重算，视觉上很晃。
        // 宽度必须是「列数 × 单元格宽(96)」的整数倍：206/300/400 这类整数
        // 会多出十几像素全堆在右侧，最后一行看起来就是左对齐、右边空一块。
        BandHost.Width = ColumnsFor(_allItems.Count) * 96;

        BindVisible(animate);
    }

    /// <summary>按数量选一个不出现「3+1」这种残行的列数。</summary>
    private static int ColumnsFor(int count) => count switch
    {
        <= 1 => 1,
        2 => 2,
        3 => 3,
        4 => 2,      // 2×2 比 3+1 稳
        <= 6 => 3,
        _ => 4,
    };

    /// <summary>
    /// 按当前关键字把 <see cref="_allItems"/> 过滤成 <see cref="_items"/> 并绑定。
    /// 只动「显示集合」，不碰文件、不碰 <c>Box.Order</c>：
    /// 框选、拖拽、右键、打开都基于 <see cref="_items"/>，所以筛选后自动只作用于可见项。
    /// </summary>
    private void BindVisible(bool animate)
    {
        var key = _filterText.Trim();

        _items = key.Length == 0
            ? _allItems.ToList()
            : _allItems
                .Where(i => i.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase))
                .ToList();

        Items.ItemsSource = _items;

        // 搜索时显示「命中/总数」，一眼能看出被筛掉了多少
        PanelCount.Text = key.Length == 0
            ? _allItems.Count.ToString()
            : $"{_items.Count}/{_allItems.Count}";

        bool empty = _items.Count == 0;
        EmptyCard.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ItemScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Text = key.Length == 0
            ? "把桌面图标拖进来收纳"
            : $"没有匹配「{key}」的图标";

        // 虚线拖入位只在「整盒恰好 1 项」时补位；搜索结果不补，否则会挤乱网格
        bool showSlot = _items.Count == 1 && key.Length == 0;
        DropSlot.Visibility = showSlot ? Visibility.Visible : Visibility.Collapsed;
        if (showSlot) DropSlot.Margin = new Thickness(96, 4, 0, 0);

        // 只有「没在筛选」时才刷盒子：搜索时总条数没变，
        // 每敲一个字都去重扫目录、重建缩略图没有意义（大目录会明显卡）
        if (key.Length == 0) _owner.RefreshBadge();

        // 图标提取放到后台，面板先出来，图标逐个补齐
        _ = LoadIconsAsync(_items);
        if (animate) AnimateItems();
    }

    private async Task LoadIconsAsync(List<PanelItem> items)
    {
        foreach (var item in items)
        {
            ImageSource? icon;
            try { icon = await IconService.GetAsync(item.FilePath); }
            catch { continue; }
            if (icon != null) item.Icon = icon;
        }
    }

    // ---------- 搜索 ----------

    /// <summary>盒子标题栏上的放大镜：面板已开时就地展开搜索栏。</summary>
    public void ShowSearchBar()
    {
        if (_searchOpen)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }
        OpenSearch();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (_searchOpen) CloseSearch();
        else OpenSearch();
    }

    private void OpenSearch()
    {
        if (_searchOpen) return;
        _searchOpen = true;
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Text = _filterText;   // 再次打开时保留上次的关键字
        SyncSearchPlaceholder();
        SearchBox.Focus();
        SearchBox.SelectAll();
        QueuePlacement();
    }

    private void CloseSearch()
    {
        if (!_searchOpen) return;
        _searchOpen = false;
        _filterText = "";
        SearchBox.Text = "";
        SearchBar.Visibility = Visibility.Collapsed;
        SyncSearchPlaceholder();
        BindVisible(animate: false);
        QueuePlacement();
        // 焦点必须离开输入框，否则 Ctrl+A / Esc 会被 TextBox 吃掉
        Keyboard.ClearFocus();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = SearchBox.Text;
        SyncSearchPlaceholder();
        // 关搜索时也会触发一次 TextChanged，那次由 CloseSearch 统一处理
        if (_searchOpen) BindVisible(animate: false);
    }

    private void OnSearchClearClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void SyncSearchPlaceholder() =>
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    // 收起/展开搜索栏会改变窗口高度（SizeToContent），要等布局定稿再重新贴靠盒子，
    // 否则面板会先按旧高度定位、再跳一下
    private void QueuePlacement() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePlacement));

    // ---------- 全局键盘 / 失焦 ----------

    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 分层：先退搜索 → 再关菜单 → 再清选中 → 最后收面板
            if (_searchOpen)
                CloseSearch();
            else if (_openMenu?.IsOpen == true)
                _openMenu.IsOpen = false;
            else if (_items.Any(i => i.IsSelected))
                ClearSelection();
            else
                ClosePanel();
            e.Handled = true;
            return;
        }

        // 焦点在搜索框里时，其余快捷键（含 Ctrl+A 全选文字）都交给输入框
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OpenSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            foreach (var i in _items) i.IsSelected = true;
            e.Handled = true;
        }
    }

    private void OnPanelDeactivated(object? sender, EventArgs e)
    {
        if (_draggingItem || _sorting || _banding || _dropActive || _suppressDeactivate || _openMenu?.IsOpen == true)
            return;

        // 光标还在面板/盒子上 → 可能是点击盒子切换面板，交给盒子处理
        var p = ScreenUtil.CursorPosition();
        if (IsCursorOver(this, p) || IsCursorOver(_owner, p))
            return;

        ClosePanel();
    }

    private static bool IsCursorOver(Window w, PixelPoint screenPt)
    {
        if (!w.IsLoaded || w.Visibility != Visibility.Visible) return false;
        try
        {
            var tl = w.PointToScreen(new Point(0, 0));
            var br = w.PointToScreen(new Point(w.ActualWidth, w.ActualHeight));
            return screenPt.X >= tl.X && screenPt.X <= br.X &&
                   screenPt.Y >= tl.Y && screenPt.Y <= br.Y;
        }
        catch
        {
            return false;
        }
    }

    private void ShowMsg(string text, MessageBoxImage icon = MessageBoxImage.Warning)
    {
        _suppressDeactivate = true;
        try
        {
            MessageBox.Show(this, text, "Tonia-DeskBox", MessageBoxButton.OK, icon);
        }
        finally
        {
            _suppressDeactivate = false;
        }
    }

    // ---------- 面板"⋯"菜单 ----------

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AppearanceTheme.AttachMenu(menu, _box, _config.Settings.MenuOpacity, this);

        // 映射盒的"解散"语义不成立（文件不在本程序手里），干脆不提供这一项
        if (!ConfigStore.IsPortal(_box))
        {
            var miDissolve = new MenuItem { Header = "解散收纳盒（文件放回桌面）" };
            miDissolve.Click += (_, _) => DissolveBox();
            menu.Items.Add(miDissolve);

            menu.Items.Add(new Separator());
        }

        var miSortName = new MenuItem
        {
            Header = "按名称排序",
            IsCheckable = true,
            IsChecked = _sortMode == SortMode.Name,
        };
        miSortName.Click += (_, _) => ApplySort(SortMode.Name);
        menu.Items.Add(miSortName);

        var miSortAdded = new MenuItem
        {
            Header = "按放入时间排序",
            IsCheckable = true,
            IsChecked = _sortMode == SortMode.Added,
        };
        miSortAdded.Click += (_, _) => ApplySort(SortMode.Added);
        menu.Items.Add(miSortAdded);

        _openMenu = menu;
        menu.Closed += (_, _) => _openMenu = null;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void DissolveBox()
    {
        // 映射盒没有"把文件放回桌面"这一步：文件本来就不在本程序手里，
        // 强行搬到桌面等于替用户搬走别人的文件夹内容
        if (ConfigStore.IsPortal(_box))
        {
            MessageBox.Show(this,
                $"这个盒子映射的是已有文件夹，不会把里面的文件搬到桌面。\n\n" +
                $"如需移除，请在盒子右键菜单里选「移除这个映射盒子」。\n\n{_store.SourceDir(_box)}",
                "映射盒子", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dir = _store.BoxDir(_box);
        if (!Directory.Exists(dir)) return;

        var files = Directory.EnumerateFileSystemEntries(dir).ToList();
        if (files.Count == 0)
        {
            ShowMsg("盒子已经是空的。", MessageBoxImage.Information);
            return;
        }

        _suppressDeactivate = true;
        var result = MessageBox.Show(this,
            $"将把 {files.Count} 个文件全部放回桌面，盒子保留并清空。\n\n确定解散？",
            "解散收纳盒", MessageBoxButton.YesNo, MessageBoxImage.Question);
        _suppressDeactivate = false;
        if (result != MessageBoxResult.Yes) return;

        List<string> skipped;
        using (_owner.SuppressWatcher())
        {
            FileMover.MoveBackToDesktop(files, out skipped);
        }
        _box.Order.Clear();
        _store.Save(_config);
        RefreshItems();

        if (skipped.Count > 0)
            ShowMsg($"以下文件因桌面同名/占用未能放回，已保留在盒子目录：\n{string.Join("\n", skipped)}");
    }

    /// <summary>
    /// 重排并写回 <c>Box.Order</c>。
    /// - <see cref="SortMode.Name"/>：按显示名（已去掉 .lnk）的当前区域文化顺序；
    /// - <see cref="SortMode.Added"/>：按放入时间，**新放入的在前**。
    ///   <c>Box.Order</c> 本身就是放入顺序（拖入时追加到末尾），所以越靠后越新；
    ///   不在 Order 里的（直接拷进盒子目录的文件）一律视为最新。
    /// </summary>
    private void ApplySort(SortMode mode)
    {
        var dir = _store.SourceDir(_box);
        if (!Directory.Exists(dir)) return;

        var names = Directory.EnumerateFileSystemEntries(dir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToList();

        List<string> ordered;
        if (mode == SortMode.Name)
        {
            var display = DisplayNames.Build(names);
            ordered = names.OrderBy(n => display[n], StringComparer.CurrentCulture).ToList();
        }
        else
        {
            var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _box.Order.Count; i++)
                pos[_box.Order[i]] = i;

            ordered = names
                .OrderByDescending(n => pos.TryGetValue(n, out var i) ? i : int.MaxValue)
                .ThenBy(n => n, StringComparer.CurrentCulture)
                .ToList();
        }

        _sortMode = mode;
        _box.Order = ordered;
        _store.Save(_config);
        RefreshItems(animate: false);
    }

    // ---------- 选中 / 框选 ----------

    private void ClearSelection()
    {
        foreach (var i in _items) i.IsSelected = false;
    }

    private List<PanelItem> SelectedItems() => _items.Where(i => i.IsSelected).ToList();

    private Rect[] ContainerRectsRelativeTo(UIElement anchor)
    {
        var rects = new Rect[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            rects[i] = Rect.Empty;
            if (Items.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe &&
                fe.IsLoaded)
            {
                var p = fe.TranslatePoint(new Point(0, 0), anchor);
                rects[i] = new Rect(p, fe.RenderSize);
            }
        }
        return rects;
    }

    private void OnBandMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_sorting || _draggingItem) return;
        _bandBase = _items.Where(i => i.IsSelected).ToHashSet();
        _bandStart = e.GetPosition(BandHost);
        _banding = true;
        _bandMoved = false;
        BandHost.CaptureMouse();
    }

    private void OnBandMouseMove(object sender, MouseEventArgs e)
    {
        if (!_banding || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(BandHost);
        if (!_bandMoved)
        {
            if (Dist(pos, _bandStart) <= BandThreshold) return;
            _bandMoved = true;
        }

        var rect = RectFromPoints(_bandStart, pos);
        Band.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
        Band.Width = rect.Width;
        Band.Height = rect.Height;
        Band.Visibility = Visibility.Visible;

        bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var rects = ContainerRectsRelativeTo(BandHost);
        for (int i = 0; i < _items.Count; i++)
        {
            bool hit = !rects[i].IsEmpty && rects[i].IntersectsWith(rect);
            _items[i].IsSelected = additive ? (_bandBase.Contains(_items[i]) || hit) : hit;
        }
    }

    private void OnBandMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_banding) return;
        _banding = false;
        BandHost.ReleaseMouseCapture();
        Band.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (_bandMoved) return;
        // 空白处单击 = 取消全选
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ClearSelection();
    }

    // ---------- 图标：单击 / 拖动排序 / 拖出 ----------

    private PanelItem? ItemFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as PanelItem;

    private static double Dist(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static Rect RectFromPoints(Point a, Point b)
    {
        return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private void OnItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _dragStart = e.GetPosition(this);
        _draggingItem = false;
        _actionTaken = false;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggingItem || _banding) return;

        var cell = (FrameworkElement)sender;
        var pos = e.GetPosition(this);

        if (_sorting)
        {
            UpdateSort(pos);
            return;
        }

        if (Dist(pos, _dragStart) <= DragThreshold) return;

        // 越出面板窗口 → 切换为拖出到桌面/资源管理器
        if (pos.X < -DragOutMargin || pos.X > ActualWidth + DragOutMargin ||
            pos.Y < -DragOutMargin || pos.Y > ActualHeight + DragOutMargin)
        {
            cell.ReleaseMouseCapture();
            StartDragOut(sender);
            return;
        }

        if (_items.Count > 1)
            StartSort(cell);
        else
            cell.CaptureMouse();
    }

    private void StartSort(FrameworkElement cell)
    {
        if (ItemFrom(cell) is not { } item) return;
        // 搜索过滤期间只显示命中子集，拖拽重排无法安全写回全量顺序，直接不进入排序
        if (_filterText.Trim().Length > 0) return;
        _sorting = true;
        _sortIndex = _items.IndexOf(item);
        _visualIndex = _sortIndex;
        _sortCell = cell;
        _origRects = ContainerRectsRelativeTo(this);
        cell.CaptureMouse();
        cell.LostMouseCapture += OnSortLostCapture;
    }

    private void OnSortLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe) fe.LostMouseCapture -= OnSortLostCapture;
        if (!_sorting) return;
        _sorting = false;
        ClearSortTransforms();
    }

    private void UpdateSort(Point pos)
    {
        if (_sortCell == null || _sortIndex < 0 || _sortIndex >= _origRects.Length) return;

        // 拖出面板 → 取消排序并转为拖出
        if (pos.X < -DragOutMargin || pos.X > ActualWidth + DragOutMargin ||
            pos.Y < -DragOutMargin || pos.Y > ActualHeight + DragOutMargin)
        {
            CancelSort();
            if (_sortCell != null)
            {
                var cell = _sortCell;
                _sortCell = null;
                cell.ReleaseMouseCapture();
                StartDragOut(cell);
            }
            return;
        }

        double dx = pos.X - _dragStart.X;
        double dy = pos.Y - _dragStart.Y;
        SetTransform(_sortCell, dx, dy);

        // 被拖格中心落在哪一格 → 目标位
        var src = _origRects[_sortIndex];
        var center = new Point(
            src.Left + src.Width / 2 + dx,
            src.Top + src.Height / 2 + dy);

        int target = _visualIndex;
        for (int i = 0; i < _origRects.Length; i++)
        {
            if (!_origRects[i].IsEmpty && _origRects[i].Contains(center))
            {
                target = i;
                break;
            }
        }
        if (target == _visualIndex) return;

        // 让位：之间的格子整体平移一格（按原始布局矩形差值，天然支持换行）
        if (target > _visualIndex)
        {
            for (int j = _visualIndex + 1; j <= target; j++)
                ShiftContainer(j, _origRects[j - 1].TopLeft - _origRects[j].TopLeft);
        }
        else
        {
            for (int j = target; j < _visualIndex; j++)
                ShiftContainer(j, _origRects[j + 1].TopLeft - _origRects[j].TopLeft);
        }
        SetTransform(_sortCell, dx, dy);
        _visualIndex = target;
    }

    private void ShiftContainer(int index, Vector offset)
    {
        if (Items.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement fe)
            SetTransform(fe, offset.X, offset.Y);
    }

    private static void SetTransform(FrameworkElement fe, double dx, double dy)
    {
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
        {
            fe.RenderTransform = null;
            return;
        }
        fe.RenderTransform = new TranslateTransform(dx, dy);
    }

    private void ClearSortTransforms()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (Items.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                fe.RenderTransform = null;
        }
    }

    private void CancelSort()
    {
        _sorting = false;
        ClearSortTransforms();
    }

    private void CommitSort()
    {
        // 筛选状态下 _items 只是子集，用它重建全量顺序会丢掉未匹配的项
        if (_filterText.Trim().Length > 0) return;
        if (_visualIndex != _sortIndex && _sortIndex >= 0 && _sortIndex < _items.Count)
        {
            var item = _items[_sortIndex];
            _items.RemoveAt(_sortIndex);
            _items.Insert(Math.Clamp(_visualIndex, 0, _items.Count), item);

            // Order 存的是真实文件名；DisplayName 可能是去掉 .lnk 的显示名，不能写进去
            _box.Order = _items.Select(i => Path.GetFileName(i.FilePath)).ToList();
            _store.Save(_config);
        }
    }

    private void OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_sorting)
        {
            CommitSort();
            _actionTaken = true;
            CancelSort();
            _sortCell?.ReleaseMouseCapture();
            _sortCell = null;
            RefreshItems(animate: false);
            return;
        }

        if (_draggingItem || _actionTaken) return;

        if (ItemFrom(sender) is not { } item) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            item.IsSelected = !item.IsSelected;
            _actionTaken = true;
            return;
        }

        ClearSelection();
        ShortcutService.Open(item.FilePath);
    }

    private void StartDragOut(object sender)
    {
        if (ItemFrom(sender) is not { } item) return;
        _draggingItem = true;
        _actionTaken = true;
        _internalDrag = true;

        // 拖动的是选中集内一项 → 整批拖出
        var sel = SelectedItems();
        var paths = (sel.Count > 0 && sel.Contains(item))
            ? sel.Select(i => i.FilePath).ToList()
            : new List<string> { item.FilePath };

        var data = new DataObject();
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        data.SetFileDropList(list);
        using var ms = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Move));
        data.SetData("Preferred DropEffect", ms);

        _suppressDeactivate = true;
        using (_owner.SuppressWatcher())
        {
            try
            {
                DragDrop.DoDragDrop((DependencyObject)sender, data,
                    DragDropEffects.Move | DragDropEffects.Copy);
            }
            finally
            {
                _suppressDeactivate = false;
                _draggingItem = false;
                _internalDrag = false;
            }
        }

        RefreshItems();
    }

    // ---------- 右键菜单 ----------

    private void OnItemRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;

        // 点到未选中的图标 → 只选它；点到已选中 → 保持整批
        if (!item.IsSelected)
        {
            ClearSelection();
            item.IsSelected = true;
        }
        var sel = SelectedItems();
        bool multi = sel.Count > 1;

        var menu = new ContextMenu();
        AppearanceTheme.AttachMenu(menu, _box, _config.Settings.MenuOpacity, this);

        var miOpen = new MenuItem { Header = multi ? $"打开（{sel.Count}）" : "打开" };
        miOpen.Click += (_, _) =>
        {
            foreach (var i in sel) ShortcutService.Open(i.FilePath);
        };
        menu.Items.Add(miOpen);

        if (!multi)
        {
            var miLoc = new MenuItem { Header = "打开所在位置" };
            miLoc.Click += (_, _) => ShortcutService.OpenContainingFolder(item.FilePath);
            menu.Items.Add(miLoc);
        }

        if (!multi)
        {
            var miProp = new MenuItem { Header = "属性" };
            miProp.Click += (_, _) => ShortcutService.ShowProperties(item.FilePath);
            menu.Items.Add(miProp);
        }

        menu.Items.Add(new Separator());

        var miBack = new MenuItem { Header = multi ? $"放回桌面（{sel.Count}）" : "放回桌面" };
        miBack.Click += (_, _) => MoveBackToDesktop(sel);
        menu.Items.Add(miBack);

        _openMenu = menu;
        menu.Closed += (_, _) => _openMenu = null;
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void MoveBackToDesktop(List<PanelItem> items)
    {
        if (items.Count == 0) return;
        List<string> skipped;
        using (_owner.SuppressWatcher())
        {
            FileMover.MoveBackToDesktop(items.Select(i => i.FilePath), out skipped);
        }

        foreach (var i in items)
            _box.Order.Remove(Path.GetFileName(i.FilePath));
        _store.Save(_config);
        RefreshItems();

        if (skipped.Count > 0)
            ShowMsg($"桌面已存在同名文件或文件被占用，未能放回：\n{string.Join("\n", skipped)}");
    }
}
