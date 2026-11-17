using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Windows;

public enum TransferMode
{
    Export,
    Import,
}

/// <summary>
/// 导出 / 导入共用的非阻塞进度窗。
///
/// 存在的理由：导出在 UI 线程上同步跑 26 秒会把程序拖成「未响应」，
/// 进而被 Windows 挂起检测关掉（事件日志 AppHangB1）。现在压缩 / 解压都在后台线程，
/// 这个窗口只负责显示进度 + 提供取消，保证消息循环一直在转。
/// </summary>
public partial class TransferProgressWindow : Window
{
    private readonly TransferMode _mode;
    private readonly string _subjectPath;
    private bool _running = true;
    /// <summary>完成后"打开"按钮指向哪里：默认是包本体，导入成功后可改成旧数据目录。</summary>
    private string? _revealPath;
    private bool _revealIsFolder;

    /// <summary>用户点了取消（或直接关窗）时触发，由调用方取消 CancellationToken。</summary>
    public event Action? CancelRequested;

    /// <param name="mode">导出 / 导入，只影响文案与按钮。</param>
    /// <param name="subjectPath">导出时是目标 zip，导入时是来源 zip。</param>
    public TransferProgressWindow(TransferMode mode, string subjectPath)
    {
        InitializeComponent();
        _mode = mode;
        _subjectPath = subjectPath;

        // 与托盘菜单一致的取色口径：先看全局外观，没有就用第一个盒子的
        var config = (Application.Current as App)?.Config;
        var (back, opacity) = ResolveAppearance(config);
        AppearanceTheme.Attach(this).Update(
            ThemeService.ResolveColor(back), opacity,
            mode == TransferMode.Export ? "导出备份" : "导入备份", minAlpha: 0.96);

        TitleText.Text = mode == TransferMode.Export ? "导出备份" : "导入备份";
        TargetText.Text = subjectPath;
        StateText.Text = mode == TransferMode.Export ? "正在统计文件…" : "正在读取备份包…";
        DetailText.Text = "";
        CurrentText.Text = mode == TransferMode.Export
            ? "统计完成后开始压缩，期间可以取消"
            : "解压完成后才会替换当前数据，期间可以取消";
        Bar.IsIndeterminate = true;

        // 任务期间保持在最前：托盘程序没有主窗口，窗口掉到别的应用后面
        // 用户会以为又卡死了——这正是本次要根除的观感。
        Topmost = true;
    }

    private static (string BackColor, double Opacity) ResolveAppearance(Models.AppConfig? config)
    {
        if (config?.Settings.Global is { } g) return (g.BackColor, g.Opacity);
        if (config?.Boxes.FirstOrDefault() is { } b) return (b.BackColor, b.Opacity);
        return (AppearanceTheme.DefaultBackColor, AppearanceTheme.DefaultOpacity);
    }

    // ---------- 由后台进度回调（已在本 UI 线程上）驱动 ----------

    public void Report(TransferProgress p)
    {
        if (!_running) return;

        if (p.Counting)
        {
            Bar.IsIndeterminate = true;
            StateText.Text = "正在统计文件…";
            DetailText.Text = p.FilesTotal > 0 ? $"{p.FilesTotal} 个文件 · {FormatSize(p.BytesTotal)}" : "";
            return;
        }

        if (Bar.IsIndeterminate) Bar.IsIndeterminate = false;
        StateText.Text = _mode == TransferMode.Export ? "正在压缩…" : "正在解压…";

        int percent = p.BytesTotal > 0
            ? (int)Math.Clamp(p.BytesDone * 100 / p.BytesTotal, 0, 100)
            : 0;
        Bar.Value = percent;

        DetailText.Text = $"{percent}% · {FormatSize(p.BytesDone)} / {FormatSize(p.BytesTotal)}";
        CurrentText.Text = p.Current;
    }

    /// <summary>解压完成、开始替换目录时用：进度条已满，但任务还没结束。</summary>
    public void SetPhase(string state)
    {
        if (!_running) return;
        Bar.IsIndeterminate = false;
        Bar.Value = 100;
        StateText.Text = state;
        // 阶段切换后旧的百分比/字节数已不成立，留着会自相矛盾（"正在替换数据… 37%"）
        DetailText.Text = "";
    }

