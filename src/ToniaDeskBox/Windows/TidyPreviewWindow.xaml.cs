using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Windows;

/// <summary>
/// 一键整理预览窗：先给清单、逐条可勾选，再动手。
///
/// 设计取舍：
/// - **默认全勾**：这个动作的价值是"装完立刻见效"，全不勾等于没做；
/// - **分组标题也是勾选框**：12 个快捷方式里只想留 3 个时，先整组取消再点 3 个最快；
/// - **计数实时更新**：底部按钮上方的"已勾选 N 项 / 共 X MB"是唯一的执行口径，
///   用户能在按下按钮之前就知道会搬多少东西；
/// - **搬运期间的遮罩可取消**：拖动大目录到另一块盘时是复制+删除，可能几分钟，
///   必须留一个出口。
/// </summary>
public partial class TidyPreviewWindow : Window
{
    private readonly List<TidyGroup> _groups;
    private readonly ConfigStore _store;
    private readonly AppConfig _config;
    private readonly Func<string, BoxConfig> _ensureBox;

    private readonly Dictionary<TidyItem, CheckBox> _itemBoxes = new();
    private readonly List<(TidyGroup Group, CheckBox Box, TextBlock Count)> _groupRows = new();

    private CancellationTokenSource? _cts;
    private bool _syncing;

    public bool Applied { get; private set; }
    public TidyResult? Result { get; private set; }

    /// <param name="ensureBox">按名字拿到盒子（不存在就创建）。创建窗口 / 写配置的职责留在 App。</param>
    public TidyPreviewWindow(
        List<TidyGroup> groups, ConfigStore store, AppConfig config,
        Func<string, BoxConfig> ensureBox)
    {
        InitializeComponent();
        _groups = groups;
        _store = store;
        _config = config;
        _ensureBox = ensureBox;

        var (back, opacity) = ResolveAppearance(config);
        AppearanceTheme.Attach(this).Update(
            ThemeService.ResolveColor(back), opacity, "一键整理", minAlpha: 0.96);

        BuildGroups();
        UpdateSummary();
    }

    private static (string BackColor, double Opacity) ResolveAppearance(AppConfig? config)
    {
        if (config?.Settings.Global is { } g) return (g.BackColor, g.Opacity);
        if (config?.Boxes.FirstOrDefault() is { } b) return (b.BackColor, b.Opacity);
        return (AppearanceTheme.DefaultBackColor, AppearanceTheme.DefaultOpacity);
    }

    private void BuildGroups()
    {
        GroupList.Children.Clear();

        if (_groups.Count == 0)
        {
            EmptyText.Text = "桌面上没有可整理的条目（可能已经空了，或者只有 desktop.ini 这类系统文件）。";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        foreach (var group in _groups)
        {
            var container = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };

            // 组标题行：勾选框 + 分类名 + 计数 + 目标盒子
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var groupCheck = new CheckBox
            {
                Style = (Style)FindResource("ModernCheck"),
                IsChecked = true,
                Content = group.Title,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            groupCheck.Click += (_, _) => ToggleGroup(group, groupCheck.IsChecked == true);
            header.Children.Add(groupCheck);

            var count = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(ThemeKeys.TextMuted),
            };
            Grid.SetColumn(count, 1);
            header.Children.Add(count);

            var target = new TextBlock
            {
                Text = $"→ 盒子「{group.TargetBoxName}」",
                FontSize = 11,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(ThemeKeys.Accent),
            };
            Grid.SetColumn(target, 2);
            header.Children.Add(target);

            container.Children.Add(header);

            // 条目：换行排列，一行放不下自动折行，长文件名省略但 ToolTip 给全名
            var wrap = new WrapPanel { Margin = new Thickness(26, 6, 0, 0) };
            foreach (var item in group.Items)
            {
                var cb = new CheckBox
                {
                    Style = (Style)FindResource("ModernCheck"),
                    IsChecked = item.IsSelected,
                    Margin = new Thickness(0, 0, 14, 4),
                    MaxWidth = 190,
                };
                cb.Content = new TextBlock
                {
                    Text = item.Name,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = item.Path,
                    Foreground = (Brush)FindResource(ThemeKeys.TextSecondary),
                };
                cb.Checked += (_, _) => OnItemToggled(item, cb, true);
                cb.Unchecked += (_, _) => OnItemToggled(item, cb, false);

                _itemBoxes[item] = cb;
                wrap.Children.Add(cb);
            }

            container.Children.Add(wrap);
            GroupList.Children.Add(container);
            _groupRows.Add((group, groupCheck, count));
        }
    }

