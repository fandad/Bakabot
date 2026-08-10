using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
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
    /// 从 GitHub Releases 下载最新版 ViaProxy JAR。
    /// 先通过 GitHub API 获取最新 Release 资产链接，失败则回退到已知版本链接。
    /// </summary>
    public async Task DownloadViaProxyAsync(
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(PathHelper.ViaProxyDir);
        var downloadUrl = await ResolveViaProxyDownloadUrlAsync(ct);
        await DownloadFileAsync(downloadUrl, PathHelper.ViaProxyJarPath, progress, ct);
    }

    /// <summary>通过 GitHub API 解析最新 ViaProxy 下载地址，失败则回退到固定链接</summary>
    private async Task<string> ResolveViaProxyDownloadUrlAsync(CancellationToken ct)
    {
        const string fallbackUrl =
            "https://github.com/ViaVersion/ViaProxy/releases/download/v3.4.12/ViaProxy-3.4.12.jar";
        try
        {
            var json = await _httpClient.GetStringAsync(
                "https://api.github.com/repos/ViaVersion/ViaProxy/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("ViaProxy") && name.EndsWith(".jar")
                    && !name.Contains("java8", StringComparison.OrdinalIgnoreCase))
                    return asset.GetProperty("browser_download_url").GetString() ?? fallbackUrl;
            }
        }
        catch { /* API 限流或网络问题，回退到已知版本 */ }
        return fallbackUrl;
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

        // 1. 下载
        await DownloadFileAsync(NodeJsUrl, zipPath, progress, ct);

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
}
