using Bakabot.Helpers;
using Bakabot.Models;
using DnsClient;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Bakabot.Services;

/// <summary>
/// 管理单个 Node.js 进程的完整生命周期：
/// 启动（含 ViaProxy 代理）、流重定向、UTF-8 编码、停止、事件通知。
///
/// 每个运行中的 BotInstance 对应一个 NodeProcessManager 实例。
///
/// ViaProxy 集成逻辑（仿 ViaFabricPlus 客户端侧协议转换思路）：
///   如果配置的版本（如 26.1）不被 mineflayer 原生支持，
///   且 ViaProxy 功能已就绪，则在启动机器人之前先启动一个本地 ViaProxy 进程：
///     Bot (mineflayer/1.21.4) → ViaProxy(:localPort) → 目标服务器(:25565, 26.x协议)
///   ViaProxy 在客户端侧完成协议翻译，无需服务器安装任何插件。
/// </summary>
public class NodeProcessManager : IDisposable
{
    private Process? _process;
    private readonly AuthInterceptor _authInterceptor;
    private readonly ViaProxyService? _viaProxyService;

    /// <summary>控制台输出落盘文件（每次启动覆盖，便于排查问题）</summary>
    private StreamWriter? _logWriter;
    private readonly object _logLock = new();

    public string InstanceName { get; }
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>收到 stdout 一行输出时触发</summary>
    public event EventHandler<ConsoleEntry>? OutputReceived;

    /// <summary>进程退出时触发</summary>
    public event EventHandler<int>? ProcessExited;

    public NodeProcessManager(string instanceName, AuthInterceptor authInterceptor,
        ViaProxyService? viaProxyService = null)
    {
        InstanceName = instanceName;
        _authInterceptor = authInterceptor;
        _viaProxyService = viaProxyService;
    }

