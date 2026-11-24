using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Windows;

/// <summary>
/// 一键恢复的确认与执行窗。
///
/// 两件事必须分开说清楚，否则用户会以为"恢复"就是"撤销"：
/// - **文件**：从盒子（或整理前的备份）搬回桌面原位，桌面已有同名的不覆盖；
/// - **布局**：文件回去之后，再按记录把图标摆回原来的坐标、还原「自动排列」状态。
/// 所以界面上把「能恢复到什么程度」逐项列出来 —— 哪些来自盒子、哪些只有备份、
/// 哪些彻底找不到了，动手之前就能看见。
/// </summary>
public partial class TidyRestoreWindow : Window
{
    private readonly TidyState _state;
    private readonly TidyRestorePlan _plan;
    private CancellationTokenSource? _cts;

    public bool Applied { get; private set; }
    public TidyRestoreResult? Result { get; private set; }

    /// <summary>布局实际摆回去的图标数。</summary>
    public int LayoutApplied { get; private set; }
    /// <summary>记录里有、但桌面上已经不存在的图标数。</summary>
    public int LayoutMissing { get; private set; }
    public string LayoutError { get; private set; } = "";
    public bool LayoutAttempted { get; private set; }

    public TidyRestoreWindow(TidyState state, TidyRestorePlan plan, AppConfig? config)
    {
        InitializeComponent();
        _state = state;
        _plan = plan;

        var (back, opacity) = ResolveAppearance(config);
        AppearanceTheme.Attach(this).Update(
            ThemeService.ResolveColor(back), opacity, "一键恢复", minAlpha: 0.96);

        BuildList();
        UpdateSummary();
    }

    private static (string BackColor, double Opacity) ResolveAppearance(AppConfig? config)
    {
        if (config?.Settings.Global is { } g) return (g.BackColor, g.Opacity);
        if (config?.Boxes.FirstOrDefault() is { } b) return (b.BackColor, b.Opacity);
        return (AppearanceTheme.DefaultBackColor, AppearanceTheme.DefaultOpacity);
    }

    private void BuildList()
    {
        ItemList.Children.Clear();

        foreach (var entry in _plan.Items)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = entry.Item.Name,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = entry.Item.OriginalPath,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(
                    entry.Origin == TidyRestoreOrigin.Missing ? ThemeKeys.TextMuted : ThemeKeys.Text),
            };
            row.Children.Add(name);

            var (label, brushKey) = entry.Origin switch
            {
                TidyRestoreOrigin.Box => ($"从盒子「{entry.Item.BoxName}」搬回", ThemeKeys.TextMuted),
                TidyRestoreOrigin.Backup => ("从整理前的备份复制", ThemeKeys.Accent),
                _ => ("无法恢复：盒子里和备份里都没有", ThemeKeys.Danger),
            };

            var tag = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(brushKey),
            };
            Grid.SetColumn(tag, 1);
            row.Children.Add(tag);

            ItemList.Children.Add(row);
        }
    }

    private void UpdateSummary()
    {
        int restorable = _plan.RestorableCount;
        int missing = _plan.MissingCount;
        int fromBackup = _plan.FromBackupCount;

        var parts = new List<string>();
        if (restorable > 0) parts.Add($"可恢复 {restorable} 项");
        if (fromBackup > 0) parts.Add($"其中 {fromBackup} 项来自备份");
        if (missing > 0) parts.Add($"{missing} 项已找不到");

        SummaryText.Text = parts.Count > 0
            ? "上次整理搬走了 " + _state.Items.Count + " 项：" + string.Join("，", parts) + "。"
            : "上次整理没有搬走任何文件。";

        var layoutText = _state.LayoutCaptured
            ? $"已记录 {_state.Icons.Count} 个图标的位置"
            : "图标位置未记录";
        TimeText.Text = $"整理时间：{_state.CreatedAt:yyyy-MM-dd HH:mm} · {layoutText}"
            + (_state.HasBackup ? $" · 备份 {_state.BackupFiles} 个文件（{StorageMigration.Format(_state.BackupBytes)}）" : " · 未备份");

        bool canRestoreLayout = _state.LayoutCaptured && _state.Icons.Count > 0;
        if (!_state.LayoutCaptured)
        {
            LayoutHint.Text = $"图标位置没有记录（{_state.LayoutError ?? "原因未知"}），"
                + "文件放回桌面后会按系统当前的排列方式摆放。";
            LayoutHint.Foreground = (Brush)FindResource(ThemeKeys.Danger);
        }
        else if (canRestoreLayout)
        {
            LayoutHint.Text = "文件回到桌面后会按整理前的位置重新摆放图标，并还原「自动排列」状态。";
            LayoutHint.Foreground = (Brush)FindResource(ThemeKeys.TextSecondary);
        }
        else
        {
            LayoutHint.Text = "整理时桌面上没有图标，无需还原布局。";
            LayoutHint.Foreground = (Brush)FindResource(ThemeKeys.TextSecondary);
        }

        OkBtn.IsEnabled = restorable > 0 || canRestoreLayout;
        OkBtn.Content = restorable > 0 ? $"恢复 {restorable} 项" : "还原图标布局";
    }

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        OkBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitle.Text = "正在恢复…";
        BusyBar.Value = 0;
        BusyDetail.Text = "准备中…";

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<TidyProgress>(p =>
        {
            BusyBar.Value = p.Percent;
            BusyDetail.Text = $"{p.Done}/{p.Total} · {p.Current}";
        });

        try
        {
            if (_plan.Items.Count > 0)
            {
                Result = await Task.Run(
                    () => TidyRestoreService.Execute(_plan, progress, token), token);
            }

            // 布局必须在文件回到桌面**之后**再还原：图标是按名字匹配的，
            // 文件还没出现在桌面上时，设置位置是对着空气设。
            if (_state.LayoutCaptured && _state.Icons.Count > 0)
            {
                BusyTitle.Text = "正在还原图标布局…";
                BusyBar.Value = 0;
                BusyDetail.Text = "按整理前的位置摆放…";

                var layout = new DesktopLayout
                {
                    AutoArrange = _state.AutoArrange,
                    Icons = _state.Icons,
                };

                var (ok, error, applied, missing) = await Task.Run(() =>
                {
                    bool r = DesktopLayoutService.TryRestore(layout, out string err,
                        out int ap, out int mi);
                    return (r, err, ap, mi);
                }, token);

                LayoutAttempted = true;
                LayoutError = ok ? "" : error;
                LayoutApplied = applied;
                LayoutMissing = missing;
            }

            Applied = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            Applied = true;
            MessageBox.Show(this,
                "已取消。已经放回桌面的条目会留在桌面，其余仍在盒子里。",
                "一键恢复", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, $"恢复失败：{ex.Message}", "一键恢复",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            OkBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancelRestore(object sender, RoutedEventArgs e)
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
