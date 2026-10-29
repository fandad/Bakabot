using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Bakabot.Models;
using Bakabot.Views.Dialogs;

namespace Bakabot.Services;

/// <summary>
/// 更新检查：查询 GitHub Releases 的**最新正式版**并与本地版本比较。
///
/// 历史说明：本类原先请求的是 `http://localhost:3000/update` —— 那是 ARCbot 时代
/// 作者自建更新服务的遗留地址（v2.1.0 引入）。该地址在实际用户机器上**没有任何进程监听**，
/// 所以每次启动都要白等 5 秒超时、并且永远不可能提示更新。现改为查询本仓库的 GitHub Releases。
///
/// 另一个历史坑：旧的 UpdateDialog 会下载一个 **.exe** 并调用批处理替换自身。
/// 但本项目的发布产物是 **zip 包**（内含 exe + README + 基础包），那个自替换流程对 zip 无意义
/// 且会破坏安装。因此现在改为**只提示 + 打开下载页**，由用户自行下载覆盖，不再尝试自动替换。
/// </summary>
public class UpdateService
{
    /// <summary>当前版本号（发版时同步更新此处，与 Bakabot.csproj 的 &lt;Version&gt; 保持一致）</summary>
    private readonly string _currentVersion = "2.9.2";

    /// <summary>本仓库的 Releases API（GitHub 会重定向/直接返回最新正式版，不含 draft 与 prerelease）</summary>
    private const string LatestReleaseApi = "https://api.github.com/repos/fandad/Bakabot/releases/latest";

    /// <summary>更新失败时的兜底落地页</summary>
    private const string ReleasesPageFallback = "https://github.com/fandad/Bakabot/releases/latest";

    private readonly SettingsService _settingsService;

    public UpdateService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task CheckUpdateAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            // GitHub API 要求带 User-Agent，否则直接 403
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Bakabot-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json = await client.GetStringAsync(LatestReleaseApi);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // tag_name 形如 "Bakabot_v2.8.1"，取出版本数字部分
            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
            string versionText = ExtractVersion(tag);
            if (string.IsNullOrEmpty(versionText) || !Version.TryParse(versionText, out var remoteVersion))
                return;

            if (remoteVersion <= new Version(_currentVersion))
                return;   // 已是最新

            // 用户勾选过"跳过此版本"则静默退出
            if (_settingsService.Settings.SkippedVersion == versionText)
                return;

            var info = new UpdateInfo
            {
                Version = versionText,
                ReleaseNotes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? string.Empty) : string.Empty,
                // 优先给"某个资源的下载地址"，否则退回 release 页面
                DownloadUrl = PickAssetUrl(root) ?? (root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? ReleasesPageFallback) : ReleasesPageFallback)
            };

            Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new UpdateDialog(info);
                bool? result = dialog.ShowDialog();

                // 用户取消且勾了"跳过此版本"才记录；点"去下载"不算跳过
                if (result != true && dialog.IsSkipChecked)
                    SaveSkippedVersionToSettings(versionText);
            });
        }
        catch
        {
            // 网络异常 / 被限流 / 解析失败：静默处理，绝不能打扰用户或阻塞启动
        }
    }

    /// <summary>从 tag 里取出版本号：`Bakabot_v2.8.1` / `v2.8.1` / `2.8.1` 都要能处理</summary>
    private static string ExtractVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"\d+(?:\.\d+)+");
        return m.Success ? m.Value : string.Empty;
    }

    /// <summary>优先挑 zip 资源；没有就挑第一个；都没有返回 null</summary>
    private static string? PickAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string? first = null;
        foreach (var a in assets.EnumerateArray())
        {
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) continue;
            first ??= url;
            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return url;
        }
        return first;
    }

    private void SaveSkippedVersionToSettings(string version)
    {
        _settingsService.UpdateSettings(settings =>
        {
            settings.SkippedVersion = version;
        });
    }
}
