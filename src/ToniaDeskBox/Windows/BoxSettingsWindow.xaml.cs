using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Windows;

public partial class BoxSettingsWindow : Window
{
    private static readonly string[] Palette =
    {
        "#1F2A30", "#2B2B2B", "#3A3A3A", "#1E1E2E",
        "#202A44", "#2D3B2D", "#4A3434", "#5B4A2F",
        "#F2F2F2", "#DCE3E8", "#E8DFF5", "#FFFFFF",
    };

    private readonly BoxConfig _box;
    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly List<(Button Btn, string Hex)> _swatches = new();
    private string _selectedColor;
    private BoxDisplayMode _mode;
    // 可能为空：InitializeComponent() 期间滑杆 ValueChanged 会先触发一次预览
    private AppearanceTheme? _theme;

    public bool Applied { get; private set; }

    public BoxSettingsWindow(BoxConfig box, AppConfig config, ConfigStore store)
    {
        InitializeComponent();
        _theme = AppearanceTheme.Attach(this);
        _box = box;
        _config = config;
        _store = store;
        _selectedColor = box.BackColor;

        NameBox.Text = box.Name;
        OpacitySlider.Value = Math.Clamp(box.Opacity * 100, 0, 100);
        WidthSlider.Value = box.W;
        HeightSlider.Value = box.H;
        RoundedCheck.IsChecked = box.Rounded;
        RadiusSlider.Value = box.CornerSize;
        TopmostCheck.IsChecked = box.Topmost;

        _mode = box.DisplayMode;
        SyncModeChips();

        BuildSwatches();
        SyncValueBoxes();
        ApplyThemeLock();
        ApplyPortalNote();
        UpdatePreview();
    }

    // ---------- 展示方式 ----------

    private void SyncModeChips()
    {
        ModePreviewChip.IsChecked = _mode == BoxDisplayMode.Preview;
        ModeTitleChip.IsChecked = _mode == BoxDisplayMode.TitleOnly;
        ModeContentChip.IsChecked = _mode == BoxDisplayMode.Content;

        ModeHint.Text = _mode switch
        {
            BoxDisplayMode.Preview => "标题 + 最多 3 个缩略图和总数。点盒子不会打开面板，搜索与「更多」直接挂在盒子上。",
            BoxDisplayMode.TitleOnly => "只留一行标题，最省地方。同样不打开面板。",
            _ => "盒子内嵌图标网格，点空白处打开面板（原有行为）。宽高尺寸只在这一档生效。",
        };

        // 尺寸只对「显示内容」有意义，另外两档是自适应宽度
        bool content = _mode == BoxDisplayMode.Content;
        WidthSlider.IsEnabled = content;
        WidthBox.IsEnabled = content;
        HeightSlider.IsEnabled = content;
        HeightBox.IsEnabled = content;
    }

    private void OnModeChipClick(object sender, RoutedEventArgs e)
    {
        _mode = sender == ModePreviewChip ? BoxDisplayMode.Preview
            : sender == ModeTitleChip ? BoxDisplayMode.TitleOnly
            : BoxDisplayMode.Content;

        // 切到「显示内容」时给一个刚好三列的宽度，否则滑块还停在 0（自适应）会让人以为没生效
        if (_mode == BoxDisplayMode.Content && WidthSlider.Value < BoxMetrics.ContentMinWidth)
            WidthSlider.Value = BoxMetrics.ContentMinWidth;

        SyncModeChips();
        UpdatePreview();
    }

    /// <summary>映射盒在设置里必须说清楚"文件不归我管"，否则改名时用户会担心搬了目录。</summary>
    private void ApplyPortalNote()
    {
        if (!ConfigStore.IsPortal(_box)) return;
        NameBox.ToolTip = "映射盒改名只改显示名，不会移动任何文件";
        AppearanceNote.Text =
            $"映射盒：显示的是 {_store.SourceDir(_box)} 里的内容，文件始终在原位置。" +
            "改名只改显示名，不会移动、重命名或删除那个文件夹。";
    }

