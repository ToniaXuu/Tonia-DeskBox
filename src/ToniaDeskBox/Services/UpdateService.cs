using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ToniaDeskBox.Services;

/// <summary>一次可用的新版本描述。字段全部取自 Release 资产，不猜。</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Notes,
    string AssetName,
    Uri DownloadUrl,
    long Size,
    string? Sha256);

/// <summary>检查结果：Latest 用于「已是最新」提示，Available 非空表示有新版本。</summary>
public sealed record UpdateCheck(Version Latest, UpdateInfo? Available);

/// <summary>更新流程里可以直接展示给用户的错误。</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 检查 / 下载 / 启动安装包。
///
/// 版本来源只有一个：本程序集版本（csproj 的 Version）。
/// 远端版本取自 GitHub Releases 的最新一条，安装包资产名固定为 ToniaDeskBox-Setup-x.y.z.exe。
///
/// 这里刻意不碰任何用户数据：下载只写 %LOCALAPPDATA%\Tonia-DeskBox\updates，
/// 安装由安装包负责，而安装包本身对 boxes / config.json 一律只读不写。
/// </summary>
public static class UpdateService
{
    private const string Owner = "ToniaXuu";
    private const string Repo = "Tonia-DeskBox";
    private const string AssetPrefix = "ToniaDeskBox-Setup-";

    /// <summary>
    /// 更新源。默认走 GitHub Releases（仓库公开，匿名可读，不需要令牌）。
    /// 想临时指向镜像 / 本地调试时，设置环境变量 TONIA_UPDATE_API 覆盖即可。
    /// </summary>
    public static string ApiUrl =>
        Environment.GetEnvironmentVariable("TONIA_UPDATE_API") is { Length: > 0 } custom
            ? custom
            : $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases";

    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>安装包下载到这里。只放大安装包，与配置/盒子数据分开，互不影响。</summary>
    public static string UpdatesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tonia-DeskBox", "updates");

    private static Version ReadCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(informational, out var v)) return v;
        return asm.GetName().Version ?? new Version(0, 0, 0);
    }

    /// <summary>
    /// "0.2.0" / "v0.2.0" / "0.2.0+sha" / "0.2.0-beta.1" 统一取前面的数字段。
    /// 取不到就返回 false，交给调用方给出可读错误。
    /// </summary>
    private static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        int end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        text = text[..end].TrimEnd('.');

        return Version.TryParse(text, out version!);
    }

    /// <summary>
    /// 查询最新版本。有新版本返回 Available，否则 Available 为 null。
    /// 网络/解析失败抛 UpdateException，消息可直接显示给用户。
    /// </summary>
    public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        using var http = CreateHttpClient(TimeSpan.FromSeconds(20));

        string json;
        try
        {
            using var resp = await http.GetAsync(ApiUrl, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new UpdateException(
                    "更新源里还没有发布过版本。\n发布第一个 Release 后即可在此检查更新。");
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                resp.StatusCode == (System.Net.HttpStatusCode)429)
                throw new UpdateException("查询过于频繁被 GitHub 限流了，请过一会儿再试。");
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"更新服务器返回 {(int)resp.StatusCode}，暂时无法检查更新。");

            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (UpdateException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            throw new UpdateException("连接更新服务器超时，请检查网络后重试。");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("连不上更新服务器，请检查网络或代理设置。", ex);
        }

        return ParseRelease(json);
    }

    private static UpdateCheck ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var latest))
                throw new UpdateException($"无法识别远端版本号「{tag}」。");

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            UpdateInfo? best = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

                    var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0L;
                    // GitHub 从 2025 起给资产带 digest，形如 "sha256:ab12..."
                    var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest[7..]
                        : null;

                    best = new UpdateInfo(latest, tag, notes, name, uri, size, sha);
                    break;
                }
            }

            if (best is null)
                throw new UpdateException(
                    $"找到了版本 {tag}，但里面没有安装包资产（{AssetPrefix}x.y.z.exe）。");

            return new UpdateCheck(latest, latest > CurrentVersion ? best : null);
        }
        catch (UpdateException) { throw; }
        catch (JsonException ex)
        {
            throw new UpdateException("更新信息格式无法识别，可能是更新源地址不对。", ex);
        }
    }

    /// <summary>
    /// 下载安装包，返回本地路径。已存在且大小一致的包直接复用，避免重复下载。
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdatesDir);
        var target = Path.Combine(UpdatesDir, info.AssetName);
        var partial = target + ".part";

        if (File.Exists(target) && (info.Size <= 0 || new FileInfo(target).Length == info.Size))
        {
            progress?.Report(1);
            return target;
        }

        using var http = CreateHttpClient(TimeSpan.FromMinutes(30));
        try
        {
            using var resp = await http.GetAsync(
                info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"下载安装包失败（HTTP {(int)resp.StatusCode}）。");

            var total = resp.Content.Headers.ContentLength ?? info.Size;
            long received = 0;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total > 0) progress?.Report(Math.Min(1d, (double)received / total));
                }
            }

            if (total > 0 && received != total)
                throw new UpdateException($"安装包下载不完整（{received}/{total} 字节），请重试。");

            if (info.Sha256 is { Length: > 0 })
            {
                var actual = await ComputeSha256Async(partial, ct).ConfigureAwait(false);
                if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("安装包校验失败（哈希不一致），已放弃本次更新。");
            }

            File.Move(partial, target, overwrite: true);
            progress?.Report(1);
            return target;
        }
        catch (UpdateException)
        {
            TryDelete(partial);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            throw new UpdateException("下载安装包时出错：" + ex.Message, ex);
        }
    }

    /// <summary>
    /// 静默覆盖安装。安装包会自己关掉本进程、替换程序文件、再重新拉起，
    /// 全程不碰 boxes 与 config.json。
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
            throw new UpdateException("安装包不见了，请重新下载。");

        try
        {
            var psi = new ProcessStartInfo(installerPath)
            {
                // /SILENT 保留安装进度条，比 /VERYSILENT 更让人安心；
                // /CLOSEAPPLICATIONS 允许安装包在程序仍占用文件时结束它。
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new UpdateException("无法启动安装程序：" + ex.Message, ex);
        }
    }

    /// <summary>只保留最新一个安装包，避免 updates 目录越攒越大。</summary>
    public static void CleanupOldInstallers(string keepFileName)
    {
        try
        {
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (var f in Directory.EnumerateFiles(UpdatesDir, AssetPrefix + "*.exe"))
            {
                if (string.Equals(Path.GetFileName(f), keepFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                TryDelete(f);
            }
            foreach (var f in Directory.EnumerateFiles(UpdatesDir, "*.part"))
                TryDelete(f);
        }
        catch
        {
            // 清理失败不影响更新本身
        }
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ToniaDeskBox/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
