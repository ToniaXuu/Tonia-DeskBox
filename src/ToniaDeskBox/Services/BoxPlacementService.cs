using System.IO;

namespace ToniaDeskBox.Services;

/// <summary>
/// 屏幕上的矩形（DIP）。刻意不依赖 WPF 类型：
/// 落位规则要能在单元测试里直接跑，不该为了一个矩形去拉整个 WindowsDesktop 框架。
/// </summary>
public readonly record struct BoxArea(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;

    /// <summary>按给定的缝计算是否相撞（<paramref name="gap"/> 会同时扩到两边）。</summary>
    public bool Overlaps(BoxArea other, double gap = 0) =>
        OverlapArea(this, other, gap) > 0;

    public override string ToString() => $"({X:0},{Y:0} {W:0}×{H:0})";

    /// <summary>
    /// 两个矩形（其中一个按 gap 外扩）的相交面积。
    ///
    /// 亚像素容差：窗口位置经 DPI 换算后常带小数（199.0000001 / 198.9998），
    /// 严格按 &gt; 0 判断会把"刚好贴边"当成重叠，进而触发一次无意义的重排。
    /// 半个 DIP 以内的相交本来就是渲染上看不出来的。
    /// </summary>
    public static double OverlapArea(BoxArea a, BoxArea b, double gap = 0)
    {
        const double tolerance = 0.5;

        double left = Math.Max(a.X, b.X - gap);
        double top = Math.Max(a.Y, b.Y - gap);
        double right = Math.Min(a.Right, b.Right + gap);
        double bottom = Math.Min(a.Bottom, b.Bottom + gap);
        double w = right - left;
        double h = bottom - top;
        if (w <= tolerance || h <= tolerance) return 0;
        return w * h;
    }
}

/// <summary>
/// 新盒子的落位：在桌面工作区里找一块**不压住已有元素**的空地。
///
/// 为什么需要它：以前新盒子按「120 + n×26, 120 + n×20」级联摆放，四个分类一次
/// 整理出来就叠成一摞 —— 26px 的错位远小于盒子宽度，视觉上等于完全重叠。
///
/// 约束（按优先级）：
/// 1. **只决定新盒子的位置**。已有盒子的坐标是用户的财产，任何整理动作都不得顺手调整；
/// 2. 尽量不压住已有盒子 / 已展开的面板（后者是盒子的一部分，压住它同样读不清）；
/// 3. 留在工作区内（任务栏、屏幕外都不该去）；
/// 4. 桌面实在放不下时，挑"重叠面积最小"的位置，而不是把整块丢回左上角。
///
/// 候选点只取「工作区左上角」和「每个障碍的右下角 + 缝」的笛卡尔积：真正要放下一块
/// 矩形的位置，左边界或上边界必定紧贴某个障碍，或者贴在容器边界上。这样不必按像素
/// 扫描，几十个盒子也只有几百个候选。
/// </summary>
public static class BoxPlacementService
{
    /// <summary>两块盒子之间至少留的缝，太贴会看成同一块。</summary>
    public const double Gap = 12;

    /// <summary>距工作区边界的留白（盒子本身还有投影，贴边会显得被裁掉）。</summary>
    public const double EdgeMargin = 16;

    /// <summary>
    /// 自适应盒子（W/H = 0）的估算尺寸。真实尺寸由 SizeToContent 决定，
    /// 这里只用于"先把位置摆对"，窗口显示后还会按实测尺寸复核一次。
    /// </summary>
    public const double DefaultWidth = 210;
    public const double DefaultHeight = 96;

