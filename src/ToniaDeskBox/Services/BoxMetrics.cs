namespace ToniaDeskBox.Services;

/// <summary>
/// 内嵌图标网格的尺寸常量，盒子、面板与配置迁移共用同一套数字。
///
/// 这些值必须和 <c>BoxWindow.xaml</c> 里 ItemTemplate 的 <c>Width="76" Height="88" Margin="2"</c>
/// 保持一致：一处改、另一处忘了改，格子就会和盒子边界对不上。
/// 之所以要有 <see cref="CellPitch"/>，是因为盒子宽度必须落在「整列」上——
/// 否则最后一列右侧永远空一截，拖动改宽时还会因为换行点来回跳而抖动。
/// </summary>
public static class BoxMetrics
{
    /// <summary>单元格本体宽度（DIP）。</summary>
    public const double CellWidth = 76;

    /// <summary>左右各 2px 外边距，合起来构成列间距。</summary>
    public const double CellGap = 4;

    /// <summary>一列占用的水平空间。</summary>
    public const double CellPitch = CellWidth + CellGap;

    /// <summary>
    /// 盒子左右占位之和：RootBorder 的 1px 描边 + 12px 内边距，两侧各 13。
    /// 少算这 1px 描边，四列就会差 2px 而变成三列。
    /// </summary>
    public const double PaddingH = 26;

    public const int MinColumns = 3;
    public const int MaxColumns = 7;

    /// <summary>三列盒子对应的最小宽度。低于这个值就放不下内嵌网格了。</summary>
    public const double ContentMinWidth = MinColumns * CellPitch + PaddingH;

    /// <summary>把宽度吸附到整列，返回吸附后的宽度与列数。</summary>
    public static double Snap(double rawWidth, out int columns)
    {
        int cols = (int)Math.Round((rawWidth - PaddingH) / CellPitch);
        columns = Math.Clamp(cols, MinColumns, MaxColumns);
        return columns * CellPitch + PaddingH;
    }

    /// <summary>按宽度算能放几列（不吸附，仅用于查询）。</summary>
    public static int ColumnsOf(double width) =>
        Math.Clamp((int)Math.Floor((width - PaddingH) / CellPitch), MinColumns, MaxColumns);
}
