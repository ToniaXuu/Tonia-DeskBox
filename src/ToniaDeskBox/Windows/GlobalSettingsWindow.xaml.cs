using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Windows;

public partial class GlobalSettingsWindow : Window
{
    private static readonly string[] Palette =
    {
        "#1F2A30", "#2B2B2B", "#3A3A3A", "#1E1E2E",
        "#202A44", "#2D3B2D", "#4A3434", "#5B4A2F",
        "#F2F2F2", "#DCE3E8", "#E8DFF5", "#FFFFFF",
    };

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly List<(Button Btn, string Hex)> _swatches = new();
    private string _selectedColor;
    // 可能为空：InitializeComponent() 期间滑杆 ValueChanged 会先触发一次预览
    private AppearanceTheme? _theme;
    // 打开时的主题模式：主题芯片是"点一下就地生效"，点取消要能回滚
    private ThemeMode _initialMode;
    private const string ThemeName = "全局盒子设置";

    public GlobalSettingsWindow(AppConfig config, ConfigStore store)
    {
        InitializeComponent();
        _theme = AppearanceTheme.Attach(this);
        _config = config;
        _store = store;
        _initialMode = ThemeService.Mode;

        var g = config.Settings.Global;
        _selectedColor = g?.BackColor ?? "#1F2A30";
        OpacitySlider.Value = Math.Clamp((g?.Opacity ?? 0.85) * 100, 0, 100);
        RoundedCheck.IsChecked = g?.Rounded ?? true;
        RadiusSlider.Value = g?.CornerSize ?? 8;
        TopmostCheck.IsChecked = g?.Topmost ?? false;

        BuildSwatches();
        SyncValueBoxes();
        SyncThemeChips();
        UpdatePreview();
        RefreshStorageInfo();
    }

    // ---------- 主题模式 ----------

    private void SyncThemeChips()
    {
        var mode = ThemeService.Mode;
        ThemeSystemChip.IsChecked = mode == ThemeMode.System;
        ThemeLightChip.IsChecked = mode == ThemeMode.Light;
        ThemeDarkChip.IsChecked = mode == ThemeMode.Dark;
        ThemeCustomChip.IsChecked = mode == ThemeMode.Custom;

        bool locked = ThemeService.IsColorLocked;
        // 底色被主题接管后不再允许挑色板；但保留原选择，切回「自定义」继续用
        SwatchPanel.IsEnabled = !locked;
        WallpaperBtn.IsEnabled = !locked;

        ThemeHintText.Text = locked
            ? "底色由主题统一决定；各盒子原来的配色会保留，切回「自定义」即可恢复。"
            : "各盒子使用自己的配色，可在这里改默认值，也能单独在盒子设置里改。";
    }