    /// <summary>
    /// 给一个宽高找空地。返回的坐标保证落在工作区内；若无完全空位，
    /// 返回重叠面积最小的那个候选。
    /// </summary>
    /// <param name="preferred">
    /// 希望靠近的位置（默认工作区左上）。多个空位都可用时选离它最近的那个：
    /// 初次摆放时盒子会挨着左上逐格铺开，而"实测尺寸偏小、需要重排"时
    /// 只会在原地附近微调，不会突然跳回屏幕另一头。
    /// </param>
    public static (double X, double Y) FindFreeSpot(
        BoxArea workArea, double width, double height,
        IReadOnlyList<BoxArea>? obstacles = null,
        (double X, double Y)? preferred = null)
    {
        if (workArea.W <= 0 || workArea.H <= 0) return (workArea.X, workArea.Y);

        obstacles ??= Array.Empty<BoxArea>();

        // 装不下的尺寸强行压进可用区域：宁可放小一点，也不能把盒子甩到屏幕外
        double w = Math.Clamp(width > 0 ? width : DefaultWidth, 40, Math.Max(40, workArea.W - EdgeMargin * 2));
        double h = Math.Clamp(height > 0 ? height : DefaultHeight, 40, Math.Max(40, workArea.H - EdgeMargin * 2));

        double minX = workArea.X + EdgeMargin;
        double minY = workArea.Y + EdgeMargin;
        double maxX = Math.Max(minX, workArea.Right - EdgeMargin - w);
        double maxY = Math.Max(minY, workArea.Bottom - EdgeMargin - h);

        var anchor = preferred ?? (minX, minY);

        var xs = new SortedSet<double> { minX };
        var ys = new SortedSet<double> { minY };
        foreach (var o in obstacles)
        {
            xs.Add(Math.Clamp(o.Right + Gap, minX, maxX));
            ys.Add(Math.Clamp(o.Bottom + Gap, minY, maxY));
        }

        (double X, double Y)? bestFree = null;
        double bestFreeDistance = double.MaxValue;

        (double X, double Y) bestFallback = (minX, minY);
        double bestOverlap = double.MaxValue;
        double bestFallbackDistance = double.MaxValue;

        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                var candidate = new BoxArea(x, y, w, h);

                double overlap = 0;
                foreach (var o in obstacles) overlap += BoxArea.OverlapArea(candidate, o, Gap);

                double distance = Distance(anchor, x, y);

                if (overlap <= 0)
                {
                    // 完全空的位置里挑离期望点最近的
                    if (distance < bestFreeDistance)
                    {
                        bestFreeDistance = distance;
                        bestFree = (x, y);
                    }
                }
                else if (overlap < bestOverlap - 0.5
                         || (Math.Abs(overlap - bestOverlap) <= 0.5 && distance < bestFallbackDistance))
                {
                    bestOverlap = overlap;
                    bestFallbackDistance = distance;
                    bestFallback = (x, y);
                }
            }
        }

        return bestFree ?? bestFallback;
    }

    /// <summary>到锚点的欧氏距离；平方即可比较大小，不必开方。</summary>
    private static double Distance((double X, double Y) anchor, double x, double y)
    {
        double dx = x - anchor.X;
        double dy = y - anchor.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// 批量给新盒子排位。已有的矩形原样进障碍集、一个都不改，
    /// 新加进去的只追加 —— 这就是「只整理新增盒子」的语义。
    /// </summary>
    public static IReadOnlyList<BoxArea> Arrange(
        BoxArea workArea,
        IReadOnlyList<BoxArea> existing,
        IReadOnlyList<(double W, double H)> sizes)
    {
        var obstacles = (existing ?? Array.Empty<BoxArea>()).ToList();
        var placed = new List<BoxArea>(sizes.Count);

        foreach (var (w, h) in sizes)
        {
            var (x, y) = FindFreeSpot(workArea, w, h, obstacles);
            var area = new BoxArea(x, y,
                w > 0 ? w : DefaultWidth,
                h > 0 ? h : DefaultHeight);
            placed.Add(area);
            obstacles.Add(area);
        }

        return placed;
    }
}

/// <summary>
/// 「一键恢复」之后的盒子清理。
///
/// 整理时按分类新建的盒子，在文件全部搬回桌面后就成了空壳；
/// 留在桌面上既占地方，又让人以为整理没撤销干净。
/// 但删盒子是不可逆动作，所以判据收得很紧：
/// - 只处理**本次恢复涉及的盒子**（快照里出现过的名字），不碰用户自己建的空盒子身份不明的问题；
/// - 必须目录真实存在且**里面一个条目都没有**；
/// - 目录不存在、或还剩任何文件/子目录（例如恢复时因同名被跳过），一律保留。
/// </summary>
public static class TidyBoxCleanupService
{
    private static readonly HashSet<string> IgnoredEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db",
    };

    /// <summary>盒目录是否已经空了。目录不存在时返回 false —— 那是异常状态，不该顺手删配置。</summary>
    public static bool IsEmpty(string? boxDir)
    {
        if (string.IsNullOrWhiteSpace(boxDir) || !Directory.Exists(boxDir)) return false;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(boxDir))
            {
                var name = Path.GetFileName(entry);
                if (IgnoredEntries.Contains(name)) continue;
                return false;
            }
            return true;
        }
        catch
        {
            // 读不了就当"不空"：删盒子是可逆性最差的一步，宁可不删
            return false;
        }
    }

    /// <summary>
    /// 从本次恢复涉及的盒子名里挑出已经空了的。
    /// <paramref name="dirOf"/> 返回 null 表示这个盒子不该参与清理（映射盒 / 配置里已不存在）。
    /// </summary>
    public static IReadOnlyList<string> FindEmptiedBoxes(
        IEnumerable<string> involvedBoxNames, Func<string, string?> dirOf)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in involvedBoxNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add(name)) continue;

            string? dir;
            try { dir = dirOf(name); }
            catch { continue; }

            if (IsEmpty(dir)) result.Add(name);
        }

        return result;
    }
}
