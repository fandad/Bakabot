using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bakabot.Helpers;

namespace Bakabot.Services;

/// <summary>
/// ViaProxy 进程管理服务。
///
/// ViaProxy 是由 ViaVersion 团队开发的独立协议代理，可将任意版本的
/// Minecraft 客户端接入任意版本的服务器（包括 26.x 年份命名版本）。
///
/// 工作原理（类比 ViaFabricPlus 的客户端侧协议转换，但通过代理实现）：
///   机器人 (mineflayer/1.21.4) ──→ ViaProxy(:本地端口) ──→ 目标服务器(:25565, 26.x协议)
///
/// 参考项目：https://github.com/ViaVersion/ViaProxy
/// 要求：Java 17+（优先使用 Minecraft 内置 JRE）
/// </summary>
public class ViaProxyService : IDisposable
{
    private readonly SettingsService _settingsService;

    /// <summary>正在运行的代理字典 [实例名 → (进程, 本地端口)]</summary>
    private readonly Dictionary<string, (Process Process, int Port)> _running = new();

    public ViaProxyService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    // ─── 状态属性 ───

    /// <summary>ViaProxy JAR 是否已下载</summary>
    public bool IsViaProxyAvailable => File.Exists(PathHelper.ViaProxyJarPath);

    /// <summary>当前可用的 java.exe 路径（null = 未找到）</summary>
    public string? JavaPath => JavaHelper.FindJava(_settingsService.Settings.JavaPath);

    /// <summary>功能是否完全就绪（Java 17+ 且 ViaProxy JAR 均已就位）</summary>
    public bool IsReady =>
        _settingsService.Settings.EnableViaProxy &&
        IsViaProxyAvailable &&
        JavaPath != null;

    /// <summary>ViaProxy 存档文件路径（账号等数据由 ViaProxy GUI 写入此处）</summary>
    private static string SavesJsonPath => Path.Combine(PathHelper.ViaProxyDir, "saves.json");

    /// <summary>获取已在 ViaProxy 中配置的正版账号数量（由 saves.json 的 accountsV4 字段记录）</summary>
    public int GetAccountCount()
    {
        try
        {
            if (!File.Exists(SavesJsonPath)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(SavesJsonPath));
            if (doc.RootElement.TryGetProperty("accountsV4", out var accounts) &&
                accounts.ValueKind == JsonValueKind.Array)
                return accounts.GetArrayLength();
        }
        catch { /* 文件损坏时视为未配置 */ }
        return 0;
    }

    /// <summary>是否已配置至少一个正版账号（连接正版验证服务器必需）</summary>
    public bool HasMinecraftAccount => GetAccountCount() > 0;

    /// <summary>
    /// 打开 ViaProxy 自带的图形界面，供用户在 Accounts 页签添加/管理微软正版账号。
    /// GUI 关闭后账号会保存到 saves.json，之后 CLI 模式可用 --auth-method ACCOUNT 登录服务器。
    /// </summary>
    public void OpenAccountManagerUi()
    {
        var javaPath = JavaPath
            ?? throw new InvalidOperationException("未找到 Java 运行时，无法打开 ViaProxy 账号管理界面。");
        if (!IsViaProxyAvailable)
            throw new FileNotFoundException("ViaProxy.jar 未下载，请先在设置中下载。");

        // 不带 cli 参数启动 = 打开 ViaProxy 图形界面
        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            Arguments = $"-jar \"{PathHelper.ViaProxyJarPath}\"",
            WorkingDirectory = PathHelper.ViaProxyDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
    }

    // ─── 核心方法 ───

