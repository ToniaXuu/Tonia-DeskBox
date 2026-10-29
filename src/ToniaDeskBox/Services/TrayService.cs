using System.Diagnostics;
using WF = System.Windows.Forms;

namespace ToniaDeskBox.Services;

public class TrayService : IDisposable
{
    private readonly WF.NotifyIcon _notifyIcon;

    public event Action? NewBoxRequested;
    public event Action? ShowAllRequested;
    public event Action? HideAllRequested;
    public event Action<bool>? AutoStartToggled;
    public event Action? ExportRequested;
    public event Action? ExitRequested;

    public TrayService(string dataDir, bool autoStart)
    {
        var menu = new WF.ContextMenuStrip();

        menu.Items.Add("新建盒子", null, (_, _) => NewBoxRequested?.Invoke());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("显示全部", null, (_, _) => ShowAllRequested?.Invoke());
        menu.Items.Add("隐藏全部", null, (_, _) => HideAllRequested?.Invoke());
        menu.Items.Add(new WF.ToolStripSeparator());

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
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
