using System.IO;

namespace Bakabot.Helpers;

/// <summary>
/// ViaProxy.jar 的落盘与替换策略（无任何外部依赖，便于独立测试）。
///
/// 背景（2026-10-28）：旧实现"先删旧 jar 再下载"，一旦旧 jar 被进程占用
/// （典型是上一次启动器留下的孤儿 java）就永远失败，且提示"关实例/关启动器重试"
/// 根本不管用。现在把策略收敛到这里：
///   ① 先下载到 .new，不碰正式 jar（下载失败也不会破坏现有安装）；
///   ② 尝试替换；被占用则返回 false，由调用方先清理占用者再重试；
///   ③ 仍不行就另存为 ViaProxy-&lt;时间戳&gt;.jar，下次启动时由 Promote 提升为正式 jar。
/// </summary>
public static class ViaProxyJarInstaller
{
    public const string VersionedPattern = "ViaProxy-*.jar";

    /// <summary>把临时文件替换为正式 jar。成功 true；被占用/失败 false（不抛出）。</summary>
    public static bool TryReplace(string temp, string target)
    {
        try
        {
            if (!File.Exists(temp)) return false;
            if (File.Exists(target))
            {
                try { File.Delete(target); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
            File.Move(temp, target);
            return File.Exists(target);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>把临时文件另存为版本化文件名，返回最终路径；失败抛出。</summary>
    public static string SaveAsVersioned(string temp, string dir, DateTime? stamp = null)
    {
        Directory.CreateDirectory(dir);
        var name = $"ViaProxy-{(stamp ?? DateTime.Now):yyyyMMdd_HHmmss}.jar";
        var dest = Path.Combine(dir, name);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(temp, dest);
        return dest;
    }

    /// <summary>
    /// 把最新的版本化 jar 提升为正式 jar（正式 jar 已存在且更新则跳过）。
    /// 返回被提升的文件路径；没有可提升的返回 null。
    /// </summary>
    public static string? PromoteVersioned(string dir, string target)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            string? newest = null;
            DateTime newestTime = DateTime.MinValue;
            foreach (var p in Directory.GetFiles(dir, VersionedPattern))
            {
                if (p.EndsWith(".new", StringComparison.OrdinalIgnoreCase)) continue;
                var t = File.GetLastWriteTimeUtc(p);
                if (t > newestTime) { newestTime = t; newest = p; }
            }
            if (newest == null) return null;

            if (File.Exists(target))
            {
                if (File.GetLastWriteTimeUtc(target) >= newestTime) return null;
                File.Delete(target);   // 若仍被占用会抛 IOException，被下面 catch
            }
            File.Move(newest, target);
            return target;
        }
        catch { return null; }
    }
}
