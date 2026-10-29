using System.Diagnostics;
using WF = System.Windows.Forms;

namespace ToniaDeskBox.Services;

public class TrayService : IDisposable
{
    private readonly WF.NotifyIcon _notifyIcon;

    public event Action? NewBoxRequested;
    public event Action? ExitRequested;

    public TrayService(string dataDir)
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("新建盒子", null, (_, _) => NewBoxRequested?.Invoke());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("打开数据目录", null, (_, _) => Process.Start("explorer.exe", dataDir));
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
