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

    public GlobalSettingsWindow(AppConfig config, ConfigStore store)
    {
        InitializeComponent();
        _config = config;
        _store = store;

        var g = config.Settings.Global;
        _selectedColor = g?.BackColor ?? "#1F2A30";
        OpacitySlider.Value = Math.Clamp((g?.Opacity ?? 0.85) * 100, 0, 100);
        RoundedCheck.IsChecked = g?.Rounded ?? true;
        RadiusSlider.Value = g?.CornerSize ?? 8;
        AcrylicCheck.IsChecked = g?.Acrylic ?? false;
        AcrylicCheck.IsEnabled = DwmHelper.IsAcrylicSupported();
        TopmostCheck.IsChecked = g?.Topmost ?? false;

        BuildSwatches();
        SyncValueBoxes();
        UpdatePreview();
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
            btn.BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
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
        PreviewBorder.Background = MakeBrush(_selectedColor);
        PreviewBorder.Opacity = Math.Max(OpacitySlider.Value / 100.0, 0.15);
        var radius = RoundedCheck.IsChecked == true ? RadiusSlider.Value : 0;
        PreviewBorder.CornerRadius = new CornerRadius(radius);
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
        g.Acrylic = AcrylicCheck.IsChecked == true;
        g.Rounded = RoundedCheck.IsChecked == true;
        g.CornerSize = RadiusSlider.Value;
        g.Topmost = TopmostCheck.IsChecked == true;
        _store.Save(_config);

        if (Application.Current is App app)
            app.ApplyGlobalToAllBoxes(g);

        Close();
    }
}
