using System.Collections.ObjectModel;
using System.IO;
using Bakabot.Helpers;
using Bakabot.Models;

namespace Bakabot.Services;

/// <summary>
/// 实例的 CRUD 和状态管理。
/// 作为全局单例维护实例列表和运行中的进程字典。
/// </summary>
public class InstanceManager
{
    private readonly EnvManager _envManager;
    private readonly AuthInterceptor _authInterceptor;
    private readonly DownloadService _downloadService;
    private readonly SettingsService _settingsService;
    private readonly PatchService _patchService;
    private readonly ViaProxyService _viaProxyService;

    /// <summary>已创建的实例列表（UI 数据源）</summary>
    public ObservableCollection<BotInstance> Instances { get; } = new();

    /// <summary>运行中的进程管理字典 [实例名 -> 进程管理器]</summary>
    public Dictionary<string, NodeProcessManager> RunningProcesses { get; } = new();

    /// <summary>当实例进程启动时触发</summary>
    public event Action<string, NodeProcessManager>? ProcessStarted;

    public InstanceManager(EnvManager envManager, AuthInterceptor authInterceptor,
        DownloadService downloadService, SettingsService settingsService,
        PatchService patchService, ViaProxyService viaProxyService)
    {
        _envManager = envManager;
        _authInterceptor = authInterceptor;
        _downloadService = downloadService;
        _settingsService = settingsService;
        _patchService = patchService;
        _viaProxyService = viaProxyService;
    }

    /// <summary>
    /// 扫描实例目录，加载所有已存在的实例。
    /// </summary>
    public void LoadAllInstances()
    {
        Instances.Clear();

        if (!Directory.Exists(PathHelper.InstancesDir)) return;

        foreach (var dir in Directory.GetDirectories(PathHelper.InstancesDir))
        {
            var instanceName = Path.GetFileName(dir);
            var envPath = PathHelper.GetInstanceEnvPath(instanceName);

            if (File.Exists(envPath))
            {
                // 对旧实例补打最新补丁（幂等，已有标记的补丁不会重复注入）
                try { _patchService.PatchInstance(instanceName); } catch { /* 补丁失败不影响加载 */ }

                var instance = _envManager.ReadEnv(instanceName);
                instance.Status = RunningProcesses.ContainsKey(instanceName)
                    ? BotStatus.Running
                    : BotStatus.Stopped;
                Instances.Add(instance);
            }
            else
            {
                // 目录存在但没有 .env，创建一个默认的
                Instances.Add(new BotInstance
                {
                    InstanceName = instanceName,
                    Status = BotStatus.Stopped
                });
            }
        }
    }

    /// <summary>
    /// 创建新实例：解压基础包 + 写入 .env。
    /// </summary>
    public void CreateInstance(BotInstance instance)
    {
        var instanceDir = PathHelper.GetInstanceDir(instance.InstanceName);
        if (Directory.Exists(instanceDir))
            throw new InvalidOperationException($"实例 '{instance.InstanceName}' 已存在。");

        // 解压基础包
        _downloadService.ExtractBaseAgentToInstance(instance.InstanceName, _settingsService.Settings.UseCustomBaseAgent);

        // 应用自动修补（版本映射 + 资源包支持）
        _patchService.PatchInstance(instance.InstanceName);

        // 写入 .env
        _envManager.WriteEnv(instance);

        instance.Status = BotStatus.Stopped;
        Instances.Add(instance);
    }

    /// <summary>
    /// 更新实例配置，重新写入 .env。
    /// </summary>
    public void UpdateInstance(BotInstance instance)
    {
        _envManager.WriteEnv(instance);

        // 更新列表中的对应项
        var existing = Instances.FirstOrDefault(i => i.InstanceName == instance.InstanceName);
        if (existing != null)
        {
            var index = Instances.IndexOf(existing);
            Instances[index] = instance;
        }
    }

    /// <summary>
    /// 删除实例：停止进程 + 删除目录。
    /// </summary>
    public async Task DeleteInstanceAsync(string instanceName)
    {
        // 如果正在运行，先停止
        if (RunningProcesses.TryGetValue(instanceName, out var pm))
        {
            await pm.StopAsync();
            pm.Dispose();
            RunningProcesses.Remove(instanceName);
        }

        // 删除目录
        var dir = PathHelper.GetInstanceDir(instanceName);
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);