    /// <param name="revealPath">完成后"打开"按钮指向的目录；null 则打开包所在位置（仅导出）。</param>
    public void Complete(TransferResult result, string? note = null, string? revealPath = null)
    {
        _running = false;

        Bar.IsIndeterminate = false;
        Bar.Value = 100;
        StateText.Text = _mode == TransferMode.Export ? "导出完成" : "导入完成";
        DetailText.Text = $"{result.FilesWritten} 个文件 · {FormatSize(result.BytesTotal)} · 用时 {result.Elapsed.TotalSeconds:0.0}s";

        CurrentText.Text = note ?? (_mode == TransferMode.Export
            ? "已包含各盒子的结构与配置，可在另一台机器直接恢复"
            : "盒子已按备份内容重建");
        CurrentText.Visibility = Visibility.Visible;

        CancelBtn.Visibility = Visibility.Collapsed;
        DoneBtn.Visibility = Visibility.Visible;

        // 导出完给"打开所在文件夹"；导入完给"打开旧数据" —— 那条路径是长字符串，
        // 显示不全也没关系，一个按钮直接带过去比留在文本里更实用
        if (revealPath != null)
        {
            _revealPath = revealPath;
            _revealIsFolder = true;
            OpenFolderBtn.Content = "打开旧数据";
            OpenFolderBtn.Visibility = Visibility.Visible;
            // 导入成功后，来源 zip 已经不重要，把这一行让给"旧数据在哪"
            TargetText.Text = "旧数据：" + revealPath;
            TargetText.ToolTip = revealPath;
        }
        else
        {
            OpenFolderBtn.Visibility = _mode == TransferMode.Export ? Visibility.Visible : Visibility.Collapsed;
        }
        Topmost = false;
        DoneBtn.Focus();
    }

    public void Failed(string message)
    {
        _running = false;

        Bar.IsIndeterminate = false;
        Bar.Value = 0;
        StateText.Text = _mode == TransferMode.Export ? "导出失败" : "导入失败";
        DetailText.Text = "";
        CurrentText.Text = message;
        CancelBtn.Visibility = Visibility.Collapsed;
        DoneBtn.Visibility = Visibility.Visible;
        DoneBtn.Content = "关闭";
        Topmost = false;
        DoneBtn.Focus();
    }

    public void Cancelled()
    {
        _running = false;

        Bar.IsIndeterminate = false;
        StateText.Text = "已取消";
        DetailText.Text = "";
        CurrentText.Text = _mode == TransferMode.Export
            ? "未完成的压缩包已删除"
            : "已取消，当前数据没有任何改动";
        CancelBtn.Visibility = Visibility.Collapsed;
        DoneBtn.Visibility = Visibility.Visible;
        DoneBtn.Content = "关闭";
        Topmost = false;
        DoneBtn.Focus();
    }

    // ---------- 交互 ----------

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (!_running) return;
        CancelBtn.IsEnabled = false;
        StateText.Text = "正在取消…";
        // 大文件当前块读完才会响应取消，别让按钮看起来没反应
        CancelRequested?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 跑着的时候关窗 = 取消任务，避免留下一个还在写盘、却没人看得见的后台任务
        if (_running)
        {
            RequestCancel();
            return;
        }
        Close();
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = _revealPath ?? _subjectPath;
            if (_revealIsFolder || Directory.Exists(target))
                Process.Start("explorer.exe", target);
            else if (File.Exists(target))
                Process.Start("explorer.exe", $"/select,\"{target}\"");
            else
                Process.Start("explorer.exe", Path.GetDirectoryName(target) ?? target);
        }
        catch
        {
            // 资源管理器拉不起来不值得再弹一个错误框
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关窗按钮 / Alt+F4 都不许在任务途中直接离开
        if (_running)
        {
            e.Cancel = true;
            RequestCancel();
            return;
        }
        base.OnClosing(e);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1073741824.0:0.00} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1048576.0:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }
}
