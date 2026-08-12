using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Bakabot.Helpers;

/// <summary>
/// Java 运行时检测工具。
/// 用于为 ViaProxy 协议代理功能定位可用的 java.exe。
/// 
/// 检测优先级：
///   1. 用户在设置中指定的自定义路径
///   2. JAVA_HOME 环境变量
///   3. 系统 PATH
///   4. Minecraft 官方启动器内置 JRE（最常见情况，MC 玩家必备）
///   5. 通用安装目录（Eclipse Adoptium、Oracle 等）
/// </summary>
public static class JavaHelper
{
    /// <summary>
    /// 查找可用的 java.exe 路径。
    /// </summary>
    /// <param name="customPath">用户自定义路径（优先级最高），可为 null 或空</param>
    /// <returns>java.exe 完整路径；未找到则返回 null</returns>
    public static string? FindJava(string? customPath = null)
    {
        // 1. 用户自定义路径
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (File.Exists(customPath)) return customPath;
            // 支持传入目录路径，自动补全 bin/java.exe
            var inBin = Path.Combine(customPath.Trim(), "bin", "java.exe");
            if (File.Exists(inBin)) return inBin;
        }

        // 2. JAVA_HOME 环境变量
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var javaExe = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(javaExe)) return javaExe;
        }

        // 3. 系统 PATH
        var fromPath = FindInSystemPath("java.exe");
        if (fromPath != null) return fromPath;

        // 4. Minecraft 官方启动器内置 JRE
        //    路径格式: %APPDATA%\.minecraft\runtime\<runtime-name>\windows[-x64]\<runtime-name>\bin\java.exe
        var mcRuntimeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft", "runtime");

        if (Directory.Exists(mcRuntimeDir))
        {
            // 按版本优先级排序（java-runtime-delta = Java 21，MC 1.21+ 使用）
            var runtimeNames = new[]
            {
                "java-runtime-delta",   // Java 21 (MC 1.21+)
                "java-runtime-gamma",   // Java 17 (MC 1.18–1.20)
                "java-runtime-beta",    // Java 17
                "java-runtime-alpha",   // Java 16
            };

            foreach (var name in runtimeNames)
            {
                // 尝试多种平台子目录格式
                foreach (var platform in new[] { "windows", "windows-x64" })
                {
                    var candidate = Path.Combine(mcRuntimeDir, name, platform, name, "bin", "java.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // 兜底：全局搜索（覆盖非标准路径格式）
            try
            {
                var found = Directory.GetFiles(mcRuntimeDir, "java.exe", SearchOption.AllDirectories);
                if (found.Length > 0)
                    return found.OrderByDescending(File.GetLastWriteTime).First();
            }
            catch { /* 忽略权限错误 */ }
        }

        // 5. 通用安装目录
        var searchDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft"),
            @"C:\Program Files\Common Files\Oracle\Java\javapath",
        };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var found = Directory.GetFiles(dir, "java.exe", SearchOption.AllDirectories);
                if (found.Length > 0)
                    return found.OrderByDescending(File.GetLastWriteTime).First();
            }
            catch { /* 忽略权限错误 */ }
        }

        return null;
    }

    /// <summary>Java 是否可用</summary>
    public static bool IsJavaAvailable(string? customPath = null)
        => FindJava(customPath) != null;

    /// <summary>
    /// 获取 Java 版本字符串，例如 "21.0.3"。
    /// 失败时返回 null。
    /// </summary>
    public static string? GetJavaVersion(string javaPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardError = true, // java -version 输出到 stderr
                CreateNoWindow = true
            };
            using var process = Process.Start(psi)!;
            var output = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            var m = Regex.Match(output, @"version ""([^""]+)""");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 获取 Java 状态描述字符串（用于 UI 显示）。
    /// </summary>
    public static string GetStatusText(string? customPath = null)
    {
        var path = FindJava(customPath);
        if (path == null) return "未找到 Java 运行时（ViaProxy 需要 Java 17+）";

        var version = GetJavaVersion(path);
        return version != null
            ? $"Java {version} 已就绪"
            : $"Java 已找到（版本未知）";
    }

    private static string? FindInSystemPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var full = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