        // 从列表移除
        var instance = Instances.FirstOrDefault(i => i.InstanceName == instanceName);
        if (instance != null)
            Instances.Remove(instance);
    }

    /// <summary>
    /// 一键刷新所有未运行实例的补丁（剥离旧补丁块后重新注入最新版本）。
    /// </summary>
    /// <returns>成功刷新的实例数量</returns>
    public int RepatchAllInstances()
    {
        var count = 0;
        if (!Directory.Exists(PathHelper.InstancesDir)) return 0;

        // 确定当前生效的基础包 zip，用于向旧实例同步新增文件
        var useCustom = _settingsService.Settings.UseCustomBaseAgent;
        var zipPath = useCustom ? PathHelper.CustomBaseAgentZipPath : PathHelper.BaseAgentZipPath;
        if (!File.Exists(zipPath)) zipPath = File.Exists(PathHelper.CustomBaseAgentZipPath)
            ? PathHelper.CustomBaseAgentZipPath
            : PathHelper.BaseAgentZipPath;

        foreach (var dir in Directory.GetDirectories(PathHelper.InstancesDir))
        {
            var instanceName = Path.GetFileName(dir);
            // 运行中的实例不刷新，避免影响进程
            if (RunningProcesses.ContainsKey(instanceName)) continue;

            try
            {
                // 先同步基础包新增/更新的文件，再重新注入补丁
                if (File.Exists(zipPath))
                    _patchService.SyncBaseFiles(instanceName, zipPath);
                _patchService.RepatchInstance(instanceName);
                count++;
            }
            catch { /* 单个实例失败不影响其他 */ }
        }
        return count;
    }

    /// <summary>
    /// 复制实例：完整拷贝实例目录（含 .env 与依赖），新实例名为“原名_copy”（重名时自动追加序号）。
    /// </summary>
    /// <returns>新实例名</returns>
    public string DuplicateInstance(string instanceName)
    {
        var srcDir = PathHelper.GetInstanceDir(instanceName);
        if (!Directory.Exists(srcDir))
            throw new InvalidOperationException($"实例 '{instanceName}' 不存在。");

        if (RunningProcesses.ContainsKey(instanceName))
            throw new InvalidOperationException($"实例 '{instanceName}' 正在运行中，请先停止后再复制。");

        // 生成不重名的新实例名：xxx_copy → xxx_copy2 → xxx_copy3 ...
        var newName = instanceName + "_copy";
        var suffix = 2;
        while (Directory.Exists(PathHelper.GetInstanceDir(newName)))
            newName = instanceName + "_copy" + suffix++;

        CopyDirectoryRecursive(srcDir, PathHelper.GetInstanceDir(newName));

        // 加入 UI 列表
        var instance = _envManager.ReadEnv(newName);
        instance.InstanceName = newName;
        instance.Status = BotStatus.Stopped;
        Instances.Add(instance);

        return newName;
    }

    /// <summary>递归复制目录</summary>
    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
    }

    /// <summary>
    /// 异步启动实例的 Node.js 进程。
    /// 如果配置了 26.x 等需要 ViaProxy 的版本，会先等待 ViaProxy 代理就绪。
    /// </summary>
    public async Task<NodeProcessManager> StartInstanceAsync(string instanceName)
    {
        if (RunningProcesses.ContainsKey(instanceName))
            throw new InvalidOperationException($"实例 '{instanceName}' 已在运行中。");

        var pm = new NodeProcessManager(instanceName, _authInterceptor, _viaProxyService);

        pm.ProcessExited += (_, exitCode) =>
        {
            RunningProcesses.Remove(instanceName);
            var inst = Instances.FirstOrDefault(i => i.InstanceName == instanceName);
            if (inst != null)
                inst.Status = exitCode == 0 ? BotStatus.Stopped : BotStatus.Error;
        };

        await pm.StartAsync();
        RunningProcesses[instanceName] = pm;
        ProcessStarted?.Invoke(instanceName, pm);

        var instance = Instances.FirstOrDefault(i => i.InstanceName == instanceName);
        if (instance != null)
            instance.Status = BotStatus.Running;

        return pm;
    }

    /// <summary>
    /// 停止实例的 Node.js 进程。
    /// </summary>
    public async Task StopInstanceAsync(string instanceName)
    {
        if (!RunningProcesses.TryGetValue(instanceName, out var pm)) return;

        await pm.StopAsync();
        pm.Dispose();
        RunningProcesses.Remove(instanceName);

        var instance = Instances.FirstOrDefault(i => i.InstanceName == instanceName);
        if (instance != null)
            instance.Status = BotStatus.Stopped;
    }
}