    private void OnThemeChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<ThemeMode>(tag, out var mode)) return;

        // 立即生效才能看到盒子的真实效果；点「取消」会回滚到打开时的模式
        ThemeService.SetMode(mode);
        SyncThemeChips();
        UpdatePreview();
    }

    private void OnCancelTheme(object sender, RoutedEventArgs e)
    {
        // 主题是点一下就地生效的，取消时恢复原状，避免「取消却改了东西」
        if (ThemeService.Mode != _initialMode)
            ThemeService.SetMode(_initialMode);
    }

    private void BuildSwatches()
    {
        foreach (var hex in Palette)
        {
            var btn = new Button
            {
                Width = 34,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 6),
                Tag = hex,
                Background = MakeBrush(hex),
                Style = (Style)FindResource("SwatchButton"),
            };
            btn.Click += (_, _) =>
            {
                _selectedColor = (string)btn.Tag;
                UpdatePreview();
            };
            _swatches.Add((btn, hex));
            SwatchPanel.Children.Add(btn);
        }
    }

    private void UpdateSwatchSelection()
    {
        foreach (var (btn, hex) in _swatches)
        {
            bool on = string.Equals(hex, _selectedColor, StringComparison.OrdinalIgnoreCase);
            btn.BorderBrush = _theme!.BrushOf(on ? ThemeKeys.Ring : ThemeKeys.Border);
            btn.BorderThickness = new Thickness(on ? 2 : 1);
        }
    }

    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && e.ChangedButton == System.Windows.Input.MouseButton.Left)
            DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPickWallpaper(object sender, RoutedEventArgs e)
    {
        var hex = WallpaperColorService.GetAverageColorHex();
        if (hex == null)
        {
            MessageBox.Show(this, "无法读取桌面壁纸，请手动选择颜色。", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedColor = hex;
        UpdatePreview();
    }

    private void OnPreviewChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    // ---------- 数值输入框 ↔ 滑杆 ----------

    private void SyncValueBoxes()
    {
        OpacityBox.Text = $"{Math.Round(OpacitySlider.Value):0}";
        RadiusBox.Text = $"{Math.Round(RadiusSlider.Value):0}";
    }

    private void OnValueBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CommitValueBox((TextBox)sender);
        e.Handled = true;
    }

    private void OnValueBoxLostFocus(object sender, RoutedEventArgs e) =>
        CommitValueBox((TextBox)sender);

    private void CommitValueBox(TextBox box)
    {
        var slider = box == OpacityBox ? OpacitySlider : RadiusSlider;
        if (!double.TryParse(box.Text.Trim(), out var v))
        {
            SyncValueBoxes();
            return;
        }
        slider.Value = Math.Clamp(Math.Round(v), slider.Minimum, slider.Maximum);
    }

    private void UpdatePreview()
    {
        if (_theme is null) return;   // InitializeComponent 期间的早期回调

        // 主题接管时用主题色；「自定义」时就是用户选的颜色
        var hex = ThemeService.ResolveColor(_selectedColor);

        // 与「盒子设置」共用同一套主题；切换底色时对话框自己就是实时预览
        _theme.Update(hex, OpacitySlider.Value / 100.0, ThemeName, minAlpha: 0.9);

        double radius = RoundedCheck.IsChecked == true ? RadiusSlider.Value : 0;
        PreviewBorder.Background = MakeBrush(hex);
        PreviewBorder.Opacity = Math.Max(OpacitySlider.Value / 100.0, 0.15);
        PreviewBorder.CornerRadius = new CornerRadius(radius);

        double r = RoundedCheck.IsChecked == true ? Math.Clamp(RadiusSlider.Value + 2, 10, 18) : 2;
        ChromeBorder.CornerRadius = new CornerRadius(r);
        ShadowCaster.CornerRadius = new CornerRadius(r);
        TitleBar.CornerRadius = new CornerRadius(r - 1, r - 1, 0, 0);

        UpdateSwatchSelection();
        SyncValueBoxes();
    }

    private static Brush MakeBrush(string hex)
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

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var g = _config.Settings.Global ??= new GlobalBoxSettings();
        g.Opacity = OpacitySlider.Value / 100.0;
        g.BackColor = _selectedColor;
        g.Rounded = RoundedCheck.IsChecked == true;
        g.CornerSize = RadiusSlider.Value;
        g.Topmost = TopmostCheck.IsChecked == true;
        _store.Save(_config);

        if (Application.Current is App app)
            app.ApplyGlobalToAllBoxes(g);

        Close();
    }

    // ---------- 存储位置 ----------

    private CancellationTokenSource? _migrationCts;

    private void RefreshStorageInfo()
    {
        var root = _store.DataRoot;
        StoragePathText.Text = root;
        StoragePathText.ToolTip = root;

        var plan = StorageMigration.Scan(root);
        var free = StorageMigration.FreeSpaceOf(root);
        var parts = new List<string>
        {
            $"{_config.Boxes.Count} 个盒子",
            $"占用 {StorageMigration.Format(plan.TotalBytes)}",
        };
        if (free.HasValue) parts.Add($"该盘可用 {StorageMigration.Format(free.Value)}");
        if (_store.UsedFallbackRoot) parts.Add("注意：配置的位置本次不可用，已临时回退");
        StorageStatsText.Text = string.Join(" · ", parts);

        ResetStorageBtn.IsEnabled = !_store.IsUsingDefaultRoot;
    }

    private async void OnChangeStorage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择盒子的存储位置",
            InitialDirectory = _store.DataRoot,
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;
        await MigrateToAsync(dlg.FolderName);
    }

    private async void OnResetStorage(object sender, RoutedEventArgs e) =>
        await MigrateToAsync(_store.DefaultDataRoot);

    private void OnCancelMigration(object sender, RoutedEventArgs e)
    {
        _migrationCts?.Cancel();
        BusyCancelBtn.IsEnabled = false;
        BusyTitle.Text = "正在取消…";
    }

    private async Task MigrateToAsync(string target)
    {
        if (Application.Current is not App app) return;

        var current = _store.DataRoot;

        if (!StorageMigration.ValidateTarget(target, current, _store.ConfigDir, out var invalid))
        {
            MessageBox.Show(this, invalid, "存储位置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var plan = StorageMigration.Scan(current);
        var free = StorageMigration.FreeSpaceOf(target);

        var confirm = $"将把 {_config.Boxes.Count} 个盒子的 {plan.FileCount} 个文件" +
                      $"（{StorageMigration.Format(plan.TotalBytes)}）迁移到：\n{target}\n\n" +
                      $"原位置：{current}\n";
        if (free.HasValue)
            confirm += $"目标位置可用空间：{StorageMigration.Format(free.Value)}\n";
        confirm += "\n迁移期间盒子会暂时隐藏，完成后自动恢复。是否继续？";

        if (MessageBox.Show(this, confirm, "迁移存储位置",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        // Owner 是盒子窗口，稍后重建盒子会把它关掉并连带关闭本窗口，所以先断开
        Owner = null;

        app.StorageBusy = true;
        var hidden = app.BoxWindows.Where(w => w.IsVisible).ToList();
        foreach (var w in hidden) w.Hide();

        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitle.Text = "正在迁移盒子文件…";
        BusyBar.Value = 0;
        BusyDetail.Text = "准备中…";
        BusyCancelBtn.IsEnabled = true;
        _migrationCts = new CancellationTokenSource();
        var token = _migrationCts.Token;

        var progress = new Progress<MigrationProgress>(p =>
        {
            BusyBar.Value = p.Percent;
            BusyDetail.Text =
                $"{p.DoneFiles}/{p.TotalFiles} 个文件 · " +
                $"{StorageMigration.Format(p.DoneBytes)} / {StorageMigration.Format(p.TotalBytes)}";
            if (!string.IsNullOrEmpty(p.Current)) BusyDetail.ToolTip = p.Current;
        });

        MigrationResult result;
        try
        {
            // 只复制+校验，不在这里删源：等配置真的切过去了再删
            result = await Task.Run(
                () => StorageMigration.Migrate(current, target, progress, token, purgeSource: false),
                token);
        }
        catch (OperationCanceledException)
        {
            result = new MigrationResult { Success = false, Canceled = true, Error = "已取消。" };
        }
        catch (Exception ex)
        {
            result = new MigrationResult { Success = false, Error = ex.Message };
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            _migrationCts?.Dispose();
            _migrationCts = null;
        }

        if (!result.Success)
        {
            app.StorageBusy = false;
            foreach (var w in hidden) w.Show();
            RefreshStorageInfo();
            if (!result.Canceled)
            {
                MessageBox.Show(this, result.Error ?? "迁移失败，原位置未改动。",
                    "迁移存储位置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        if (!_store.TrySetDataRoot(target, out var setError))
        {
            // 副本已经复制好了，但切不过去：保持原位不动，源仍然完整
            app.StorageBusy = false;
            foreach (var w in hidden) w.Show();
            RefreshStorageInfo();
            MessageBox.Show(this,
                $"文件已复制到新位置，但无法切换过去：{setError}\n\n" +
                $"当前存储位置保持不变，原文件完好。\n新位置的副本可手动删除：\n{target}",
                "迁移存储位置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.Settings.DataRoot = _store.IsUsingDefaultRoot ? null : _store.DataRoot;
        _store.Save(_config);
        _store.Log($"存储位置迁移：{current} -> {target}；" +
                   $"{result.MovedFiles} 个文件 / {StorageMigration.Format(result.MovedBytes)}。");

        // 配置已经指向新根，这才清空原位置；删不掉只提示，不影响使用
        var cleanupErrors = await Task.Run(() => StorageMigration.PurgeSource(current));
        if (cleanupErrors.Count > 0)
            _store.Log("原位置清理未完成：" + string.Join("；", cleanupErrors));

        app.OnDataRootChanged();
        app.StorageBusy = false;
        RefreshStorageInfo();

        var done = $"已迁移 {result.MovedFiles} 个文件（{StorageMigration.Format(result.MovedBytes)}）。\n\n" +
                   $"新位置：{target}";
        if (cleanupErrors.Count > 0)
            done += $"\n\n{StorageMigration.BuildCleanupWarning(cleanupErrors)}";

        MessageBox.Show(this, done, "迁移完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
