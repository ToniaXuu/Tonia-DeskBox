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
    private string _selectedColor;

    public bool Applied { get; private set; }
    public bool AcrylicWanted { get; private set; }

    public BoxSettingsWindow(BoxConfig box, AppConfig config, ConfigStore store)
    {
        InitializeComponent();
        _box = box;
        _config = config;
        _store = store;
        _selectedColor = box.BackColor;

        NameBox.Text = box.Name;
        OpacitySlider.Value = Math.Clamp(box.Opacity * 100, 0, 100);
        RoundedCheck.IsChecked = box.Rounded;
        RadiusSlider.Value = box.CornerSize;
        AcrylicCheck.IsChecked = box.Acrylic;
        AcrylicCheck.IsEnabled = DwmHelper.IsAcrylicSupported();
        TopmostCheck.IsChecked = box.Topmost;

        BuildSwatches();
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
            };
            btn.Click += (_, _) =>
            {
                _selectedColor = (string)btn.Tag;
                UpdatePreview();
            };
            SwatchPanel.Children.Add(btn);
        }
    }

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

    private void UpdatePreview()
    {
        PreviewBorder.Background = MakeBrush(_selectedColor);
        PreviewBorder.Opacity = Math.Max(OpacitySlider.Value / 100.0, 0.15);
        var radius = RoundedCheck.IsChecked == true ? RadiusSlider.Value : 0;
        PreviewBorder.CornerRadius = new CornerRadius(radius);
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
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
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "名称不能包含 \\ / : * ? \" < > | 等字符。", "Tonia-DeskBox");
            return;
        }

        if (!string.Equals(name, _box.Name, StringComparison.Ordinal))
        {
            if (_config.Boxes.Any(b => !ReferenceEquals(b, _box) && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, $"已有名为“{name}”的盒子。", "Tonia-DeskBox");
                return;
            }

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

        _box.Opacity = OpacitySlider.Value / 100.0;
        _box.BackColor = _selectedColor;
        _box.Rounded = RoundedCheck.IsChecked == true;
        _box.CornerSize = RadiusSlider.Value;
        AcrylicWanted = AcrylicCheck.IsChecked == true;
        _box.Acrylic = AcrylicWanted;
        _box.Topmost = TopmostCheck.IsChecked == true;

        Applied = true;
        Close();
    }
}
