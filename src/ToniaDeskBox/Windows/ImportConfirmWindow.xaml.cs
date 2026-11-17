using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Windows;

/// <summary>
/// 导入前的确认窗：把"包里有什么""会怎么处理"和"将会失去什么"同时摆出来，
/// 让人在按下按钮前就知道代价，而不是导入完才发现原来的盒子没了。
/// </summary>
public partial class ImportConfirmWindow : Window
{
    private readonly BackupManifest _manifest;
    private readonly IReadOnlyList<string> _existingNames;
    private readonly int _currentBoxes;
    private readonly int _currentItems;
    private readonly string _dataRoot;
    private readonly string _replacedBackupPath;

    public bool Confirmed { get; private set; }

    /// <summary>用户选的导入方式。合并 = 只增不删；覆盖 = 整体替换。</summary>
    public ImportMode Mode => ReplaceChip.IsChecked == true ? ImportMode.Replace : ImportMode.Merge;

    /// <param name="manifest">对备份包做的只读体检结果。</param>
    /// <param name="existingNames">当前所有盒子的名字（算数量 + 预演重名改名）。</param>
    /// <param name="currentItems">当前盒子里的文件总数（用于对比"会不会少东西"）。</param>
    /// <param name="replacedBackupPath">覆盖模式下旧数据将被改名到的位置。</param>
    public ImportConfirmWindow(
        BackupManifest manifest, IReadOnlyList<string> existingNames, int currentItems,
        string dataRoot, string replacedBackupPath)
    {
        InitializeComponent();

        _manifest = manifest;
        _existingNames = existingNames;
        _currentBoxes = existingNames.Count;
        _currentItems = currentItems;
        _dataRoot = dataRoot;
        _replacedBackupPath = replacedBackupPath;

        var config = (Application.Current as App)?.Config;
        var (back, opacity) = ResolveAppearance(config);
        AppearanceTheme.Attach(this).Update(
            ThemeService.ResolveColor(back), opacity, "导入备份", minAlpha: 0.96);

        var when = manifest.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "未知";
        SummaryText.Text = $"{when} · {manifest.FileCount} 个文件 · {TransferProgressWindow.FormatSize(manifest.BytesTotal)}";
        ZipPathText.Text = manifest.ZipPath;
        ZipPathText.ToolTip = manifest.ZipPath;

        FillBoxList(ImportMode.Merge);

        if (!string.IsNullOrWhiteSpace(manifest.BackupDataRoot) &&
            !string.Equals(manifest.BackupDataRoot, dataRoot, StringComparison.OrdinalIgnoreCase))
        {
            RootNoteText.Text =
                $"备份来自 {manifest.BackupDataRoot}；那台机器的路径在本机不适用，" +
                $"导入后仍使用本机数据根 {dataRoot}。";
            RootNoteText.Visibility = Visibility.Visible;
        }

        // 默认「合并」：非破坏性；换台电脑恢复时效果与覆盖等价
        MergeChip.IsChecked = true;
        ApplyModeTexts();
    }

    private static (string BackColor, double Opacity) ResolveAppearance(Models.AppConfig? config)
    {
        if (config?.Settings.Global is { } g) return (g.BackColor, g.Opacity);
        if (config?.Boxes.FirstOrDefault() is { } b) return (b.BackColor, b.Opacity);
        return (AppearanceTheme.DefaultBackColor, AppearanceTheme.DefaultOpacity);
    }

