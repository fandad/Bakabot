using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bakabot.Helpers;

namespace Bakabot.Services;

/// <summary>
/// 负责下载 Node.js 携带运行时、机器人基础包以及 ViaProxy JAR。
/// 支持进度回调、取消令牌。
/// </summary>
public class DownloadService
{
    private readonly HttpClient _httpClient;

    // Node.js 携带版下载地址（Windows x64）——mineflayer 4.37+ 要求 Node ≥ 22
    private const string NodeJsUrl =
        "https://nodejs.org/dist/v22.23.2/node-v22.23.2-win-x64.zip";
    private const string NodeJsOfficialBase = "https://nodejs.org/dist/";
    private const string NodeJsMirrorBase = "https://npmmirror.com/mirrors/node/";

    // GitHub 直连在国内经常被限速/断流（实测仅 30KB/s 且会中断），
    // 下载统一走“镜像优先、官方兜底”：镜像失败时自动重试官方地址。
    private const string GhMirrorPrefix = "https://gh-proxy.com/";

    // 机器人基础包下载地址
    private const string BaseAgentUrl =
        "https://zip1.webgetstore.com/2026/04/13/6b22070a3b7dc42cc840faf020ff0ff4.zip?sg=4ae7284f4af496ed2f49bfca2b8d17d1&e=69dc6ea3&fileName=minecraft-ai-agent.zip&fi=282406185";

    public DownloadService()
    {
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true   // 支持重定向
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        // GitHub API 必须携带 User-Agent
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Bakabot/1.0");
    }

    /// <summary>Node.js 运行时是否已存在</summary>
    public bool IsNodeInstalled() => File.Exists(PathHelper.NodeExePath);

    /// <summary>机器人基础包是否已下载</summary>
    public bool IsBaseAgentDownloaded() => File.Exists(PathHelper.BaseAgentZipPath);

    /// <summary>ViaProxy JAR 是否已下载</summary>
    public bool IsViaProxyDownloaded() => File.Exists(PathHelper.ViaProxyJarPath);

    /// <summary>
    /// 下载最新版 ViaProxy JAR（始终重新下载，不会因为旧文件已存在就跳过；
    /// 覆盖前先删除旧文件，避免残留旧版本）。
    /// </summary>
    public async Task DownloadViaProxyAsync(
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(PathHelper.ViaProxyDir);

        // 覆盖已有 JAR：先删掉旧的，确保拿到的确实是新版本而不是被跳过
        if (File.Exists(PathHelper.ViaProxyJarPath))
        {
            try { File.Delete(PathHelper.ViaProxyJarPath); }
            catch (IOException)
            {
                throw new InvalidOperationException(
                    "无法覆盖已有的 ViaProxy.jar（文件正被占用）。\n" +
                    "请先在设置里停止正在使用 ViaProxy 的实例，或关闭启动器后重试。");
            }
        }

        var officialUrl = await ResolveViaProxyDownloadUrlAsync(ct);
        await DownloadWithFallbackAsync(ToGhMirror(officialUrl), officialUrl,
            PathHelper.ViaProxyJarPath, progress, ct);
    }

    /// <summary>本地已安装的 ViaProxy 版本号（无 JAR 或读取失败返回 null）</summary>
    public string? GetInstalledViaProxyVersion() => TryGetInstalledViaProxyVersion();

