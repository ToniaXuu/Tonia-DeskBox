using System.Drawing;
using System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace ToniaDeskBox.Services;

// 托盘右键菜单的深色圆角渲染器，与 WPF 菜单样式统一
public class DarkContextRenderer : WF.ToolStripProfessionalRenderer
{
    private static readonly Color Bg = Color.FromArgb(0xF2, 0x22, 0x2A, 0x38);
    private static readonly Color Hover = Color.FromArgb(0xFF, 0x3A, 0x4A, 0x6B);
    private static readonly Color Border = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
    private static readonly Color Text = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Color TextDisabled = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
    private static readonly Color Accent = Color.FromArgb(0xFF, 0x6E, 0x8B, 0xFF);

    public DarkContextRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderToolStripBackground(WF.ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Bg);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(WF.ToolStripRenderEventArgs e)
    {
        var ts = e.ToolStrip;
        var rect = new Rectangle(0, 0, ts.Width - 1, ts.Height - 1);
        using var path = RoundedPath(rect, 10);
        ts.Region?.Dispose();
        ts.Region = new Region(path);
        using var p = new Pen(Border);
        e.Graphics.DrawPath(p, path);
    }

    protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var rect = new Rectangle(1, 1, e.Item.Width - 3, e.Item.Height - 2);
        using var path = RoundedPath(rect, 6);
        using var b = new SolidBrush(Hover);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : TextDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(WF.ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderImageMargin(WF.ToolStripRenderEventArgs e) { }

    protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        var box = new Rectangle(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
        using (var path = RoundedPath(box, 4))
        using (var b = new SolidBrush(Accent))
            e.Graphics.FillPath(b, path);
        using var p = new Pen(Color.White, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        int x = r.X, y = r.Y, w = r.Width, h = r.Height;
        e.Graphics.DrawLines(p, new[]
        {
            new PointF(x + w * 0.15f, y + h * 0.55f),
            new PointF(x + w * 0.42f, y + h * 0.80f),
            new PointF(x + w * 0.85f, y + h * 0.20f),
        });
    }

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (r.Width < d || r.Height < d)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public class DarkColorTable : WF.ProfessionalColorTable
{
}
