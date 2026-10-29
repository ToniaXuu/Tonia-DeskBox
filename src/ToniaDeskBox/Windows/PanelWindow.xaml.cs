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
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;
using WF = System.Windows.Forms;

namespace ToniaDeskBox.Windows;

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

    private List<PanelItem> _items = new();
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

    public PanelWindow(BoxWindow owner, BoxConfig box, AppConfig config, ConfigStore store)
    {
        InitializeComponent();
        _owner = owner;
        _box = box;
        _config = config;
        _store = store;

        PanelTitle.Text = box.Name;
        Root.Background = BoxBrush();
        Owner = owner;
        ContentRendered += (_, _) => UpdatePlacement();
        RefreshItems();

        // 显示前预量尺寸并定位，避免窗口先在默认位置闪现一下
        if (Content is FrameworkElement fe)
        {
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _designSize = fe.DesiredSize;
        }
        UpdatePlacement();
    }

    private Brush BoxBrush()
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(_box.BackColor);
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

    private Size _designSize;

    private void UpdatePlacement()
    {
        // SizeToContent 在 Loaded 时可能还未量出尺寸：用渲染前预量的尺寸兜底
        double w = ActualWidth > 0 ? ActualWidth : _designSize.Width;
        double h = ActualHeight > 0 ? ActualHeight : _designSize.Height;
        if (w <= 0 || h <= 0 || !_owner.IsLoaded) return;

        var hwnd = new WindowInteropHelper(_owner).Handle;
        var wa = WF.Screen.FromHandle(hwnd).WorkingArea;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double waL = wa.Left / dpi, waT = wa.Top / dpi;
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

    public void RefreshItems()
    {
        var dir = _store.BoxDir(_box);
        var names = Directory.Exists(dir)
            ? Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var orderedNames = _box.Order
            .Where(names.Contains)
            .Concat(names.Where(n => !_box.Order.Contains(n)));

        _items = orderedNames
            .Select(n =>
            {
                var path = Path.Combine(dir, n);
                return new PanelItem
                {
                    FilePath = path,
                    DisplayName = n,
                    Icon = IconService.GetCached(path),
                };
            })
            .ToList();

        Items.ItemsSource = _items;
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _owner.RefreshBadge();

        // 图标提取放到后台，面板先出来，图标逐个补齐
        _ = LoadIconsAsync(_items);
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

    // ---------- 全局键盘 / 失焦 ----------

    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 分层：先关菜单 → 再清选中 → 最后收面板
            if (_openMenu?.IsOpen == true)
                _openMenu.IsOpen = false;
            else if (_items.Any(i => i.IsSelected))
                ClearSelection();
            else
                Close();
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
        if (_draggingItem || _sorting || _banding || _suppressDeactivate || _openMenu?.IsOpen == true)
            return;

        // 光标还在面板/盒子上 → 可能是点击盒子切换面板，交给盒子处理
        var p = WF.Cursor.Position;
        if (IsCursorOver(this, p) || IsCursorOver(_owner, p))
            return;

        Close();
    }

    private static bool IsCursorOver(Window w, System.Drawing.Point screenPt)
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

        var miDissolve = new MenuItem { Header = "解散收纳盒（文件放回桌面）" };
        miDissolve.Click += (_, _) => DissolveBox();
        menu.Items.Add(miDissolve);

        var miSort = new MenuItem { Header = "按名称排序" };
        miSort.Click += (_, _) => SortByName();
        menu.Items.Add(miSort);

        _openMenu = menu;
        menu.Closed += (_, _) => _openMenu = null;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void DissolveBox()
    {
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

        FileMover.MoveBackToDesktop(files, out var skipped);
        _box.Order.Clear();
        _store.Save(_config);
        RefreshItems();

        if (skipped.Count > 0)
            ShowMsg($"以下文件因桌面同名/占用未能放回，已保留在盒子目录：\n{string.Join("\n", skipped)}");
    }

    private void SortByName()
    {
        var dir = _store.BoxDir(_box);
        if (!Directory.Exists(dir)) return;

        _box.Order = Directory.EnumerateFileSystemEntries(dir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();
        _store.Save(_config);
        RefreshItems();
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
        if (_visualIndex != _sortIndex && _sortIndex >= 0 && _sortIndex < _items.Count)
        {
            var item = _items[_sortIndex];
            _items.RemoveAt(_sortIndex);
            _items.Insert(Math.Clamp(_visualIndex, 0, _items.Count), item);

            _box.Order = _items.Select(i => i.DisplayName).ToList();
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
            RefreshItems();
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
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data,
                DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _suppressDeactivate = false;
            _draggingItem = false;
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
        FileMover.MoveBackToDesktop(items.Select(i => i.FilePath), out var skipped);

        foreach (var i in items)
            _box.Order.Remove(i.DisplayName);
        _store.Save(_config);
        RefreshItems();

        if (skipped.Count > 0)
            ShowMsg($"桌面已存在同名文件或文件被占用，未能放回：\n{string.Join("\n", skipped)}");
    }
}