    /// <summary>
    /// 为指定实例启动一个本地 ViaProxy 代理进程。
    ///
    /// 成功后，机器人应将 MC_HOST/MC_PORT 改为 127.0.0.1:返回端口，
    /// MC_VERSION 改为 mineflayer 原生支持的最新版本（如 1.21.4）。
    /// ViaProxy 负责将协议从 1.21.4 翻译成目标服务器所需的 26.x 协议。
    /// </summary>
    /// <param name="instanceName">实例名（用于追踪和清理进程）</param>
    /// <param name="targetHost">目标服务器地址</param>
    /// <param name="targetPort">目标服务器端口</param>
    /// <param name="targetVersion">目标服务器 Minecraft 版本（如 "26.1"）</param>
    /// <param name="fakeAcceptResourcePacks">由代理自动接受服务器资源包（强制资源包服务器必需）</param>
    /// <param name="useAccountAuth">使用已配置的正版账号登录目标服务器（正版验证服务器必需）</param>
    /// <param name="logAction">日志回调，用于把代理输出转发到实例控制台</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>机器人应连接的本地代理端口</returns>
    public async Task<int> StartProxyAsync(
        string instanceName,
        string targetHost,
        int targetPort,
        string targetVersion,
        bool fakeAcceptResourcePacks = false,
        bool useAccountAuth = false,
        Action<string>? logAction = null,
        CancellationToken ct = default)
    {
        // 如果已有存活的代理，直接复用本地端口
        if (_running.TryGetValue(instanceName, out var existing) && !existing.Process.HasExited)
        {
            logAction?.Invoke($"[ViaProxy] 复用已有代理，本地端口: {existing.Port}");
            return existing.Port;
        }

        var javaPath = JavaPath
            ?? throw new InvalidOperationException(
                "未找到 Java 运行时，无法启动 ViaProxy。\n" +
                "请安装 Java 17+ 或在设置中指定 java.exe 路径。\n" +
                "Minecraft Java 版玩家通常已自带 Java（.minecraft/runtime 目录）。");

        if (!IsViaProxyAvailable)
            throw new FileNotFoundException(
                "ViaProxy.jar 未下载，请前往「设置 → 协议代理」中下载。");

        var localPort = FindFreePort();
        var viaVersion = VersionMapper.ToViaProxyVersionName(targetVersion);

        // ViaProxy CLI 调用格式（v3.4.x，参数为长选项形式，地址与端口合并为 host:port）：
        //   java -jar ViaProxy.jar cli --bind-address 127.0.0.1:PORT --target-address HOST:PORT --target-version VERSION --auth-method NONE
        // 参数说明：
        //   --bind-address: 代理监听地址（机器人连接此处）
        //   --target-address: 目标服务器地址
        //   --target-version: 目标服务器版本（26.x 等，可用 --list-versions 查看）
        //   --auth-method NONE: 不由代理验证账号（适合离线模式服务器）
        //   --auth-method ACCOUNT: 使用 ViaProxy GUI 中配置的正版账号登录（正版验证服务器）
        //   --proxy-online-mode false: 不强制客户端正版验证（默认即 false）
        //   --fake-accept-resource-packs true: 由代理自动接受服务器强制资源包
        var args = $"-jar \"{PathHelper.ViaProxyJarPath}\" cli" +
                   $" --bind-address 127.0.0.1:{localPort}" +
                   $" --target-address {targetHost}:{targetPort}" +
                   $" --target-version \"{viaVersion}\"" +
                   $" --auth-method {(useAccountAuth ? "ACCOUNT" : "NONE")}" +
                   $" --proxy-online-mode false";

        if (useAccountAuth)
            args += " --minecraft-account-index 0";
        if (fakeAcceptResourcePacks)
            args += " --fake-accept-resource-packs true";

        logAction?.Invoke(
            $"[ViaProxy] 正在启动... 目标: {targetHost}:{targetPort} ({viaVersion}) → 本地: 127.0.0.1:{localPort}");

        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            Arguments = args,
            // ViaProxy 在工作目录生成配置文件，使用专用目录避免污染
            WorkingDirectory = PathHelper.ViaProxyDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            logAction?.Invoke($"[ViaProxy] {e.Data}");
            TrySetReady(e.Data, readyTcs);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            logAction?.Invoke($"[ViaProxy] {e.Data}");
            TrySetReady(e.Data, readyTcs);
        };

        process.Exited += (_, _) =>
        {
            _running.Remove(instanceName);
            readyTcs.TrySetResult(false);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _running[instanceName] = (process, localPort);

        // 等待就绪信号，最多 20 秒
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(20_000);

        try
        {
            await readyTcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时但进程还活着：再给 1.5 秒等端口就绪
            if (!process.HasExited)
            {
                logAction?.Invoke("[ViaProxy] 等待就绪信号超时，继续尝试...");
                await Task.Delay(1500, ct);
            }
        }

        if (process.HasExited)
        {
            _running.Remove(instanceName);
            throw new Exception(
                $"ViaProxy 启动失败（退出码: {process.ExitCode}）。\n" +
                "请检查日志输出，确认 Java 版本 ≥ 17 且 ViaProxy.jar 文件完整。");
        }

        logAction?.Invoke($"[ViaProxy] 代理已就绪 → 机器人请连接 127.0.0.1:{localPort}");
        return localPort;
    }

    /// <summary>停止指定实例的代理进程</summary>
    public async Task StopProxyAsync(string instanceName)
    {
        if (!_running.TryGetValue(instanceName, out var entry)) return;

        try
        {
            if (!entry.Process.HasExited)
            {
                entry.Process.Kill(entireProcessTree: true);
                await Task.Run(() => entry.Process.WaitForExit(3000));
            }
        }
        catch { /* 忽略清理时的错误 */ }
        finally
        {
            entry.Process.Dispose();
            _running.Remove(instanceName);
        }
    }

    /// <summary>指定实例的代理是否仍在运行</summary>
    public bool IsProxyRunning(string instanceName)
        => _running.TryGetValue(instanceName, out var e) && !e.Process.HasExited;

    // ─── 内部辅助方法 ───

    /// <summary>判断 ViaProxy 输出行是否代表"已就绪"</summary>
    private static void TrySetReady(string line, TaskCompletionSource<bool> tcs)
    {
        if (tcs.Task.IsCompleted) return;
        // ViaProxy 就绪时会依次输出：
        //   "ViaProxy started successfully!" → "Starting proxy server" → "Binding proxy server to 127.0.0.1:PORT"
        if (line.Contains("Binding proxy server", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Listening on", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ViaProxy started", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Proxy started", StringComparison.OrdinalIgnoreCase))
        {
            tcs.TrySetResult(true);
        }
    }

    /// <summary>从操作系统动态获取一个当前未占用的随机端口</summary>
    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        foreach (var (_, entry) in _running)
        {
            try
            {
                if (!entry.Process.HasExited)
                    entry.Process.Kill(entireProcessTree: true);
                entry.Process.Dispose();
            }
            catch { /* ignore */ }
        }
        _running.Clear();
        GC.SuppressFinalize(this);
    }
}