    /// <summary>
    /// 查询 GitHub 上 ViaProxy 的最新版本号（供设置页提示"有新版本可更新"，失败返回 null）
    /// </summary>
    public async Task<string?> GetLatestViaProxyVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(
                "https://api.github.com/repos/ViaVersion/ViaProxy/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            return string.IsNullOrWhiteSpace(tag) ? null : tag!.TrimStart('v', 'V');
        }
        catch { return null; }
    }

    /// <summary>NapCat 是否已下载（解压目录存在且非空）</summary>
    public bool IsNapCatDownloaded()
    {
        try
        {
            return Directory.Exists(PathHelper.NapCatDir)
                && Directory.EnumerateFiles(PathHelper.NapCatDir, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 通过 GitHub API 获取最新版 NapCat 包并下载解压到 napcat/ 目录。
    /// </summary>
    public async Task DownloadNapCatAsync(
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(PathHelper.NapCatDir);
        var officialUrl = await ResolveNapCatDownloadUrlAsync(ct);
        await DownloadWithFallbackAsync(ToGhMirror(officialUrl), officialUrl,
            PathHelper.NapCatZipPath, progress, ct);

        // 解压前清空旧目录，保证版本干净
        if (Directory.Exists(PathHelper.NapCatDir))
            Directory.Delete(PathHelper.NapCatDir, true);
        Directory.CreateDirectory(PathHelper.NapCatDir);
        ZipFile.ExtractToDirectory(PathHelper.NapCatZipPath, PathHelper.NapCatDir);
        File.Delete(PathHelper.NapCatZipPath);
    }

    /// <summary>解析 NapCat 最新版 Windows zip 下载地址（失败则抛异常说明）</summary>
    private async Task<string> ResolveNapCatDownloadUrlAsync(CancellationToken ct)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(
                "https://api.github.com/repos/NapNeko/NapCatQQ/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);

            var zips = new List<string>();
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                zips.Add(asset.GetProperty("browser_download_url").GetString() ?? "");
            }

            // 优先 Windows 包，找不到则取任意 zip
            var win = zips.FirstOrDefault(u =>
                u.Contains("win", StringComparison.OrdinalIgnoreCase)
                || u.Contains("windows", StringComparison.OrdinalIgnoreCase));
            return win ?? zips.FirstOrDefault()
                ?? throw new InvalidOperationException("NapCat 最新版本中未找到可用的 zip 下载包");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"获取 NapCat 最新版本失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 已知可用的 ViaProxy 版本，仅作为前两种方式都失败时的最后兜底。
    /// 每次 ViaProxy 发新版本可以顺手更新这里，但正常情况下用不到它。
    /// </summary>
    private const string ViaProxyPinnedFallbackVersion = "3.4.14";

    /// <summary>从 GitHub Releases 解析最新 ViaProxy JAR 的直链（best-effort，失败返回 null）</summary>
    private async Task<string?> TryResolveViaProxyUrlFromApiAsync(CancellationToken ct)
    {
        var json = await _httpClient.GetStringAsync(
            "https://api.github.com/repos/ViaVersion/ViaProxy/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;

        var candidates = new List<string>();
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("ViaProxy", StringComparison.OrdinalIgnoreCase)) continue;
            // 不要 java8 专版，优先取"普通名字"的 jar（ViaProxy-x.y.z.jar）
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (name.Contains("java8", StringComparison.OrdinalIgnoreCase)) { candidates.Add(url!); continue; }
            return url;
        }
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>
    /// 解析要下载的 ViaProxy JAR 地址（尽量拿到最新版）：
    ///   1. GitHub API 的真实最新 release（最准，能挑掉 java8 资产）
    ///   2. releases/latest/download 重定向直链（不需要 API，规避限流）
    ///   3. 代码内记录的已知版本（最后兜底）
    /// 每次解析出的结果都与本地已装版本比较，用于提示是否需要重新下载。
    /// </summary>
    private async Task<string> ResolveViaProxyDownloadUrlAsync(CancellationToken ct)
    {
        // 1) API 拿到确切的资产链接
        try
        {
            var fromApi = await TryResolveViaProxyUrlFromApiAsync(ct);
            if (!string.IsNullOrWhiteSpace(fromApi)) return fromApi!;
        }
        catch { /* API 限流 / 网络波动，继续往下试 */ }

        // 2) 不需要 API 的最新车次链接（GitHub 会 302 到最新的同名资产）
        var latestUrl =
            $"https://github.com/ViaVersion/ViaProxy/releases/latest/download/ViaProxy-{ViaProxyPinnedFallbackVersion}.jar";
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, latestUrl);
            using var resp = await _httpClient.SendAsync(head, ct);
            if (resp.IsSuccessStatusCode) return latestUrl;
        }
        catch { /* 忽略，走兜底 */ }

        // 3) 已知版本直链
        return $"https://github.com/ViaVersion/ViaProxy/releases/download/" +
               $"v{ViaProxyPinnedFallbackVersion}/ViaProxy-{ViaProxyPinnedFallbackVersion}.jar";
    }

    /// <summary>
    /// 读取本地已安装 ViaProxy 的版本号。
    /// JAR 内没有任何版本元数据，所以从 ViaProxy 自己写的日志里取
    /// （它启动时会打印 "Initializing ViaProxy CLI v3.4.12"）。
    /// 读不到返回 null。
    /// </summary>
    private static string? TryGetInstalledViaProxyVersion()
    {
        try
        {
            if (!File.Exists(PathHelper.ViaProxyJarPath)) return null;

            var logDir = Path.Combine(PathHelper.ViaProxyDir, "logs");
            if (Directory.Exists(logDir))
            {
                // latest.log 是当前会话，debug.log 兜底；都取不到就扫最近的历史日志
                foreach (var name in new[] { "latest.log", "debug.log" })
                {
                    var v = ExtractViaProxyVersion(Path.Combine(logDir, name));
                    if (v != null) return v;
                }

                var recent = new DirectoryInfo(logDir).GetFiles("*.log")
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();
                if (recent != null) return ExtractViaProxyVersion(recent.FullName);
            }
        }
        catch { /* 读不到就算了，不影响下载功能 */ }
        return null;
    }

    /// <summary>从日志文本里提取 "Initializing ViaProxy CLI vX.Y.Z" 的版本号</summary>
    private static string? ExtractViaProxyVersion(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return null;
            using var reader = new StreamReader(logPath);
            for (var i = 0; i < 200 && reader.ReadLine() is { } line; i++)
            {
                var m = Regex.Match(line, @"ViaProxy CLI v(\d+\.\d+(?:\.\d+)?)");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>
    /// 将文件下载到指定路径，支持进度回调。
    /// </summary>
    private async Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            progress?.Report((totalRead, totalBytes));
        }
    }

    /// <summary>
    /// 下载并解压 Node.js 携带版运行时。
    /// 解压后 node.exe 放到 runtime/ 目录。
    /// </summary>
    public async Task DownloadNodeRuntimeAsync(
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        if (IsNodeInstalled()) return;

        var zipPath = Path.Combine(PathHelper.DownloadsDir, "node_runtime.zip");

        // 1. 下载（npmmirror 镜像优先，官方源兜底）
        var nodeMirrorUrl = NodeJsUrl.Replace(NodeJsOfficialBase, NodeJsMirrorBase);
        await DownloadWithFallbackAsync(nodeMirrorUrl, NodeJsUrl, zipPath, progress, ct);

        // 2. 解压（Node.js 官方 zip 包含一层目录，需要找到 node.exe）
        var tempExtract = Path.Combine(PathHelper.DownloadsDir, "node_temp");
        if (Directory.Exists(tempExtract))
            Directory.Delete(tempExtract, true);

        ZipFile.ExtractToDirectory(zipPath, tempExtract);

        // 找到 node.exe 并移动到 runtime/
        Directory.CreateDirectory(PathHelper.RuntimeDir);
        var nodeExe = Directory.GetFiles(tempExtract, "node.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (nodeExe == null)
            throw new FileNotFoundException("下载的 Node.js 压缩包未找到 node.exe");

        File.Copy(nodeExe, PathHelper.NodeExePath, overwrite: true);

        // 同时复制 npm 等文件（后续可能需要 npm install）
        var sourceDir = Path.GetDirectoryName(nodeExe)!;
        CopyDirectory(sourceDir, PathHelper.RuntimeDir);

        // 3. 清理临时文件
        Directory.Delete(tempExtract, true);
        File.Delete(zipPath);
    }

    /// <summary>
    /// 下载机器人基础包
    /// </summary>
    public async Task DownloadBaseAgentAsync(
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        // 始终重新下载以获取最新版本
        await DownloadFileAsync(BaseAgentUrl, PathHelper.BaseAgentZipPath, progress, ct);
    }

    /// <summary>
    /// 将基础包解压到指定实例目录。
    /// </summary>
    public void ExtractBaseAgentToInstance(string instanceName, bool useCustom = false)
    {
        var instanceDir = PathHelper.GetInstanceDir(instanceName);
        Directory.CreateDirectory(instanceDir);

        string zipPath = useCustom ? PathHelper.CustomBaseAgentZipPath : PathHelper.BaseAgentZipPath;

        if (!File.Exists(zipPath))
        {
            if (useCustom)
                throw new FileNotFoundException("自定义基础包未导入，请在设置中导入。");
            else
                throw new FileNotFoundException("云端基础包未下载，请在设置中下载。");
        }

        ZipFile.ExtractToDirectory(zipPath, instanceDir, overwriteFiles: true);

        // 确保 plugins 目录存在
        Directory.CreateDirectory(PathHelper.GetInstancePluginsDir(instanceName));
    }

    /// <summary>递归复制目录</summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }

    /// <summary>
    /// 镜像优先、官方兜底：镜像下载失败（限速/断流/超时）时，
    /// 自动改用官方地址重试；两个地址相同时直接下载。
    /// </summary>
    private async Task DownloadWithFallbackAsync(
        string mirrorUrl,
        string officialUrl,
        string destPath,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.Equals(mirrorUrl, officialUrl, StringComparison.OrdinalIgnoreCase))
        {
            await DownloadFileAsync(officialUrl, destPath, progress, ct);
            return;
        }

        try
        {
            await DownloadFileAsync(mirrorUrl, destPath, progress, ct);
        }
        catch
        {
            // 镜像失败不阻断：清掉半成品后重试官方地址
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { /* ignore */ }
            try
            {
                await DownloadFileAsync(officialUrl, destPath, progress, ct);
            }
            catch
            {
                // 官方源也失败：删掉半成品，避免留下无法断点续传的垃圾文件
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { /* ignore */ }
                throw;
            }
        }
    }

    /// <summary>把 GitHub 下载地址包一层国内加速镜像前缀</summary>
    private static string ToGhMirror(string url) => GhMirrorPrefix + url;
}
