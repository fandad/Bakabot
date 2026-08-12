using System.IO;

namespace Bakabot.Helpers;

/// <summary>
/// ���й��� %APPDATA%/Bakabot/ �µ�����·������֤��ɫ���С�
/// </summary>
public static class PathHelper
{
    /// <summary>��Ŀ¼ %APPDATA%/Bakabot/</summary>
    public static string RootDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bakabot");

    /// <summary>改名前的旧数据目录 %APPDATA%/ARCbot/（仅用于一次性迁移）</summary>
    private static string LegacyRootDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ARCbot");

    /// <summary>Node.js ��Я����ʱĿ¼</summary>
    public static string RuntimeDir => Path.Combine(RootDir, "runtime");

    /// <summary>node.exe ����·��</summary>
    public static string NodeExePath => Path.Combine(RuntimeDir, "node.exe");

    /// <summary>ʵ����Ŀ¼</summary>
    public static string InstancesDir => Path.Combine(RootDir, "Instances");

    /// <summary>���ػ���Ŀ¼</summary>
    public static string DownloadsDir => Path.Combine(RootDir, "downloads");

    /// <summary>������ zip ����·��</summary>
    public static string BaseAgentZipPath => Path.Combine(DownloadsDir, "base_agent.zip");

    /// <summary>自定义基础包 zip 存储路径</summary>
    public static string CustomBaseAgentZipPath => Path.Combine(DownloadsDir, "custom_base_agent.zip");

    /// <summary>自定义背景图存储目录（拖入/选择的背景图持久化在这里，可随时切换）</summary>
    public static string BackgroundsDir => Path.Combine(RootDir, "backgrounds");

    /// <summary>ViaProxy 目录（存放 JAR 及生成的配置文件）</summary>
    public static string ViaProxyDir => Path.Combine(RootDir, "viaproxy");

    /// <summary>ViaProxy JAR 存储路径</summary>
    public static string ViaProxyJarPath => Path.Combine(ViaProxyDir, "ViaProxy.jar");

    /// <summary>Ӧ�������ļ�</summary>
    public static string AppSettingsPath => Path.Combine(RootDir, "settings.json");

    /// <summary>��ȡָ��ʵ���ĸ�Ŀ¼</summary>
    public static string GetInstanceDir(string instanceName) =>
        Path.Combine(InstancesDir, instanceName);

    /// <summary>��ȡָ��ʵ���� src Ŀ¼��Node.js ����Ŀ¼��</summary>
    public static string GetInstanceSrcDir(string instanceName) =>
        Path.Combine(GetInstanceDir(instanceName), "src");

    /// <summary>��ȡָ��ʵ���� .env �ļ�·��</summary>
    public static string GetInstanceEnvPath(string instanceName) =>
        Path.Combine(GetInstanceDir(instanceName), ".env");

    /// <summary>��ȡָ��ʵ���� plugins Ŀ¼</summary>
    public static string GetInstancePluginsDir(string instanceName) =>
        Path.Combine(GetInstanceDir(instanceName), "plugins");

    /// <summary>ȷ�����б�ҪĿ¼����</summary>
    public static void EnsureDirectories()
    {
        MigrateLegacyRoot();
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(RuntimeDir);
        Directory.CreateDirectory(InstancesDir);
        Directory.CreateDirectory(DownloadsDir);
        Directory.CreateDirectory(BackgroundsDir);
        Directory.CreateDirectory(ViaProxyDir);
    }

    /// <summary>
    /// 改名（ARCbot → Bakabot）后的一次性数据迁移：
    /// 新目录尚不存在而旧目录存在时整体移动，保留用户已有的实例/配置/下载内容。
    /// </summary>
    private static void MigrateLegacyRoot()
    {
        try
        {
            if (!Directory.Exists(RootDir) && Directory.Exists(LegacyRootDir))
                Directory.Move(LegacyRootDir, RootDir);
        }
        catch
        {
            // 迁移失败时退回新建目录，不影响启动（旧数据仍在原处）
        }
    }
}