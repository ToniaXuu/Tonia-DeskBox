using System.IO;
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

    /// <summary>
    /// 建盒子之前的一次性回调：告诉 App「哪些桌面图标整理后会留在原处」。
    /// 新盒子要绕开它们，否则会正好盖在没被整理的快捷方式上面。
    /// </summary>
    private readonly Action<IReadOnlyList<BoxArea>>? _preparePlacement;

    private readonly Dictionary<TidyItem, CheckBox> _itemBoxes = new();
    private readonly List<(TidyGroup Group, CheckBox Box, TextBlock Count)> _groupRows = new();

    private CancellationTokenSource? _cts;
    private bool _syncing;

    /// <summary>整理前的桌面布局。窗口打开时就采好：模态期间用户动不了桌面，这份就是"整理前"。</summary>
    private DesktopLayout? _layout;
    private string _layoutError = "";

    public bool Applied { get; private set; }
    public TidyResult? Result { get; private set; }

    /// <param name="ensureBox">按名字拿到盒子（不存在就创建）。创建窗口 / 写配置的职责留在 App。</param>
    /// <param name="preparePlacement">
    /// 第一次建盒子之前调用，参数是**整理后仍留在桌面**的图标矩形。
    /// App 会把这些矩形当障碍，让新盒子避开它们。
    /// </param>
    public TidyPreviewWindow(
        List<TidyGroup> groups, ConfigStore store, AppConfig config,
        Func<string, BoxConfig> ensureBox,
        Action<IReadOnlyList<BoxArea>>? preparePlacement = null)
    {
        InitializeComponent();
        _groups = groups;
        _store = store;
        _config = config;
        _ensureBox = ensureBox;
        _preparePlacement = preparePlacement;

        var (back, opacity) = ResolveAppearance(config);
        AppearanceTheme.Attach(this).Update(
            ThemeService.ResolveColor(back), opacity, "一键整理", minAlpha: 0.96);

        BuildGroups();
        CaptureLayout();
        UpdateSummary();
    }

    /// <summary>
    /// 记录整理前的图标位置与排列状态。
    ///
    /// 失败**不阻止**整理：文件去向仍然会被记录，只是「一键恢复」还原不了位置。
    /// 但失败原因必须带出去 —— 否则用户点了恢复、图标却全堆在左上角，会以为功能坏了。
    /// </summary>
    private void CaptureLayout()
    {
        if (DesktopLayoutService.TryCapture(out var layout, out var error))
        {
            _layout = layout;
            _layoutError = "";
        }
        else
        {
            _layout = null;
            _layoutError = error;
        }
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

        UpdateLayoutHint();
        UpdateBackupHint();

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

    private void OnBackupToggled(object sender, RoutedEventArgs e) => UpdateBackupHint();

    private void UpdateLayoutHint()
    {
        if (LayoutHint == null) return;

        if (_layout != null)
        {
            LayoutHint.Text = _layout.Icons.Count == 0
                ? "桌面当前没有图标，仍会记录「自动排列」状态。"
                : $"已记录 {_layout.Icons.Count} 个桌面图标的位置与排列方式（自动排列："
                  + (_layout.AutoArrange ? "开" : "关") + "），「一键恢复」可连同布局一起还原。";
            LayoutHint.Foreground = (Brush)FindResource(ThemeKeys.TextSecondary);
        }
        else
        {
            LayoutHint.Text = $"图标位置未能记录：{_layoutError}。仍可用「一键恢复」把文件放回桌面。";
            LayoutHint.Foreground = (Brush)FindResource(ThemeKeys.Danger);
        }
    }

    private void UpdateBackupHint()
    {
        if (BackupHint == null) return;

        if (BackupCheck.IsChecked != true)
        {
            BackupHint.Text = "不备份：只记录文件去向与图标位置，恢复时直接从盒子里搬回。";
            return;
        }

        long bytes = _groups.Sum(g => g.SelectedBytes);
        var sizeText = bytes > 0 ? $"约 {StorageMigration.Format(bytes)}" : "大小未知";
        BackupHint.Text = $"只复制本次勾选的条目（{sizeText}），未勾选的文件不会被移动或复制。";
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

        // 先交底：哪些桌面图标整理后仍留在原处。必须赶在第一个盒子创建之前 ——
        // 否则第一个盒子拿到的障碍清单里没有它们，会正好压上去
        _preparePlacement?.Invoke(BuildKeptIconBounds(chosen));

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

        // 布局在窗口打开时就采过；那次失败（比如 Explorer 刚重启）的话这里再试一次
        if (_layout == null) CaptureLayout();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<TidyProgress>(p =>
        {
            BusyBar.Value = p.Percent;
            BusyDetail.Text = $"{p.Done}/{p.Total} · {p.Current}";
        });

        void ResetUi()
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
            OkBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }

        // ---- 1. 可选备份：必须在任何文件被搬走之前完成 ----
        // 备份失败不直接终止：用户可能只想腾桌面，而"文件去向 + 图标位置"仍然记着，
        // 一键恢复照样能从盒子里搬回来。但必须问一句，不能默默降级。
        TidyBackupResult? backup = null;
        if (BackupCheck.IsChecked == true)
        {
            BusyTitle.Text = "正在备份…";
            BusyBar.Value = 0;
            BusyDetail.Text = "准备备份…";
            var sources = moves.Select(m => m.Source).ToList();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                backup = await Task.Run(
                    () => TidyBackupService.Create(sources, _store.ConfigDir, stamp, progress, token),
                    token);
            }
            catch (OperationCanceledException)
            {
                ResetUi();
                MessageBox.Show(this, "已取消，还没有移动任何文件。", "一键整理",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            catch (Exception ex)
            {
                var go = MessageBox.Show(this,
                    $"备份失败：{ex.Message}\n\n继续整理吗？\n"
                    + "文件去向与图标位置仍会被记录，「一键恢复」照样能把它们从盒子里搬回来，"
                    + "只是没有额外副本兜底。",
                    "一键整理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (go != MessageBoxResult.Yes)
                {
                    ResetUi();
                    return;
                }
            }
        }

        // ---- 2. 搬运 ----
        BusyTitle.Text = "正在整理…";
        BusyBar.Value = 0;

        TidyResult result;
        try
        {
            result = await Task.Run(
                () => DesktopTidyService.MoveAll(moves, progress, token), token);
        }
        catch (OperationCanceledException)
        {
            // 已经搬走的那部分必须留下恢复记录，否则它们就"回不去了"
            SaveState(moves, backup);
            _store.Save(_config);
            ResetUi();
            MessageBox.Show(this,
                "已取消。已经搬走的条目留在盒子里，可用托盘菜单「一键恢复桌面」放回桌面。",
                "一键整理", MessageBoxButton.OK, MessageBoxImage.Information);
            Applied = true;
            Close();
            return;
        }
        catch (Exception ex)
        {
            SaveState(moves, backup);
            _store.Save(_config);
            ResetUi();
            MessageBox.Show(this,
                $"整理失败：{ex.Message}\n\n可用托盘菜单「一键恢复桌面」把已搬走的条目放回桌面。",
                "一键整理", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        // ---- 3. 记录恢复所需的快照（与是否备份无关，永远写）----
        SaveState(moves, backup);

        Result = result;
        Applied = true;
        Close();
    }

    /// <summary>
    /// 本次整理**不会**搬走的桌面图标矩形。
    ///
    /// 用的是窗口打开时采的布局（那一刻就是"整理前"）：被勾选的会离开桌面，
    /// 其余必须留着 —— 新盒子绕开它们，才不会盖住用户还要点的快捷方式。
    /// 布局采不到时返回空列表，盒子退回"只避让已有盒子"的老规则。
    /// </summary>
    private IReadOnlyList<BoxArea> BuildKeptIconBounds(IReadOnlyList<TidyGroup> chosen)
    {
        var bounds = _layout?.ScreenBounds;
        if (bounds == null || bounds.Count == 0) return Array.Empty<BoxArea>();

        var moving = chosen.SelectMany(g => g.Selected).Select(i => i.Name).ToList();
        var keep = new List<BoxArea>(bounds.Count);

        foreach (var icon in bounds)
        {
            if (icon.W <= 0 || icon.H <= 0) continue;
            if (DesktopTidyService.IconWillMove(icon.Name, moving)) continue;
            keep.Add(new BoxArea(icon.X, icon.Y, icon.W, icon.H));
        }

        return keep;
    }

    /// <summary>
    /// 写恢复快照。存的是**计划里的全部条目**而不是"成功搬走的"：
    /// 取消或部分失败时，没搬走的那些 BoxPath 不存在，恢复计划会退到备份、
    /// 或者因桌面已有同名而跳过 —— 两种都比"漏记导致回不去"安全。
    /// </summary>
    private void SaveState(IReadOnlyList<TidyMove> moves, TidyBackupResult? backup)
    {
        var state = new TidyState
        {
            CreatedAt = DateTime.Now,
            LayoutCaptured = _layout != null,
            LayoutError = _layout == null ? _layoutError : null,
            AutoArrange = _layout?.AutoArrange ?? false,
            Icons = _layout?.Icons ?? new List<DesktopIconPos>(),
            Items = moves.Select(m => new TidyRestoreItem
            {
                Name = m.Name,
                OriginalPath = m.Source,
                BoxPath = Path.Combine(m.TargetDir, m.Name),
                BoxName = m.TargetBox,
            }).ToList(),
            BackupDir = backup?.Dir,
            BackupFiles = backup?.Files ?? 0,
            BackupBytes = backup?.Bytes ?? 0,
        };

        try
        {
            TidyStateStore.Save(_store.ConfigDir, state);
        }
        catch (Exception ex)
        {
            // 写不进去必须说：否则下次点「一键恢复」会被告知"没有整理记录"
            MessageBox.Show(this,
                $"文件已整理，但恢复记录保存失败：{ex.Message}\n\n"
                + "请检查配置目录是否可写；本次整理将无法通过「一键恢复」还原。",
                "一键整理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
