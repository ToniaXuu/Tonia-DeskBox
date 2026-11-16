using System.IO;
using ToniaDeskBox.Services;

// 用法：
//   dotnet run --project tools/UpdateProbe                 → 查真实更新源
//   set TONIA_UPDATE_API=http://127.0.0.1:8899/latest.json  → 查本地假数据，验证解析与下载
//   加 --download 会真的把安装包拉下来并校验大小/哈希

Console.OutputEncoding = System.Text.Encoding.UTF8;
bool download = args.Contains("--download", StringComparer.OrdinalIgnoreCase);

Console.WriteLine($"当前版本 : {UpdateService.CurrentVersion}");
Console.WriteLine($"更新源   : {UpdateService.ApiUrl}");

try
{
    var check = await UpdateService.CheckAsync();
    Console.WriteLine($"远端最新 : {check.Latest}");

    if (check.Available is not { } info)
    {
        Console.WriteLine("结论     : 已是最新，不提示更新。");
        return 0;
    }

    Console.WriteLine($"结论     : 发现新版本 {info.Version}");
    Console.WriteLine($"  资产名 : {info.AssetName}");
    Console.WriteLine($"  大小   : {info.Size:N0} 字节");
    Console.WriteLine($"  哈希   : {info.Sha256 ?? "(发布方未提供)"}");
    Console.WriteLine($"  下载页 : {info.DownloadUrl}");
    Console.WriteLine($"  说明   : {(info.Notes.Length > 60 ? info.Notes[..60] + "…" : info.Notes)}");

    if (!download) return 0;

    double lastPct = -1;
    var progress = new Progress<double>(p =>
    {
        int pct = (int)(p * 100);
        if (pct / 10 == (int)lastPct / 10 && pct != 100) return;
        lastPct = pct;
        Console.WriteLine($"  下载中 {pct}%");
    });

    var path = await UpdateService.DownloadAsync(info, progress);
    var fi = new FileInfo(path);
    Console.WriteLine($"下载完成 : {fi.FullName}");
    Console.WriteLine($"  实际大小: {fi.Length:N0} 字节（与发布信息一致：{fi.Length == info.Size}）");
    return 0;
}
catch (UpdateException ex)
{
    Console.WriteLine($"失败     : {ex.Message}");
    return 1;
}
