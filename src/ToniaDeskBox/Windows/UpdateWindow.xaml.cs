using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Windows;

/// <summary>
/// 「检查更新」窗口。所有网络与文件操作都交给 UpdateService，
/// 这里只负责状态机与展示，保证任何一步失败都能给出可读提示且不影响已运行的盒子。
/// </summary>
public partial class UpdateWindow : Window
{
    private enum Stage { Checking, UpToDate, Available, Downloading, Installing, Failed }

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    // 可能为空：InitializeComponent() 期间就会有动态资源解析，先挂主题再取用
    private AppearanceTheme? _theme;

    private CancellationTokenSource? _cts;
    private UpdateInfo? _pending;
    private Stage _stage = Stage.Checking;

    /// <summary>
    /// 安装程序是否已经拉起。只有为 true 时 App 才会保存配置并退出，
    /// 让安装包接管「替换文件 + 重新启动」。
    /// </summary>
    public bool InstallerLaunched { get; private set; }

    public UpdateWindow(AppConfig config, ConfigStore store)
    {
        InitializeComponent();
        // Attach 只是把 Theme.* 初始化成透明，必须跟着 Update 才能算出实际配色，
        // 否则整个窗口的文字与底色都是透明的（看起来像没渲染）。
        _theme = AppearanceTheme.Attach(this);
        _config = config;
        _store = store;

        // 跟随全局外观；没配过就用第一个盒子的，再退到应用默认深色
        var global = config.Settings.Global;
        var backHex = ThemeService.ResolveColor(
            global?.BackColor
            ?? config.Boxes.FirstOrDefault()?.BackColor
            ?? AppearanceTheme.DefaultBackColor);
        _theme.Update(backHex, global?.Opacity ?? AppearanceTheme.DefaultOpacity,
            "检查更新", minAlpha: 0.9);

        VersionText.Text = $"当前 {UpdateService.CurrentVersion}";
        Loaded += async (_, _) => await CheckAsync();
        Loaded += (_, _) => BringToFront();
        Closing += OnClosing;
    }

    /// <summary>
    /// 从托盘弹出时本进程未必在前台，窗口可能被别的程序盖住。
    /// 置顶一瞬再取消，是最不打扰的「抢前台」写法。
    /// </summary>
    private void BringToFront()
    {
        try
        {
            Topmost = true;
            Activate();
            Topmost = false;
        }
        catch
        {
            // 抢前台失败不影响功能
        }
    }

    // ---------- 检查 ----------