    private void ToggleGroup(TidyGroup group, bool selected)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            group.SetAll(selected);
            foreach (var item in group.Items)
            {
                if (_itemBoxes.TryGetValue(item, out var cb)) cb.IsChecked = selected;
            }
        }
        finally
        {
            _syncing = false;
        }
        UpdateSummary();
    }

    private void OnItemToggled(TidyItem item, CheckBox box, bool selected)
    {
        item.IsSelected = selected;
        if (_syncing) return;

        // 组勾选框跟随「是否全选」，部分选中时显示为未勾（模板没有三态，避免出现假状态）
        _syncing = true;
        try
        {
            var group = FindGroup(item);
            if (group != null)
            {
                foreach (var r in _groupRows)
                {
                    if (ReferenceEquals(r.Group, group)) r.Box.IsChecked = group.AllSelected;
                }
            }
        }
        finally
        {
            _syncing = false;
        }
        UpdateSummary();
    }

    private TidyGroup? FindGroup(TidyItem item) =>
        _groups.FirstOrDefault(g => g.Items.Contains(item));

    private void UpdateSummary()
    {
        int total = _groups.Sum(g => g.Items.Count);
        int selected = _groups.Sum(g => g.SelectedCount);
        long bytes = _groups.Sum(g => g.SelectedBytes);

        foreach (var (group, _, count) in _groupRows)
        {
            int n = group.Items.Count;
            int sel = group.SelectedCount;
            var size = group.Items.Where(i => i.IsDirectory).Any() && group.SelectedBytes == 0
                ? ""
                : $" · {StorageMigration.Format(group.SelectedBytes)}";
            count.Text = sel == n
                ? $"{n} 项{size}"
                : $"{sel}/{n} 项{size}";
        }

        if (total == 0)
        {
            SummaryText.Text = "桌面没有可整理的条目";
            OkBtn.IsEnabled = false;
            return;
        }

        var sizeText = bytes > 0 ? $"（约 {StorageMigration.Format(bytes)}）" : "";
        SummaryText.Text = selected == total
            ? $"桌面共 {total} 项，分成 {_groups.Count} 组 · 全部勾选{sizeText}"
            : $"桌面共 {total} 项，分成 {_groups.Count} 组 · 已勾选 {selected} 项{sizeText}";

        OkBtn.IsEnabled = selected > 0;
        OkBtn.Content = selected > 0 ? $"整理 {selected} 项" : "开始整理";
    }

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        var chosen = _groups.Where(g => g.SelectedCount > 0).ToList();
        if (chosen.Count == 0) return;

        OkBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitle.Text = "正在整理…";
        BusyBar.Value = 0;
        BusyDetail.Text = "准备中…";

        // 盒子必须先在 UI 线程建好（要开窗口、写配置），搬运本身再丢后台
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var boxes = new Dictionary<string, BoxConfig>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var group in chosen)
            {
                var box = _ensureBox(group.TargetBoxName);
                boxes[group.TargetBoxName] = box;
                dirs[group.TargetBoxName] = _store.BoxDir(box);
            }
        }
        catch (Exception ex)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, $"无法创建目标盒子：{ex.Message}", "一键整理",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            OkBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
            return;
        }

        var moves = DesktopTidyService.BuildMoves(chosen, name => dirs[name]);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<TidyProgress>(p =>
        {
            BusyBar.Value = p.Percent;
            BusyDetail.Text = $"{p.Done}/{p.Total} · {p.Current}";
        });

        TidyResult result;
        try
        {
            result = await Task.Run(
                () => DesktopTidyService.MoveAll(moves, progress, token), token);
        }
        catch (OperationCanceledException)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this,
                "已取消。已经搬走的条目留在盒子里，其余仍在桌面。",
                "一键整理", MessageBoxButton.OK, MessageBoxImage.Information);
            Applied = true;   // 已搬走的要落盘
            Close();
            return;
        }
        catch (Exception ex)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, $"整理失败：{ex.Message}", "一键整理",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            OkBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
            return;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }

        // Order 记的是真实文件名，影响盒内排序；写回并落盘
        foreach (var move in result.MovedItems)
        {
            if (!boxes.TryGetValue(move.TargetBox, out var box)) continue;
            if (!box.Order.Contains(move.Name)) box.Order.Add(move.Name);
        }
        _store.Save(_config);

        Result = result;
        Applied = true;
        Close();
    }

    private void OnCancelTidy(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BusyCancelBtn.IsEnabled = false;
        BusyTitle.Text = "正在取消…";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
