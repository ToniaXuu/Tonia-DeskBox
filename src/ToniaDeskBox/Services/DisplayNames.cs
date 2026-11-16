using System.IO;

namespace ToniaDeskBox.Services;

/// <summary>
/// 盒子内文件的「显示名」计算。
///
/// 收纳盒里绝大多数是桌面快捷方式（.lnk），把扩展名原样铺在界面上既啰嗦、
/// 又会把本来能放下的一行名字挤成两行（例如 “CodeBuddy CN.lnk” 会被拆成
/// “CodeBuddy.l” + “nk”）。桌面上系统本来就隐藏已知扩展名，这里保持一致。
///
/// 唯一要小心的是重名：<c>a.lnk</c> 与 <c>a.exe</c> 去掉扩展名后都叫 <c>a</c>，
/// 两个格子看起来一模一样。这种情况下保留完整文件名，宁可啰嗦也不误导。
/// </summary>
public static class DisplayNames
{
    /// <summary>批量计算显示名，返回「真实文件名 → 显示名」映射。</summary>
    public static Dictionary<string, string> Build(IEnumerable<string> fileNames)
    {
        var names = fileNames.ToList();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in names)
        {
            map[n] = n.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(n)
                : n;
        }

        // 去后缀后撞名的那一组，全部退回完整文件名
        var collided = map.Values
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (collided.Count > 0)
        {
            foreach (var key in map.Keys.ToList())
            {
                if (collided.Contains(map[key]))
                    map[key] = key;
            }
        }

        return map;
    }

    /// <summary>单项场景（无重名顾虑）的快捷入口。</summary>
    public static string Of(string fileName) =>
        fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
}