    private async Task CheckAsync()
    {
        CancelPending();
        _pending = null;
        Show(Stage.Checking);
        StatusText.Text = "正在检查更新…";
        DetailText.Text = "正在从 GitHub 获取最新版本信息…";
        NotesPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;

        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            var check = await UpdateService.CheckAsync(_cts.Token);
            if (check.Available is { } info)
            {
                _pending = info;
                StatusText.Text = $"发现新版本 {info.Version}";
                DetailText.Text = info.Size > 0
                    ? $"当前 {UpdateService.CurrentVersion} → 最新 {info.Version}，安装包约 {FormatSize(info.Size)}。"
                    : $"当前 {UpdateService.CurrentVersion} → 最新 {info.Version}。";
                NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
                    ? "（该版本没有附加说明）"
                    : FormatNotes(info.Notes);
                NotesPanel.Visibility = Visibility.Visible;
                Show(Stage.Available);
            }
            else
            {
                StatusText.Text = "已是最新版本";
                DetailText.Text = $"当前 {UpdateService.CurrentVersion} 已经是发布过的最新版本。";
                Show(Stage.UpToDate);
            }
        }
        catch (OperationCanceledException)
        {
            Fail("检查超时，请检查网络后重试。");
        }
        catch (UpdateException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Fail("检查更新时出现意外错误：" + ex.Message);
        }
    }

    // ---------- 下载并安装 ----------

    private async Task DownloadAndInstallAsync()
    {
        if (_pending is not { } info) return;

        CancelPending();
        _cts = new CancellationTokenSource();
        Show(Stage.Downloading);
        StatusText.Text = $"正在下载 {info.Version}…";
        DetailText.Text = "下载过程中可以随时取消，已下载的部分不会留下残余文件。";
        ProgressPanel.Visibility = Visibility.Visible;
        UpdateBar.Value = 0;
        ProgressText.Text = info.Size > 0 ? $"0%  ·  {FormatSize(info.Size)}" : "0%";

        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateBar.Value = p * 100;
                ProgressText.Text = info.Size > 0
                    ? $"{p * 100:0}%  ·  {FormatSize((long)(p * info.Size))} / {FormatSize(info.Size)}"
                    : $"{p * 100:0}%";
            });

            var path = await UpdateService.DownloadAsync(info, progress, _cts.Token);
            UpdateService.CleanupOldInstallers(info.AssetName);

            // 先把配置落盘再让安装包动手：即便安装过程被杀掉，盒子布局也不会丢
            _store.Save(_config);

            UpdateService.LaunchInstaller(path);
            InstallerLaunched = true;

            Show(Stage.Installing);
            StatusText.Text = "正在安装…";
            DetailText.Text = "安装程序已启动，本程序即将退出。安装完成后会自动重新打开，你的盒子与文件都不受影响。";
            ProgressPanel.Visibility = Visibility.Collapsed;

            // 留一点时间让用户看清提示，再退出让安装包替换文件
            await Task.Delay(700);
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消下载";
            DetailText.Text = "没有做任何改动，可以随时重新检查更新。";
            ProgressPanel.Visibility = Visibility.Collapsed;
            Show(Stage.Available);
        }
        catch (UpdateException ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            Fail("更新过程中出现意外错误：" + ex.Message);
        }
    }

    // ---------- 状态 ----------

    private void Show(Stage stage)
    {
        _stage = stage;
        bool canInteract = stage is not Stage.Installing;

        PrimaryBtn.Visibility = stage is Stage.Available or Stage.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrimaryBtn.IsEnabled = canInteract;
        PrimaryBtn.Content = stage == Stage.Failed ? "重试" : "立即更新";

        SecondaryBtn.Content = stage switch
        {
            Stage.Downloading => "取消下载",
            Stage.Available => "稍后",
            _ => "关闭",
        };
        SecondaryBtn.IsEnabled = canInteract;
    }

    private void Fail(string message)
    {
        // 失败分两种：已经拿到新版本信息（可以重试安装），或压根没查到（只能关掉重来）
        if (_pending is null)
        {
            StatusText.Text = "检查更新失败";
            DetailText.Text = message;
            NotesPanel.Visibility = Visibility.Collapsed;
            Show(Stage.Failed);
            PrimaryBtn.Visibility = Visibility.Collapsed;
            SecondaryBtn.Content = "关闭";
        }
        else
        {
            StatusText.Text = "更新没有完成";
            DetailText.Text = message;
            Show(Stage.Failed);
        }
    }

    private void CancelPending()
    {
        try { _cts?.Cancel(); }
        catch { }
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Release 说明是 Markdown，直接显示会满屏 "##" 和 "-"。
    /// 这里只做最基本的降噪：去掉标题井号、列表换成圆点、引用块去符号，
    /// 不做真正的 Markdown 渲染——说明文字用不着那么重。
    /// </summary>
    private static string FormatNotes(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new System.Text.StringBuilder();
        int blankRun = 0;

        foreach (var original in lines)
        {
            var line = original.TrimEnd();
            var trimmed = line.TrimStart();

            // 代码块围栏没有展示价值，直接丢掉
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) continue;

            if (trimmed.StartsWith("### ", StringComparison.Ordinal)) line = trimmed[4..];
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal)) line = trimmed[3..];
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal)) line = trimmed[2..];
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal)) line = trimmed[2..];
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                     trimmed.StartsWith("* ", StringComparison.Ordinal)) line = "• " + trimmed[2..];

            if (line.Trim().Length == 0)
            {
                // 连续空行压成一行，避免说明区里出现大片空白
                if (++blankRun > 1) continue;
            }
            else blankRun = 0;

            sb.AppendLine(line);
        }

        var text = sb.ToString().Trim();
        return text.Length == 0 ? "（该版本没有附加说明）" : text;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        const double kb = 1024d;
        if (bytes < kb) return $"{bytes} B";
        if (bytes < kb * kb) return $"{bytes / kb:0.#} KB";
        if (bytes < kb * kb * kb) return $"{bytes / (kb * kb):0.#} MB";
        return $"{bytes / (kb * kb * kb):0.##} GB";
    }

    // ---------- 交互 ----------

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Available || _stage == Stage.Failed)
            await DownloadAndInstallAsync();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        // 下载中：只取消，不关窗，让用户能看到「已取消」，也能接着重试
        if (_stage == Stage.Downloading)
        {
            CancelPending();
            return;
        }
        Close();
    }

    private void OnOpenReleases(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ReleasesPageUrl) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器也不该打断更新流程
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 关窗即放弃本次下载：.part 文件由 UpdateService 自行清理
        CancelPending();
    }
}