    /// <summary>主题接管底色时，色板与取色按钮不再生效，并说明原因。</summary>
    private void ApplyThemeLock()
    {
        bool locked = ThemeService.IsColorLocked;
        SwatchPanel.IsEnabled = !locked;
        WallpaperBtn.IsEnabled = !locked;
        ThemeLockHint.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 数值输入框 ↔ 滑杆 ----------

    private void SyncValueBoxes()
    {
        OpacityBox.Text = $"{Math.Round(OpacitySlider.Value):0}";
        WidthBox.Text = $"{Math.Round(WidthSlider.Value):0}";
        HeightBox.Text = $"{Math.Round(HeightSlider.Value):0}";
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
        var slider = box == OpacityBox ? OpacitySlider
            : box == WidthBox ? WidthSlider
            : box == HeightBox ? HeightSlider
            : RadiusSlider;

        if (!double.TryParse(box.Text.Trim(), out var v))
        {
            SyncValueBoxes();
            return;
        }
        slider.Value = Math.Clamp(Math.Round(v), slider.Minimum, slider.Maximum);
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

    private void OnCopyFromBox(object sender, RoutedEventArgs e)
    {
        var others = _config.Boxes.Where(b => !ReferenceEquals(b, _box)).ToList();
        if (others.Count == 0)
        {
            MessageBox.Show(this, "当前没有其他盒子。", "Tonia-DeskBox",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var menu = new ContextMenu();
        AppearanceTheme.Attach(menu).Update(
            ThemeService.ResolveColor(_selectedColor), OpacitySlider.Value / 100.0, _box.Name, minAlpha: 0.9);
        foreach (var src in others)
        {
            var mi = new MenuItem { Header = src.Name };
            mi.Click += (_, _) => CopyFrom(src);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void CopyFrom(BoxConfig src)
    {
        OpacitySlider.Value = Math.Clamp(src.Opacity * 100, 0, 100);
        _selectedColor = src.BackColor;
        RoundedCheck.IsChecked = src.Rounded;
        RadiusSlider.Value = src.CornerSize;
        TopmostCheck.IsChecked = src.Topmost;
        WidthSlider.Value = Math.Clamp(Math.Round(src.W / 10) * 10, 0, 560);
        HeightSlider.Value = Math.Clamp(Math.Round(src.H / 10) * 10, 0, 460);
        SyncValueBoxes();
        UpdatePreview();
    }

    private void OnSizeSliderChanged(object sender, RoutedEventArgs e)
    {
        double w = WidthSlider.Value;
        WidthText.Text = w == 0 ? "自适应" : "";
        double h = HeightSlider.Value;
        HeightText.Text = h == 0 ? "自适应" : "";
        SyncValueBoxes();
    }

    private void UpdatePreview()
    {
        if (_theme is null) return;   // InitializeComponent 期间的早期回调

        // 设置窗口本身也用同一套主题：切换底色时立刻看到真实的文字对比效果。
        // 对话框文字密集，透明度下限比桌面盒子高，保证可读。
        // 主题模式接管时用主题色，自定义时用盒子自己的颜色。
        var hex = ThemeService.ResolveColor(_selectedColor);
        _theme.Update(hex, OpacitySlider.Value / 100.0, _box.Name, minAlpha: 0.9);

        double radius = RoundedCheck.IsChecked == true ? RadiusSlider.Value : 0;
        PreviewBorder.Background = MakeBrush(hex);
        PreviewBorder.Opacity = Math.Max(OpacitySlider.Value / 100.0, 0.15);
        PreviewBorder.CornerRadius = new CornerRadius(radius);

        // 圆角规则与展开面板一致，两个设置窗口看起来是同一个组件
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
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "盒子名称不能为空。", "Tonia-DeskBox");
            return;
        }
        if (!ConfigStore.IsSafeSegment(name))
        {
            MessageBox.Show(this,
                "名称不能是 . 或 ..，不能包含 \\ / : * ? \" < > | 等字符，也不能使用系统保留名（CON、PRN、NUL、COM1…）。",
                "Tonia-DeskBox");
            return;
        }

        if (!string.Equals(name, _box.Name, StringComparison.Ordinal))
        {
            if (_config.Boxes.Any(b => !ReferenceEquals(b, _box) && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, $"已有名为“{name}”的盒子。", "Tonia-DeskBox");
                return;
            }

            if (ConfigStore.IsPortal(_box))
            {
                // 映射盒没有属于自己的目录：改名就只是改名。
                // 以前这里会按盒子名去搬目录 —— 对映射盒而言那是别人的文件夹，绝对不能动。
                _box.Name = name;
            }
            else
            {
                var oldName = _box.Name;
                var oldDir = _store.BoxDir(_box);
                _box.Name = name;
                var newDir = _store.BoxDir(_box);

                if (Directory.Exists(oldDir) && !string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
                {
                    if (FileMover.ExistsConflict(newDir))
                    {
                        _box.Name = oldName;
                        MessageBox.Show(this, $"数据目录中已存在文件夹“{name}”，无法重命名。", "Tonia-DeskBox");
                        return;
                    }
                    try
                    {
                        FileMover.MoveTo(oldDir, newDir);
                    }
                    catch (Exception ex)
                    {
                        _box.Name = oldName;
                        MessageBox.Show(this, $"重命名失败：{ex.Message}", "Tonia-DeskBox");
                        return;
                    }
                }
            }
        }

        _box.Opacity = OpacitySlider.Value / 100.0;
        _box.BackColor = _selectedColor;
        _box.DisplayMode = _mode;
        _box.W = _mode == BoxDisplayMode.Content
            ? Math.Max(WidthSlider.Value, BoxMetrics.ContentMinWidth)
            : WidthSlider.Value;
        _box.H = HeightSlider.Value;
        _box.Rounded = RoundedCheck.IsChecked == true;
        _box.CornerSize = RadiusSlider.Value;
        _box.Topmost = TopmostCheck.IsChecked == true;

        Applied = true;
        Close();
    }
}