    /// <summary>
    /// 异步启动 Node.js 进程（如需 ViaProxy，先等待代理就绪）。
    /// </summary>
    public async Task StartAsync()
    {
        if (IsRunning)
            throw new InvalidOperationException($"实例 '{InstanceName}' 已在运行中。");

        var srcDir = PathHelper.GetInstanceSrcDir(InstanceName);
        var indexJs = Path.Combine(srcDir, "index.js");

        if (!File.Exists(PathHelper.NodeExePath))
            throw new FileNotFoundException("Node.js 运行时未找到，请先在设置中下载。");

        if (!File.Exists(indexJs))
            throw new FileNotFoundException($"入口文件不存在: {indexJs}");

        var psi = new ProcessStartInfo
        {
            FileName = PathHelper.NodeExePath,
            Arguments = "index.js",
            WorkingDirectory = srcDir,
            UseShellExecute = false,
            CreateNoWindow = true,

            // ─── 流重定向 ───
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,

            // ─── 强制 UTF-8 编码，彻底杜绝中文乱码 ───
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 设置环境变量确保 Node.js 输出 UTF-8
        psi.Environment["NODE_OPTIONS"] = "--max-old-space-size=512";
        psi.Environment["LANG"] = "zh_CN.UTF-8";
        psi.Environment["CHCP"] = "65001";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        // 将 .env 所在目录加入环境（dotenv 会自动读取）
        var envFilePath = PathHelper.GetInstanceEnvPath(InstanceName);
        if (File.Exists(envFilePath))
        {
            // 部分项目需要 .env 在工作目录，复制一份到 src
            var srcEnvPath = Path.Combine(srcDir, ".env");
            File.Copy(envFilePath, srcEnvPath, overwrite: true);
        }

        // ─── 读取配置，决定是否需要 ViaProxy ───
        if (File.Exists(envFilePath))
        {
            var envVars = ParseEnvFile(envFilePath);
            envVars.TryGetValue("MC_VERSION", out var configVersion);
            var configHost = envVars.GetValueOrDefault("MC_HOST") ?? "";
            envVars.TryGetValue("MC_PORT", out var configPortStr);
            var authType = envVars.GetValueOrDefault("MC_AUTH_TYPE", "microsoft") ?? "microsoft";
            var autoAcceptPack = !string.Equals(
                envVars.GetValueOrDefault("AUTO_ACCEPT_RESOURCE_PACK", "true"), "false", StringComparison.OrdinalIgnoreCase);

            // ─── SRV 解析：域名服的常见配置方式，真实地址藏在 _minecraft._tcp 记录里 ───
            // mineflayer / ViaProxy 都不会自行解析 SRV，不处理的话会直连域名默认端口导致拒绝连接
            if (!string.IsNullOrEmpty(configHost)
                && int.TryParse(configPortStr, out var curPort)
                && curPort == 25565
                && !System.Net.IPAddress.TryParse(configHost, out _))
            {
                var srv = TryResolveMinecraftSrv(configHost);
                if (srv != null)
                {
                    EmitInfo($"[SRV] 域名解析到真实服务器地址: {configHost} → {srv.Value.Host}:{srv.Value.Port}");
                    configHost = srv.Value.Host;
                    configPortStr = srv.Value.Port.ToString();
                    // 同步给机器人进程（直连/兼容模式下机器人需要连真实地址）
                    psi.Environment["MC_HOST"] = configHost;
                    psi.Environment["MC_PORT"] = configPortStr;
                }
            }

            var needsProxy = !string.IsNullOrEmpty(configVersion)
                             && VersionMapper.RequiresViaProxy(configVersion)
                             && _viaProxyService?.IsReady == true;

            // 第三方登录（Yggdrasil）无法经 ViaProxy 认证：代理后端只能用 NONE/正版账号登录，
            // 不支持自定义认证服务器，此时回退到直连方案（机器人自带 Yggdrasil 会话凭证）
            if (needsProxy && authType == "yggdrasil")
            {
                EmitInfo("[ViaProxy] 第三方登录（Yggdrasil）无法经 ViaProxy 认证，已切换为直连模式（机器人携带 Yggdrasil 凭证登录）。");
                needsProxy = false;
            }

            if (needsProxy)
            {
                // ─── ViaProxy 模式：客户端侧协议转换，无需服务器安装 ViaVersion ───
                var targetHost = string.IsNullOrEmpty(configHost) ? "localhost" : configHost;
                int.TryParse(configPortStr, out var targetPort);
                if (targetPort <= 0) targetPort = 25565;

                // 正版验证服务器：由 ViaProxy 使用已配置的微软账号登录后端
                var useAccountAuth = authType is "microsoft" or "mojang";
                if (useAccountAuth && !_viaProxyService!.HasMinecraftAccount)
                {
                    throw new InvalidOperationException(
                        "目标为正版验证服务器，经 ViaProxy 连接需要先在 ViaProxy 中配置微软正版账号。\n" +
                        "请前往「设置 → 协议代理」，点击「配置正版账号」在打开的窗口中添加账号后重试。");
                }

                EmitInfo($"[ViaProxy] 检测到版本 {configVersion}，将通过 ViaProxy 连接（无需服务器 ViaVersion 插件）");
                if (useAccountAuth)
                    EmitInfo("[ViaProxy] 正版验证服务器：将由 ViaProxy 使用已配置的正版账号登录");
                if (autoAcceptPack)
                    EmitInfo("[ViaProxy] 已启用自动接受资源包（强制资源包服务器可正常进入）");

                try
                {
                    var proxyPort = await _viaProxyService!.StartProxyAsync(
                        InstanceName,
                        targetHost,
                        targetPort,
                        configVersion!,
                        fakeAcceptResourcePacks: autoAcceptPack,
                        useAccountAuth: useAccountAuth,
                        logAction: text => EmitInfo(text));

                    // 将机器人的连接目标重定向到本地 ViaProxy 代理
                    psi.Environment["MC_HOST"] = "127.0.0.1";
                    psi.Environment["MC_PORT"] = proxyPort.ToString();
                    // 机器人使用 mineflayer 原生支持的最新版本，由 ViaProxy 负责对上 26.x 协议
                    psi.Environment["MC_VERSION"] = VersionMapper.ClientVersionForViaProxy;

                    if (useAccountAuth)
                    {
                        // 机器人以离线方式连入本地代理即可，真正的正版验证由 ViaProxy 账号完成
                        psi.Environment["MC_AUTH_TYPE"] = "offline";
                        EmitInfo($"[ViaProxy] 机器人将以 {VersionMapper.ClientVersionForViaProxy} 协议、离线身份连接本地代理 127.0.0.1:{proxyPort}");
                    }
                    else
                    {
                        EmitInfo($"[ViaProxy] 机器人将以 {VersionMapper.ClientVersionForViaProxy} 协议连接本地代理 127.0.0.1:{proxyPort}");
                    }
                }
                catch (Exception ex)
                {
                    // 账号未配置等硬性错误直接抛出，避免静默回退后报出更难懂的错误
                    if (ex is InvalidOperationException && useAccountAuth) throw;

                    EmitInfo($"[ViaProxy] 代理启动失败: {ex.Message}");
                    EmitInfo($"[兼容模式] 回退到版本映射（需要服务器安装 ViaVersion）...");
                    // 回退：沿用原有的版本映射兜底方案
                    var fallback = VersionMapper.ToMineflayerVersion(configVersion!);
                    psi.Environment["MC_VERSION"] = fallback;
                    EmitInfo($"[兼容模式] 版本 {configVersion} → {fallback}");
                }
            }
            else
            {
                // ─── 标准模式或 ViaProxy 不可用时的兼容模式 ───
                if (!string.IsNullOrEmpty(configVersion) && VersionMapper.RequiresViaProxy(configVersion))
                {
                    // ViaProxy 未就绪，回退到版本映射（需要服务器有 ViaVersion）
                    var mappedVer = VersionMapper.ToMineflayerVersion(configVersion);
                    psi.Environment["MC_VERSION"] = mappedVer;
                    EmitInfo($"[兼容模式] 版本 {configVersion} → {mappedVer}（ViaProxy 不可用，需要服务器 ViaVersion 插件）");
                }
                else if (!string.IsNullOrEmpty(configVersion))
                {
                    // mineflayer 原生支持的版本：版本映射仍确保安全
                    var mappedVer = VersionMapper.ToMineflayerVersion(configVersion);
                    if (mappedVer != configVersion)
                    {
                        psi.Environment["MC_VERSION"] = mappedVer;
                        EmitInfo($"[版本映射] {configVersion} → {mappedVer}");
                    }
                }

                // Yggdrasil 第三方登录：将 auth 类型改成 mojang
                if (envVars.TryGetValue("MC_AUTH_TYPE", out var at) && at == "yggdrasil")
                {
                    psi.Environment["MC_AUTH_TYPE"] = "mojang";
                }
            }

            // ─── Yggdrasil 第三方认证（在事件订阅后、进程启动前执行）───
            if (authType == "yggdrasil")
            {
                EmitInfo("正在通过 Yggdrasil 第三方认证...");
                try
                {
                    var authServer = envVars.GetValueOrDefault("AUTH_SERVER_URL", "https://littleskin.cn/api/yggdrasil").TrimEnd('/');
                    var username = envVars.GetValueOrDefault("MC_USERNAME", "");
                    var password = envVars.GetValueOrDefault("MC_LOGIN_PASSWORD", "");

                    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                        throw new Exception("请填写邮箱和密码");

                    using var http = new HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(15);
                    var authUrl = authServer + "/authserver/authenticate";
                    var reqBody = new
                    {
                        agent = new { name = "Minecraft", version = 1 },
                        username = username,
                        password = password,
                        requestUser = true
                    };
                    var resp = await http.PostAsync(authUrl, new StringContent(
                        JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json"));
                    var respJson = await resp.Content.ReadAsStringAsync();

                    using var doc = JsonDocument.Parse(respJson);
                    if (doc.RootElement.TryGetProperty("error", out _))
                    {
                        var errMsg = doc.RootElement.TryGetProperty("errorMessage", out var em) ? em.GetString() : "认证失败";
                        throw new Exception(errMsg ?? "认证失败");
                    }
                    if (!doc.RootElement.TryGetProperty("selectedProfile", out var profile))
                        throw new Exception("该账号没有角色，请先在皮肤站创建角色");

                    var accessToken = doc.RootElement.GetProperty("accessToken").GetString()!;
                    var playerName = profile.GetProperty("name").GetString()!;
                    var playerUuid = profile.GetProperty("id").GetString()!;

                    psi.Environment["YGGDRASIL_ACCESS_TOKEN"] = accessToken;
                    psi.Environment["YGGDRASIL_PLAYER_NAME"] = playerName;
                    psi.Environment["YGGDRASIL_UUID"] = playerUuid;
                    EmitInfo($"Yggdrasil 认证成功，玩家: {playerName}");
                }
                catch (Exception ex)
                {
                    EmitInfo($"Yggdrasil 认证失败: {ex.Message}");
                    throw;
                }
            }
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // 控制台输出落盘：实例目录下 console.log，每次启动覆盖
        try
        {
            var logPath = Path.Combine(PathHelper.GetInstanceDir(InstanceName), "console.log");
            _logWriter = new StreamWriter(logPath, false, Encoding.UTF8) { AutoFlush = true };
            _logWriter.WriteLine($"===== 实例 '{InstanceName}' 启动于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        }
        catch { _logWriter = null; }

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;

            var entry = new ConsoleEntry
            {
                Level = ClassifyLogLevel(e.Data),
                Text = e.Data,
                InstanceName = InstanceName
            };

            WriteLogLine(e.Data);
            OutputReceived?.Invoke(this, entry);

            // ─── 全局微软登录拦截 ───
            _authInterceptor.AnalyzeLine(InstanceName, e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;

            var entry = new ConsoleEntry
            {
                Level = LogLevel.Error,
                Text = e.Data,
                InstanceName = InstanceName
            };

            WriteLogLine("[stderr] " + e.Data);
            OutputReceived?.Invoke(this, entry);

            // stderr 也可能包含登录信息
            _authInterceptor.AnalyzeLine(InstanceName, e.Data);
        };

        _process.Exited += (_, _) =>
        {
            var exitCode = _process?.ExitCode ?? -1;
            WriteLogLine($"===== 进程退出，退出码: {exitCode} ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) =====");
            lock (_logLock)
            {
                try { _logWriter?.Dispose(); } catch { /* ignore */ }
                _logWriter = null;
            }
            // 机器人进程退出时，同步停止对应的 ViaProxy 代理进程（fire-and-forget）
            if (_viaProxyService != null)
                _ = _viaProxyService.StopProxyAsync(InstanceName);
            ProcessExited?.Invoke(this, exitCode);
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        EmitInfo($"实例 '{InstanceName}' 已启动 (PID: {_process.Id})");
    }

    /// <summary>
    /// 向进程的 stdin 写入一行文本。
    /// </summary>
    public async Task WriteInputAsync(string text)
    {
        if (_process?.StandardInput == null || !IsRunning) return;

        await _process.StandardInput.WriteLineAsync(text);
        await _process.StandardInput.FlushAsync();

        OutputReceived?.Invoke(this, new ConsoleEntry
        {
            Level = LogLevel.Stdin,
            Text = $"> {text}",
            InstanceName = InstanceName
        });
    }

    /// <summary>
    /// 优雅停止进程：先尝试关闭 stdin，等待退出；超时则强制 Kill。
    /// 同时停止对应的 ViaProxy 代理进程。
    /// </summary>
    public async Task StopAsync(int timeoutMs = 5000)
    {
        if (_process == null || !IsRunning)
        {
            EmitInfo($"实例 '{InstanceName}' 未在运行。");
            return;
        }

        try
        {
            EmitInfo($"正在停止实例 '{InstanceName}'...");

            // 尝试优雅关闭
            _process.StandardInput?.Close();

            var exited = await Task.Run(() => _process.WaitForExit(timeoutMs));
            if (!exited)
            {
                _process.Kill(entireProcessTree: true);
                EmitInfo($"实例 '{InstanceName}' 已被强制终止。");
            }
            else
            {
                EmitInfo($"实例 '{InstanceName}' 已正常退出 (Code: {_process.ExitCode})。");
            }
        }
        catch (Exception ex)
        {
            EmitInfo($"停止实例时出错: {ex.Message}");
        }
        finally
        {
            // 确保 ViaProxy 代理也一并停止
            if (_viaProxyService != null)
                await _viaProxyService.StopProxyAsync(InstanceName);
        }
    }

    /// <summary>根据日志内容简单分类日志级别</summary>
    private static LogLevel ClassifyLogLevel(string text)
    {
        if (text.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WARNING", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warn;

        if (text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ERR!", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;

        return LogLevel.Info;
    }

    /// <summary>
    /// 查询 Minecraft SRV 记录（_minecraft._tcp.域名），返回真实服务器地址与端口。
    /// 无记录或查询失败时返回 null（不影响原有流程）。
    /// </summary>
    private static (string Host, int Port)? TryResolveMinecraftSrv(string host)
    {
        try
        {
            var client = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(4) });
            var response = client.Query($"_minecraft._tcp.{host}", QueryType.SRV);
            if (response.HasError) return null;

            DnsClient.Protocol.SrvRecord? best = null;
            foreach (var record in response.Answers.OfType<DnsClient.Protocol.SrvRecord>())
            {
                if (best == null || record.Priority < best.Priority)
                    best = record;
            }
            if (best == null) return null;

            var target = best.Target?.ToString().TrimEnd('.');
            if (string.IsNullOrEmpty(target)) return null;
            var port = best.Port;
            if (port <= 0 || port > 65535) return null;
            return (target, port);
        }
        catch
        {
            // DNS 查询异常（超时/无 SRV/库不可用等）：静默回退到原地址
            return null;
        }
    }

    /// <summary>简单解析 .env 文件为字典</summary>
    private static Dictionary<string, string> ParseEnvFile(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            var key = trimmed[..idx].Trim();
            var val = trimmed[(idx + 1)..].Trim().Trim('"', '\'');
            dict[key] = val;
        }
        return dict;
    }

    private void EmitInfo(string text)
    {
        WriteLogLine("[启动器] " + text);
        OutputReceived?.Invoke(this, new ConsoleEntry
        {
            Level = LogLevel.Info,
            Text = text,
            InstanceName = InstanceName
        });
    }

    /// <summary>控制台行写入落盘日志（失败不影响主流程）</summary>
    private void WriteLogLine(string line)
    {
        lock (_logLock)
        {
            try { _logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}"); }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        if (_process != null)
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            _process.Dispose();
            _process = null;
        }
        lock (_logLock)
        {
            try { _logWriter?.Dispose(); } catch { /* ignore */ }
            _logWriter = null;
        }
        GC.SuppressFinalize(this);
    }
}