    /// <summary>
    /// 重建盒子列表。合并模式下必须显示**落地后的名字**——否则用户看到"编程工具"，
    /// 导入后却发现桌面多了一个"编程工具 (2)"，会有种"它偷偷改了东西"的错觉。
    /// </summary>
    private void FillBoxList(ImportMode mode)
    {
        BoxList.Children.Clear();

        if (_manifest.Boxes.Count == 0)
        {
            BoxList.Children.Add(Muted("（备份包里没有可识别的盒子）"));
            return;
        }

        if (mode == ImportMode.Merge)
        {
            var plan = ImportService.BuildMergePlan(_manifest, _existingNames);
            var itemCount = _manifest.Boxes.ToDictionary(b => b.Name, b => b.Items, StringComparer.Ordinal);
            foreach (var p in plan)
            {
                itemCount.TryGetValue(p.SourceDir, out int items);
                bool renamed = !string.Equals(p.SourceDir, p.FinalName, StringComparison.Ordinal);
                BoxList.Children.Add(BoxRow(p.FinalName, items, renamed ? p.SourceDir : null));
            }
            return;
        }

        foreach (var (name, items) in _manifest.Boxes)
            BoxList.Children.Add(BoxRow(name, items, null));
    }

    /// <summary>模式一换，标题、列表口径、警告文案、按钮字全都要跟着变。</summary>
    private void ApplyModeTexts()
    {
        FillBoxList(Mode);

        int incoming = _manifest.Boxes.Count;

        if (Mode == ImportMode.Merge)
        {
            BoxesHeaderText.Text = $"将从备份追加 {incoming} 个盒子";
            ModeHintText.Text = _currentBoxes > 0
                ? "现有盒子与文件保持原样，备份里的盒子作为新盒子加进来；同名盒子自动改成「名称 (2)」。"
                : "当前没有盒子，合并等同于把备份内容全部导入。";

            WarnBorder.BorderBrush = (Brush)FindResource(ThemeKeys.Border);
            WarnTitleText.Text = _currentBoxes > 0
                ? $"现有 {_currentBoxes} 个盒子（共 {_currentItems} 项）不会被改动"
                : "当前没有盒子，导入后按备份新建";
            WarnDetailText.Text =
                "合并只增不删：不会覆盖、也不会移动任何现有文件。若某个盒子名已被占用，" +
                "新盒子会落到独立目录，两个盒子互不影响。";
            OkBtn.Content = "开始合并";
        }
        else
        {
            BoxesHeaderText.Text = $"将用备份的 {incoming} 个盒子替换当前数据";
            ModeHintText.Text = "用备份整体替换当前数据：盒子结构、外观设置与文件都变成备份里的样子。";

            WarnBorder.BorderBrush = (Brush)FindResource(ThemeKeys.Danger);
            WarnTitleText.Text = _currentBoxes > 0
                ? $"当前的 {_currentBoxes} 个盒子（共 {_currentItems} 项）将被替换"
                : "当前没有盒子，导入后会按备份重建";
            WarnDetailText.Text =
                $"替换前会自动把当前数据改名备份到 {_replacedBackupPath}，" +
                "并在配置目录留一份 config.before-import 副本。备份文件夹不会自动删除，随时可以手动恢复。";
            OkBtn.Content = "替换并导入";
        }
    }

    private void OnModeClick(object sender, RoutedEventArgs e) => ApplyModeTexts();

    private TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)FindResource(ThemeKeys.TextMuted),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>一行：盒子名（左）+ 项数（右）。项数为 0 时说明结构来自目录推断。</summary>
    /// <param name="originalName">因重名被改名时的原名，用于标注"（原名：xxx）"。</param>
    private FrameworkElement BoxRow(string name, int items, string? originalName)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource(ThemeKeys.Text),
        });
        if (originalName != null)
        {
            line.Children.Add(new TextBlock
            {
                Text = $"原名 {originalName}",
                FontSize = 11,
                Margin = new Thickness(8, 1, 0, 0),
                Foreground = (Brush)FindResource(ThemeKeys.TextMuted),
            });
        }
        grid.Children.Add(line);

        var count = new TextBlock
        {
            Text = items > 0 ? $"{items} 项" : "—",
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = (Brush)FindResource(ThemeKeys.TextMuted),
        };
        Grid.SetColumn(count, 1);
        grid.Children.Add(count);
        return grid;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
