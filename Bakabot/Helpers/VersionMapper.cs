using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bakabot.Helpers;

/// <summary>
/// Minecraft 版本号映射工具
/// 支持 2026 年起的新年份命名版本（如 26.1、26.2）
/// 核心逻辑：26.x 等 mineflayer 不原生支持的新版本，会被映射到 mineflayer 实际支持的版本，
/// 配合服务器 ViaVersion 等跨版本插件实现连接
/// </summary>
public static class VersionMapper
{
    /// <summary>
    /// mineflayer 官方支持的版本列表（截至 4.37.x）
    /// 不在此列表中的版本号会导致 mineflayer 直接抛出异常退出
    /// </summary>
    private static readonly HashSet<string> SupportedVersions = new()
    {
        "1.8", "1.8.8", "1.8.9",
        "1.9", "1.9.2", "1.9.4",
        "1.10", "1.10.2",
        "1.11", "1.11.2",
        "1.12", "1.12.1", "1.12.2",
        "1.13", "1.13.1", "1.13.2",
        "1.14", "1.14.1", "1.14.2", "1.14.3", "1.14.4",
        "1.15", "1.15.1", "1.15.2",
        "1.16", "1.16.1", "1.16.2", "1.16.3", "1.16.4", "1.16.5",
        "1.17", "1.17.1",
        "1.18", "1.18.1", "1.18.2",
        "1.19", "1.19.1", "1.19.2", "1.19.3", "1.19.4",
        "1.20", "1.20.1", "1.20.2", "1.20.3", "1.20.4", "1.20.5", "1.20.6",
        "1.21", "1.21.1", "1.21.2", "1.21.3", "1.21.4",
        "1.21.5", "1.21.6", "1.21.7", "1.21.8", "1.21.9", "1.21.10", "1.21.11"
    };

    /// <summary>
    /// 26.x 新版本默认映射到的 mineflayer 兼容版本
    /// 选择 1.20.1 是因为 ViaVersion 对该版本支持最好，兼容性最高
    /// </summary>
    private const string DefaultFallbackVersion = "1.20.1";

    /// <summary>
    /// 使用 ViaProxy 时，mineflayer 应连接的版本。
    /// 选择最新且 mineflayer 原生支持的版本，让 ViaProxy 负责对上高版本服务器的协议转换。
    /// </summary>
    public const string ClientVersionForViaProxy = "1.21.4";

    /// <summary>
    /// 常用版本列表（用于 UI 下拉推荐）
    /// 注意：26.x 版本会被自动映射到兼容版本，不会直接传给 mineflayer
    /// </summary>
    public static readonly List<string> CommonVersions = new()
    {
        // 新年份命名版本（2026+，自动映射到兼容版本）
        "26.2",
        "26.1",
        // mineflayer 原生支持的版本
        "1.21.4",
        "1.21.1",
        "1.20.4",
        "1.20.1",
        "1.19.4",
        "1.19.2",
        "1.18.2",
        "1.16.5",
        "1.12.2",
        "1.8.9",
    };

    // ─── ViaProxy 版本支持 ───

    /// <summary>
    /// 判断该版本是否需要通过 ViaProxy 才能连据（即 mineflayer 不原生支持的版本）。
    /// 注意：与 IsDirectlySupported 的区别是——这里判断的是“需要 ViaProxy”，
    /// 而非“需要服务器装 ViaVersion”。
    /// </summary>
    public static bool RequiresViaProxy(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        // 已原生支持的版本不需要 ViaProxy
        return !IsDirectlySupported(version);
    }

    /// <summary>
    /// 将用户版本字符串转换为 ViaProxy 的目标服务器版本名。
    /// 对于 26.x 等 Minecraft 年份命名版本，直接使用原始字符串。
    /// </summary>
    public static string ToViaProxyVersionName(string version)
    {
        var v = version?.Trim() ?? string.Empty;
        // 26.x 等年份命名版本：ViaProxy 直接识别这种命名（如 "26.1"、"26.2"）
        if (Regex.IsMatch(v, @"^\d{2}\.\d+")) return v;
        // 1.x.x 格式的理论上应已在 SupportedVersions 中，但万一越界也直接传给 ViaProxy
        return v;
    }

    // ─── 现有符合项 ───

    /// <summary>
    /// 将用户输入的版本号转换为 mineflayer 可识别的版本号
    /// 核心：26.x 等不支持的版本不会直接传给 mineflayer，而是映射到兼容版本
    /// </summary>
    /// <param name="inputVersion">用户输入的版本号</param>
    /// <returns>mineflayer 实际使用的版本号</returns>
    public static string ToMineflayerVersion(string inputVersion)
    {
        if (string.IsNullOrWhiteSpace(inputVersion))
            return DefaultFallbackVersion;

        var version = inputVersion.Trim();

        // 1. 如果已经是 mineflayer 支持的版本，直接返回
        if (SupportedVersions.Contains(version))
            return version;

        // 2. 匹配 26.x 年份版本格式（如 26.1、26.2、26.10）
        //    不映射到 1.26.x（mineflayer 不认识会崩溃），而是映射到兼容版本
        if (Regex.IsMatch(version, @"^\d{2}\.\d+"))
            return DefaultFallbackVersion;

        // 3. 匹配 1.26.x 格式（传统格式但 mineflayer 还不支持）
        if (Regex.IsMatch(version, @"^1\.(2[6-9]|[3-9]\d+)\."))
            return DefaultFallbackVersion;

        // 4. 其他不认识的版本号，也用兼容版本兜底，避免崩溃
        return DefaultFallbackVersion;
    }

    /// <summary>
    /// 判断是否为新年份命名版本（如 26.1）
    /// </summary>
    public static bool IsYearVersion(string version)
    {
        return Regex.IsMatch(version?.Trim() ?? "", @"^\d{2}\.\d+");
    }

    /// <summary>
    /// 判断版本号是否被 mineflayer 原生支持
    /// </summary>
    public static bool IsDirectlySupported(string version)
    {
        return !string.IsNullOrEmpty(version) && SupportedVersions.Contains(version.Trim());
    }

    /// <summary>
    /// 获取版本显示名称（用于 UI 提示）
    /// </summary>
    public static string GetVersionDisplayName(string version)
    {
        if (IsYearVersion(version))
        {
            return $"{version} (兼容模式，使用 {DefaultFallbackVersion} 协议)";
        }
        if (!IsDirectlySupported(version))
        {
            return $"{version} (兼容模式，使用 {DefaultFallbackVersion} 协议)";
        }
        return version;
    }
}
