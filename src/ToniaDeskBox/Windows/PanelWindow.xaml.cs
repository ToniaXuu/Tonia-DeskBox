using System.Collections.Specialized;
using System.IO;
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
    private class PanelItem
    {
        public string FilePath { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public ImageSource? Icon { get; init; }
    }

    private readonly BoxWindow _owner;
    private readonly BoxConfig _box;
    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private Point _dragStart;
    private bool _draggingItem;

    public PanelWindow(BoxWindow owner, BoxConfig box, AppConfig config, ConfigStore store)
    {
        InitializeComponent();
        _owner = owner;
        _box = box;
        _config = config;
        _store = store;

        Root.Background = BoxBrush();
        PositionNextTo(owner);
        RefreshItems();
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

    private void PositionNextTo(BoxWindow owner)
    {
        double x = owner.Left + owner.ActualWidth + 8;
        double y = owner.Top;
        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var wa = WF.Screen.FromHandle(hwnd).WorkingArea;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            if (x + ActualWidth > wa.Right / dpi)
                x = owner.Left - ActualWidth - 8;
            x = Math.Max(x, wa.Left / dpi);
            y = Math.Min(y, (wa.Bottom - ActualHeight) / dpi);
            y = Math.Max(y, wa.Top / dpi);

            Left = x;
            Top = y;
        };
    }

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
                    Icon = ShortcutService.GetIcon(path),
                };
            })
            .ToList();

        Items.ItemsSource = _items;
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _owner.RefreshBadge();
    }

    private List<PanelItem> _items = new();

    private PanelItem? ItemFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as PanelItem;

    private void OnItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _draggingItem = false;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggingItem) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            StartDragOut(sender);
        }
    }

    private void StartDragOut(object sender)
    {
        if (ItemFrom(sender) is not { } item) return;
        _draggingItem = true;

        var data = new DataObject();
        var list = new StringCollection { item.FilePath };
        data.SetFileDropList(list);
        using var ms = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Move));
        data.SetData("Preferred DropEffect", ms);

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data,
                DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _draggingItem = false;
        }

        RefreshItems();
    }

    private void OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingItem) return;
        if (ItemFrom(sender) is { } item)
            ShortcutService.Open(item.FilePath);
    }

    private void OnItemRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;

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

        var miRemove = new MenuItem { Header = "从盒子移除（放回桌面）" };
        miRemove.Click += (_, _) => MoveBackToDesktop(item);
        menu.Items.Add(miRemove);

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void MoveBackToDesktop(PanelItem item)
    {
        var name = Path.GetFileName(item.FilePath);
        FileMover.MoveBackToDesktop(new[] { item.FilePath }, out var skipped);
        if (skipped.Count > 0)
        {
            MessageBox.Show(this, $"桌面已存在同名文件或文件被占用，未能放回：{name}",
                "Tonia-DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _box.Order.Remove(name);
        _store.Save(_config);
        RefreshItems();
    }
}
