using System.Diagnostics;
using System.IO;
using System.Windows;
using WF = System.Windows.Forms;

namespace ToniaDeskBox.Services;

public class TrayService : IDisposable
{
    private readonly WF.NotifyIcon _notifyIcon;
    private System.Drawing.Icon? _appIcon;

    public event Action? NewBoxRequested;
    public event Action? ShowAllRequested;
    public event Action? HideAllRequested;
    public event Action? AutoSortRequested;
    public event Action<bool>? AutoStartToggled;
    public event Action? GlobalSettingsRequested;
    public event Action? ExportRequested;
    public event Action? ExitRequested;

    public TrayService(string dataDir, bool autoStart)
    {
        var menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkContextRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9.75f),
        };

        menu.Items.Add("新建盒子", null, (_, _) => NewBoxRequested?.Invoke());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("显示全部", null, (_, _) => ShowAllRequested?.Invoke());
        menu.Items.Add("隐藏全部", null, (_, _) => HideAllRequested?.Invoke());
        menu.Items.Add("整理盒子", null, (_, _) => AutoSortRequested?.Invoke());
        menu.Items.Add(new WF.ToolStripSeparator());

        var miGlobal = new WF.ToolStripMenuItem("全局盒子设置…");
        miGlobal.Click += (_, _) => GlobalSettingsRequested?.Invoke();
        menu.Items.Add(miGlobal);

        var miAuto = new WF.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = autoStart,
        };
        miAuto.CheckedChanged += (_, _) => AutoStartToggled?.Invoke(miAuto.Checked);
        menu.Items.Add(miAuto);

        menu.Items.Add("打开数据目录", null, (_, _) => Process.Start("explorer.exe", dataDir));

        var miExport = new WF.ToolStripMenuItem("导出备份（Zip）…");
        miExport.Click += (_, _) => ExportRequested?.Invoke();
        menu.Items.Add(miExport);

        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new WF.NotifyIcon
        {
            Text = "Tonia-DeskBox",
            Icon = LoadAppIcon() ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    private System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/app.ico");
            var sri = Application.GetResourceStream(uri);
            if (sri == null) return null;
            using var ms = new MemoryStream();
            sri.Stream.CopyTo(ms);
            _appIcon = new System.Drawing.Icon(new MemoryStream(ms.ToArray()));
            return _appIcon;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon?.Dispose();
    }
}
