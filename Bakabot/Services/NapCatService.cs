using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bakabot.Helpers;

namespace Bakabot.Services;

/// <summary>
/// NapCat QQ 协议端进程管理与 OneBot11 反向 WebSocket 桥接服务。
///
/// 工作方式（与 ViaProxyService 相同的“启动器托管进程”模式）：
///   1. 启动器先在本机开一个 WebSocket 服务端（反向 WS，端口随机）；
///   2. 把 OneBot11 配置写入 NapCat 的 config 目录，让 NapCat 以
///      WebSocket 客户端身份连上启动器（ws://127.0.0.1:随机端口）；
///   3. 用 NapCat.Shell.Windows.Node 包内自带的 node.exe index.js 启动
///      （该包免装 QQ，扫码登录走内置 WebUI，默认 6099 端口）。
///
/// 这样启动器不依赖 NapCat 默认端口，也不需要额外安装任何依赖。
/// </summary>
public class NapCatService : IDisposable
{
    private const int WebUiPort = 6099;
    private const string WebUiToken = "bakabot";

    private readonly SettingsService _settingsService;

    // 看门狗与"用户是否主动停止"状态（2026-10-28 新增）
    private volatile bool _userStopped;
    private volatile bool _loginInvalidSignal;
    private int _restartCount;
    private DateTime _restartWindowStart = DateTime.MinValue;
    private readonly QQService _qqService;

    private readonly object _sync = new();
    private readonly List<WsClient> _clients = new();

    private Process? _process;
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private long _echoSeq;
    private string _webUiUrl = $"http://127.0.0.1:{WebUiPort}/webui?token={WebUiToken}";
    private string _statusText = "NapCat 未启动";
    private bool _qrWaiting;
    private string _qrCodePath = "";
    private long _lastQrHintTick;

    /// <summary>状态变化事件（进程启停、连接、WebUI 地址等），供 UI 刷新</summary>
    public event EventHandler<NapCatState>? StateChanged;

    public NapCatService(SettingsService settingsService, QQService qqService)
    {
        _settingsService = settingsService;
        _qqService = qqService;
    }

    /// <summary>NapCat 自助包是否已下载就绪（node.exe + index.js 存在）</summary>
    public bool IsAvailable =>
        File.Exists(PathHelper.NapCatNodeExePath) && File.Exists(PathHelper.NapCatIndexJsPath);

    /// <summary>NapCat 进程是否正在运行</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _process is { HasExited: false };
        }
    }

    /// <summary>当前可打开的 WebUI 登录地址</summary>
    public string WebUiUrl
    {
        get
        {
            lock (_sync) return _webUiUrl;
        }
    }

    /// <summary>当前状态文本</summary>
    public string StatusText
    {
        get
        {
            lock (_sync) return _statusText;
        }
    }

    /// <summary>应用启动时调用：订阅 QQ 输出，准备接收 NapCat 上报</summary>
    public void Initialize()
    {
        _qqService.QQOutReceived += (_, msg) =>
        {
            _ = SendQQOutAsync(msg);
        };
    }

    /// <summary>
    /// 启动 NapCat：先写好反向 WS 配置，再起 WS 服务端，最后拉起进程。
    /// 重复调用时直接复用已有进程。
    /// </summary>
    // ─── 落盘日志（2026-10-28 新增）────────────────────────────────────────
    // 背景：NapCat 自身 fileLog=false（不写文件），而启动器调用 StartAsync() 时
    // 没有传 logAction —— 于是 NapCat 的 stdout/stderr 和**退出码全部被丢弃**，
    // "用久了突然退掉"这类问题事后完全无法归因。
    // 现在无论调用方是否传 logAction，都统一写到 logs/napcat.log（5MB × 3 轮转）。
    private static readonly object _fileLogLock = new();
    private static string LogDir => Path.Combine(PathHelper.RootDir, "logs");
    private static string NapCatLogPath => Path.Combine(LogDir, "napcat.log");

    private static void FileLog(string line)
    {
        try
        {
            lock (_fileLogLock)
            {
                Directory.CreateDirectory(LogDir);
                var path = NapCatLogPath;
                if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                {
                    var b1 = Path.Combine(LogDir, "napcat.1.log");
                    var b2 = Path.Combine(LogDir, "napcat.2.log");
                    var b3 = Path.Combine(LogDir, "napcat.3.log");
                    try { if (File.Exists(b3)) File.Delete(b3); } catch { }
                    try { if (File.Exists(b2)) File.Move(b2, b3); } catch { }
                    try { if (File.Exists(b1)) File.Move(b1, b2); } catch { }
                    try { File.Move(path, b1); } catch { }
                }
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n", Encoding.UTF8);
            }
        }
        catch { /* 日志失败绝不影响主流程 */ }
    }

    /// <summary>记录当前 QQ 主进程的 PID/启动时间，用于判断"NapCat 退出是否伴随 QQ 重启"</summary>
    private static (int Pid, DateTime? Start) SnapshotQq()
    {
        try
        {
            Process? best = null;
            foreach (var p in Process.GetProcessesByName("QQ"))
            {
                try
                {
                    if (best == null || p.StartTime < best.StartTime) best = p;
                }
                catch { if (best == null) best = p; }
            }
            if (best == null) return (0, null);
            DateTime? st = null;
            try { st = best.StartTime; } catch { }
            return (best.Id, st);
        }
        catch { return (0, null); }
    }

    /// <summary>
    /// 判断是否为"例行消息流量"（收/发群聊私聊）。这类日志量极大（实测 20 秒 35KB），
    /// 落盘会把 5MB 轮转很快写满，掩盖真正有用的告警，因此不写入文件（仍转发给 logAction）。
    /// </summary>
    private static bool IsRoutineTraffic(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        return line.Contains("接收 <-", StringComparison.Ordinal)
            || line.Contains("发送 ->", StringComparison.Ordinal);
    }

    /// <summary>判断一行输出是否代表"登录失效/被踢下线"这类重启也没用的信号</summary>
    private static bool LooksLikeLoginFailure(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        foreach (var k in new[] { "登录失效", "被踢", "下线", "重新登录", "扫码登录", "账号异常", "登录失败" })
        {
            if (line.Contains(k, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// 看门狗（默认关闭，需在 QQ 页手动开启）。
    /// 只在"用户期望它运行、且不是用户点停止、且不是登录失效"时自动重启；
    /// 退避 1/3/5/10/30 秒，最多 5 次 / 10 分钟，超限则停手并提示看日志。
    /// </summary>
    private void MaybeScheduleWatchdogRestart(int exitCode, bool qqGone, Action<string>? logAction)
    {
        if (_userStopped) { FileLog("看门狗：用户手动停止，不重启"); return; }
        if (!_settingsService.Settings.QQWatchdogEnabled) return;   // 默认关
        if (_loginInvalidSignal)
        {
            FileLog("看门狗：检测到登录失效类信号，不自动重启（重启无效，请重新登录）");
            UpdateState(false, "NapCat 已退出（登录失效，已停止自动重启，请重新登录）");
            return;
        }

        var now = DateTime.Now;
        if (_restartWindowStart == DateTime.MinValue || (now - _restartWindowStart).TotalMinutes >= 10)
        {
            _restartWindowStart = now;
            _restartCount = 0;
        }
        if (_restartCount >= 5)
        {
            FileLog("看门狗：10 分钟内已重启 5 次，停止自动重启");
            UpdateState(false, "NapCat 反复退出，已停止自动重启，请查看 logs/napcat.log");
            return;
        }

        _restartCount++;
        var delays = new[] { 1, 3, 5, 10, 30 };
        var delay = delays[Math.Min(_restartCount - 1, delays.Length - 1)];
        FileLog($"看门狗：{delay} 秒后第 {_restartCount} 次自动重启" + (qqGone ? "（独立 QQ 进程已退出）" : ""));
        UpdateState(false, $"NapCat 意外退出，{delay} 秒后自动重启（第 {_restartCount} 次）");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (_userStopped) return;
                await StartAsync(logAction);
                FileLog("看门狗：自动重启完成");
            }
            catch (Exception ex)
            {
                FileLog("看门狗：自动重启失败 " + ex.Message);
                UpdateState(false, "NapCat 自动重启失败：" + ex.Message);
            }
        });
    }

    public async Task StartAsync(Action<string>? logAction = null)
    {
        _userStopped = false;          // 进入"用户期望运行"状态
        _loginInvalidSignal = false;
        if (IsRunning)
        {
            UpdateState(running: true, "NapCat 已在运行");
            return;
        }

        if (!IsAvailable)
            throw new InvalidOperationException(
                "NapCat 尚未下载，请先在「设置」或「QQ 功能 (NapCat)」页下载后再启动。");

        // 互斥：NapCat 启动时顶掉外部 OneBot11 反向接入（llbot / Lagrange 等）
        var oneBot = ProtocolServerCoordinator.OneBotServer;
        if (oneBot != null && oneBot.IsRunning)
            await oneBot.StopAsync();

        var nodeExe = PathHelper.NapCatNodeExePath;
        if (!File.Exists(nodeExe) && File.Exists(PathHelper.NodeExePath))
            nodeExe = PathHelper.NodeExePath;

        var wsPort = FindFreePort();
        var botQQ = _settingsService.Settings.QQBotNumber?.Trim() ?? string.Empty;

        // ── 0. 修复 NapCat v4.18.x 的已知问题 ──
        //   a) 纯 Node 模式下默认会用 --no-sandbox 拉起 worker（node.exe 不认此参数），
        //      必须切换单进程模式；
        //   b) Windows.Node 包漏打了 wrapper.node 依赖的 crypto.dll/ssl.dll，
        //      缺失时从本机已安装的 QQ 目录补齐。
        EnsureWrapperDependencies(logAction);

        // ── 1. 生成 OneBot11 配置（反向 WS 指向启动器）与 WebUI 配置 ──
        WriteOneBotConfig(wsPort, botQQ);
        WriteWebUiConfig();

        // ── 2. 启动反向 WS 服务端 ──
        lock (_sync)
        {
            _serverCts?.Cancel();
            _serverCts?.Dispose();
            _serverCts = new CancellationTokenSource();
        }
        _listener = new TcpListener(IPAddress.Loopback, wsPort);
        _listener.Start();
        var serverToken = _serverCts.Token;
        _ = AcceptLoopAsync(_listener, serverToken);

        UpdateState(running: false, $"NapCat 启动中... 反向 WS 端口 {wsPort}");
        logAction?.Invoke($"[NapCat] 反向 WS 服务端已就绪: ws://127.0.0.1:{wsPort}");

        // ── 3. 启动进程：node.exe index.js（NapCat.Shell.Windows.Node 自助包入口） ──
        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            Arguments = BuildIndexJsArgs(botQQ),
            WorkingDirectory = PathHelper.NapCatDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // 让 NapCat 的数据（config/logs/cache）全部落在启动器托管的 workdir 里
        psi.Environment["NAPCAT_WORKDIR"] = PathHelper.NapCatWorkDir;
        // 纯 Node 单进程模式：绕开 --no-sandbox worker 启动缺陷
        psi.Environment["NAPCAT_DISABLE_MULTI_PROCESS"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        (int Pid, DateTime? Start) qqBefore = default;   // Exited 回调要用，必须提前声明
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (!IsRoutineTraffic(e.Data)) FileLog("[out] " + e.Data);
            if (LooksLikeLoginFailure(e.Data)) _loginInvalidSignal = true;
            logAction?.Invoke($"[NapCat] {e.Data}");
            TryParseWebUiUrl(e.Data);
            TryParseLoginHint(e.Data);
            if (e.Data.Contains("WebUi", StringComparison.OrdinalIgnoreCase) &&
                e.Data.Contains("Url", StringComparison.OrdinalIgnoreCase))
                readyTcs.TrySetResult(true);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            FileLog("[err] " + e.Data);
            if (LooksLikeLoginFailure(e.Data)) _loginInvalidSignal = true;
            logAction?.Invoke($"[NapCat] {e.Data}");
            TryParseWebUiUrl(e.Data);
            TryParseLoginHint(e.Data);
        };

        process.Exited += (_, _) =>
        {
            lock (_sync)
            {
                _process = null;
            }
            readyTcs.TrySetResult(false);

            // ── 退出归因：退出码 + 距今多久 + QQ 是否重启过 ──
            int exitCode = -1;
            try { exitCode = process.ExitCode; } catch { }
            var qqAfter = SnapshotQq();
            string qqReason;
            if (qqBefore.Pid == 0 && qqAfter.Pid == 0)
            {
                // NapCat.Shell 是**内嵌托管 QQNT**，本来就不会有独立的 QQ.exe 进程，
                // 所以"没有 QQ 进程"不能推断成"QQ 崩了/没运行"（2026-10-28 实测踩到）。
                qqReason = "无独立 QQ 进程（NapCat 内嵌托管 QQNT，属正常；此路径下无法用 QQ 进程判断是否热更新）";
            }
            else if (qqAfter.Pid == 0)
                qqReason = $"QQ 已退出（启动前 pid {qqBefore.Pid}）";
            else if (qqBefore.Pid != 0 && (qqAfter.Pid != qqBefore.Pid || qqAfter.Start != qqBefore.Start))
                qqReason = $"QQ 已重启（pid {qqBefore.Pid}→{qqAfter.Pid}），很可能是 QQ 热更新/重启导致 NapCat 随之退出";
            else qqReason = $"QQ 仍在运行（pid {qqAfter.Pid}），说明是 NapCat 自身退出";

            var reason = $"进程退出（码 {exitCode}）；{qqReason}";
            FileLog("退出: " + reason);
            logAction?.Invoke($"[NapCat] {reason}");

            var st = $"NapCat 已退出：{reason}";
            UpdateState(running: false, st);

            // ── 看门狗（默认关闭）：仅"用户期望运行 + 非用户手动停止 + 非登录失效"时重启 ──
            // 只有"确实存在独立 QQ 进程、且现在没了"才算 QQ 退出；
            // NapCat.Shell 内嵌托管 QQNT 时 qqPid 一直是 0，不能据此判断
            var qqReallyGone = qqBefore.Pid != 0 && qqAfter.Pid == 0;
            try { MaybeScheduleWatchdogRestart(exitCode, qqReallyGone, logAction); } catch { }
        };

        qqBefore = SnapshotQq();
        process.Start();
        // 绑 Job Object：启动器崩溃/被强关时，NapCat 也不会残留
        JobObjectHelper.TryAssign(process);
        FileLog($"启动 NapCat: pid={process.Id} QQ pid={qqBefore.Pid} QQ 启动于 {qqBefore.Start?.ToString("yyyy-MM-dd HH:mm:ss") ?? "?"}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_sync)
            _process = process;

        UpdateState(running: true, "NapCat 已启动，等待 QQ 登录...");
        logAction?.Invoke($"[NapCat] 进程已启动 (PID: {process.Id})，登录页: {WebUiUrl}");

        // 等待 WebUI 或首次 WS 连接，最长 20 秒（超时不阻断，进程仍在后台跑）
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var ready = await readyTcs.Task.WaitAsync(timeoutCts.Token);
            if (!ready && process.HasExited)
                UpdateState(running: false, "NapCat 启动失败，进程已退出，请查看日志后重试");
            else
                UpdateState(running: true, _qrWaiting ? GetQrHintText() : $"NapCat 已启动，登录页: {WebUiUrl}");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                UpdateState(running: true, _qrWaiting ? GetQrHintText() : $"NapCat 运行中，登录页: {WebUiUrl}");
        }
    }

    /// <summary>停止 NapCat 进程并关闭反向 WS 服务端</summary>
    public async Task StopAsync()
    {
        _userStopped = true;           // 用户主动停止 → 看门狗不得重启
        Process? process;
        lock (_sync)
        {
            process = _process;
            _serverCts?.Cancel();
        }

        try { _listener?.Stop(); } catch { /* ignore */ }

        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await Task.Run(() => process.WaitForExit(3000));
                }
            }
            catch { /* ignore */ }
            finally
            {
                process.Dispose();
                lock (_sync) _process = null;
            }
        }

        lock (_sync)
        {
            foreach (var client in _clients)
                client.Dispose();
            _clients.Clear();
            _serverCts?.Dispose();
            _serverCts = null;
        }

        UpdateState(running: false, "NapCat 已停止");
    }

    /// <summary>
    /// 把机器人的 [QQ-OUT] 消息发回 QQ：带群号走 send_group_msg，
    /// 只有 QQ 号则走 send_private_msg。
    /// </summary>
    private async Task SendQQOutAsync(Models.QQOutMessage msg)
    {
        if (string.IsNullOrEmpty(msg.Text)) return;

        try
        {
            if (!string.IsNullOrEmpty(msg.GroupId))
            {
                await SendGroupMessageAsync(msg.GroupId, msg.Text);
            }
            else if (!string.IsNullOrEmpty(msg.QQ))
            {
                await SendPrivateMessageAsync(msg.QQ, msg.Text);
            }
        }
        catch { /* 单条发送失败不影响后续 */ }
    }

    /// <summary>向指定群发送文本消息（OneBot11 send_group_msg）</summary>
    public async Task SendGroupMessageAsync(string groupId, string text)
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "send_group_msg",
            @params = new { group_id = long.Parse(groupId), message = text },
            echo = $"bakabot-{Interlocked.Increment(ref _echoSeq)}"
        });
        await SendToAllAsync(payload);
    }

    /// <summary>向指定 QQ 发送私聊文本消息（OneBot11 send_private_msg）</summary>
    public async Task SendPrivateMessageAsync(string qq, string text)
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "send_private_msg",
            @params = new { user_id = long.Parse(qq), message = text },
            echo = $"bakabot-{Interlocked.Increment(ref _echoSeq)}"
        });
        await SendToAllAsync(payload);
    }

    // ─────────────────────────── 反向 WS 服务端 ───────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(tcp, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        var client = new WsClient(tcp);
        try
        {
            var stream = tcp.GetStream();
            var header = await ReadHttpHeaderAsync(stream, ct);
            if (header == null) return;

            var key = Regex.Match(header, @"Sec-WebSocket-Key:\s*(.+)\r?\n", RegexOptions.IgnoreCase);
            if (!key.Success) return;

            var accept = ComputeAcceptKey(key.Groups[1].Value.Trim());
            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\n" +
                           "Connection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            var respBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(respBytes.AsMemory(0, respBytes.Length), ct);

            lock (_sync)
                _clients.Add(client);
            var localPort = tcp.Client?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;
            lock (_sync)
            {
                _qrWaiting = false;
                _qrCodePath = "";
            }
            UpdateState(IsRunning, $"NapCat 已连接（ws://127.0.0.1:{localPort}）");

            await ReadFramesAsync(client, ct);
        }
        catch { /* 连接断开/取消，忽略 */ }
        finally
        {
            lock (_sync)
                _clients.Remove(client);
            client.Dispose();

            // WS 断开时也更新状态：此前只有"连上"时更新，断线后界面仍显示"已连接"，
            // 会让人误以为一切正常（2026-10-28 用户反馈"浏览器显示失效"时的困惑点）。
            try
            {
                bool anyLeft;
                lock (_sync) anyLeft = _clients.Count > 0;
                if (!anyLeft && IsRunning)
                {
                    FileLog("WS 连接已断开（无剩余客户端）");
                    UpdateState(IsRunning, "NapCat 进程在跑，但与启动器的 WS 连接已断开（会自动重连）");
                }
            }
            catch { }
        }
    }

    private async Task ReadFramesAsync(WsClient client, CancellationToken ct)
    {
        var stream = client.Stream;
        var headerBuf = new byte[14];
        var mask = new byte[4];

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactlyAsync(stream, headerBuf, 0, 2, ct)) return;

            var opcode = headerBuf[0] & 0x0F;
            var masked = (headerBuf[1] & 0x80) != 0;
            ulong len = (ulong)(headerBuf[1] & 0x7F);

            if (len == 126)
            {
                if (!await ReadExactlyAsync(stream, headerBuf, 0, 2, ct)) return;
                len = (ulong)((headerBuf[0] << 8) | headerBuf[1]);
            }
            else if (len == 127)
            {
                if (!await ReadExactlyAsync(stream, headerBuf, 0, 8, ct)) return;
                len = 0;
                for (var i = 0; i < 8; i++)
                    len = (len << 8) | headerBuf[i];
            }

            if (len > 4 * 1024 * 1024) return; // 防御超大帧

            if (masked && !await ReadExactlyAsync(stream, mask, 0, 4, ct)) return;

            var payload = new byte[len];
            if (len > 0 && !await ReadExactlyAsync(stream, payload, 0, (int)len, ct)) return;

            if (masked)
            {
                for (var i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i % 4];
            }

            switch (opcode)
            {
                case 0x1: // text
                    HandleOneBotMessage(Encoding.UTF8.GetString(payload));
                    break;
                case 0x8: // close
                    await SendFrameAsync(stream, 0x8, Array.Empty<byte>(), ct);
                    return;
                case 0x9: // ping → pong
                    await SendFrameAsync(stream, 0xA, payload, ct);
                    break;
            }
        }
    }

    private void HandleOneBotMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("post_type", out var postType)) return;

            var type = postType.GetString();
            if (type == "message" &&
                root.TryGetProperty("message_type", out var msgType) &&
                msgType.GetString() == "group")
            {
                var groupId = root.TryGetProperty("group_id", out var g) ? g.GetInt64().ToString() : string.Empty;
                var userId = root.TryGetProperty("user_id", out var u) ? u.GetInt64().ToString() : string.Empty;
                var raw = root.TryGetProperty("raw_message", out var rm) ? rm.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(userId)) return;

                // 白名单过滤与转发逻辑全部在 QQService 里，这里只做桥接
                _ = _qqService.HandleGroupMessageAsync(groupId, userId, raw);
            }
        }
        catch
        {
            // 非 JSON / 非事件帧，忽略
        }
    }

    private async Task SendToAllAsync(string json)
    {
        List<WsClient> snapshot;
        lock (_sync)
            snapshot = _clients.ToList();

        foreach (var client in snapshot)
        {
            try
            {
                var payload = Encoding.UTF8.GetBytes(json);
                await SendFrameAsync(client.Stream, 0x1, payload, CancellationToken.None);
            }
            catch
            {
                lock (_sync)
                    _clients.Remove(client);
                client.Dispose();
            }
        }
    }

    private static async Task SendFrameAsync(NetworkStream stream, byte opcode, byte[] payload, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | opcode));
        if (payload.Length <= 125)
        {
            ms.WriteByte((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            ms.WriteByte(126);
            ms.WriteByte((byte)(payload.Length >> 8));
            ms.WriteByte((byte)payload.Length);
        }
        else
        {
            ms.WriteByte(127);
            var len = (ulong)payload.Length;
            for (var shift = 56; shift >= 0; shift -= 8)
                ms.WriteByte((byte)(len >> shift));
        }
        ms.Write(payload, 0, payload.Length);

        var bytes = ms.ToArray();
        await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct);
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static async Task<string?> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0) return null;
            total += n;
            if (IndexOfDoubleCrlf(buffer, total) >= 0)
                return Encoding.UTF8.GetString(buffer, 0, total);
        }
        return null;
    }

    private static int IndexOfDoubleCrlf(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == 13 && buffer[i + 1] == 10 &&
                buffer[i + 2] == 13 && buffer[i + 3] == 10)
                return i;
        }
        return -1;
    }

    private static string ComputeAcceptKey(string key)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var bytes = Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
        var hash = sha1.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    // ─────────────────────────── 配置生成 ───────────────────────────

    /// <summary>
    /// 写 OneBot11 配置：反向 WS 客户端指向启动器。
    /// 同时写默认 onebot11.json（首次登录任意 QQ 都会套用）与按 QQ 号命名的文件。
    /// </summary>
    private void WriteOneBotConfig(int wsPort, string botQQ)
    {
        var configDir = PathHelper.NapCatConfigDir;
        Directory.CreateDirectory(configDir);

        var config = new
        {
            network = new
            {
                httpServers = Array.Empty<object>(),
                httpSseServers = Array.Empty<object>(),
                httpClients = Array.Empty<object>(),
                websocketServers = Array.Empty<object>(),
                websocketClients = new[]
                {
                    new
                    {
                        name = "bakabot",
                        enable = true,
                        url = $"ws://127.0.0.1:{wsPort}",
                        messagePostFormat = "array",
                        reportSelfMessage = false,
                        reconnectInterval = 3000,
                        token = "",
                        debug = false,
                        heartInterval = 30000
                    }
                }
            },
            musicSignUrl = "",
            enableLocalFile2Url = false,
            parseMultMsg = false
        };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(configDir, "onebot11.json"), json);
        if (!string.IsNullOrWhiteSpace(botQQ))
            File.WriteAllText(Path.Combine(configDir, $"onebot11_{botQQ}.json"), json);
    }

    /// <summary>固定 WebUI 监听地址与初始 token，便于扫码登录</summary>
    private void WriteWebUiConfig()
    {
        var configDir = PathHelper.NapCatConfigDir;
        Directory.CreateDirectory(configDir);

        var config = new
        {
            host = "127.0.0.1",
            port = WebUiPort,
            token = WebUiToken,
            loginRate = 3
        };
        File.WriteAllText(
            Path.Combine(configDir, "webui.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void TryParseWebUiUrl(string line)
    {
        var match = Regex.Match(line, @"https?://127\.0\.0\.1:\d+/webui\S*", RegexOptions.IgnoreCase);
        if (!match.Success) return;

        lock (_sync)
            _webUiUrl = match.Value;
    }

    /// <summary>
    /// 监听 NapCat 输出的登录状态：无历史会话进入二维码登录时，把状态栏切为醒目的
    /// “等待扫码”提示，避免进程看似已启动、QQ 却一直收不到消息的困惑。
    /// </summary>
    private void TryParseLoginHint(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // 进入等待扫码：NapCat 会提示快速登录失败 / 请扫描二维码 / 二维码已保存到路径
        var waiting = line.Contains("请扫描", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("快速登录失败", StringComparison.OrdinalIgnoreCase)
                && line.Contains("二维码", StringComparison.OrdinalIgnoreCase))
            || line.Contains("二维码已保存", StringComparison.OrdinalIgnoreCase);

        // 登录成功或会话恢复：清除扫码提示（WS 连接建立时也会再次兜底清除）
        var loggedIn = line.Contains("登录成功", StringComparison.OrdinalIgnoreCase)
            || line.Contains("login success", StringComparison.OrdinalIgnoreCase);

        if (loggedIn)
        {
            lock (_sync)
            {
                _qrWaiting = false;
                _qrCodePath = "";
            }
            return;
        }

        if (!waiting) return;

        var now = Environment.TickCount64;
        lock (_sync)
        {
            if (now - _lastQrHintTick < 1500) return; // 节流，避免连续输出反复刷新状态
            _lastQrHintTick = now;
            _qrWaiting = true;

            // 优先取 NapCat 自己提示的二维码图片路径，取不到则用固定缓存路径兜底
            var pathMatch = Regex.Match(line, @"[A-Za-z]:[^，。\s]*qrcode\.png", RegexOptions.IgnoreCase);
            if (pathMatch.Success)
                _qrCodePath = pathMatch.Value;
            else if (string.IsNullOrEmpty(_qrCodePath))
            {
                var fallback = Path.Combine(PathHelper.NapCatWorkDir, "cache", "qrcode.png");
                _qrCodePath = File.Exists(fallback) ? fallback : "";
            }
        }
        UpdateState(IsRunning, GetQrHintText());
    }

    private string GetQrHintText()
    {
        lock (_sync)
        {
            return _qrCodePath.Length > 0
                ? $"NapCat 等待扫码登录：请点「打开登录页」用手机 QQ 扫码（二维码图片：{_qrCodePath}）"
                : "NapCat 等待扫码登录：请点「打开登录页」用手机 QQ 扫码";
        }
    }

    /// <summary>组装入口参数：填了机器人 QQ 号时传 -q 走快速登录（本地有该号会话则免扫码）</summary>
    private static string BuildIndexJsArgs(string botQQ)
    {
        var digits = new string((botQQ ?? string.Empty).Where(char.IsDigit).ToArray());
        return string.IsNullOrEmpty(digits) ? "index.js" : $"index.js -q {digits}";
    }

    /// <summary>
    /// NapCat v4.18.x 的 Windows.Node 包漏打包 crypto.dll/ssl.dll，
    /// 而 wrapper.node 启动时静态依赖它们（缺失会直接报“找不到指定的模块”）。
    /// 启动前自动调用兜底；QQ 页“完整性检验”也会主动调用并展示完整结果。
    /// 补齐顺序：先 NapCat 包内递归查找，再从本机 QQ 安装目录查找；
    /// 找到后 File.Copy 复制（只复制、不移动原文件、不覆盖已存在文件）。
    /// </summary>
    public string CheckWrapperDependencies(Action<string>? logAction = null)
    {
        var napcatDir = PathHelper.NapCatDir;
        var all = new[] { "crypto.dll", "ssl.dll" };
        var sb = new StringBuilder();

        bool Exists(string f) => File.Exists(Path.Combine(napcatDir, f));

        var missing = all.Where(f => !Exists(f)).ToList();
        if (missing.Count == 0)
        {
            sb.Append("✓ 完整性正常：crypto.dll 与 ssl.dll 均已存在（")
              .Append(Path.Combine(napcatDir, "crypto.dll")).Append("、")
              .Append(Path.Combine(napcatDir, "ssl.dll"))
              .Append("），无需补齐。");
            return sb.ToString();
        }

        sb.Append("检测到缺失：").Append(string.Join("、", missing)).Append("。开始自动补齐：").AppendLine();

        // 1. 先在 NapCat 包内递归找：后续版本可能把依赖放进子目录（如 versions/<ver>/resources/app/）
        foreach (var file in missing.ToList())
        {
            string? found = null;
            try
            {
                found = Directory.EnumerateFiles(napcatDir, file, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                // 目录不可枚举时忽略，继续走 QQ 安装目录兜底
            }
            if (found == null) continue;
            try
            {
                File.Copy(found, Path.Combine(napcatDir, file), overwrite: true);
                sb.Append("· ").Append(file).Append(" 缺失，已从 NapCat 包内找到并复制补齐：").Append(found).AppendLine();
                logAction?.Invoke($"[NapCat] 已从 NapCat 包内补齐缺失的 {file}（完整性检验）");
                missing.Remove(file);
            }
            catch (Exception ex)
            {
                sb.Append("· ").Append(file).Append(" 从 NapCat 包内复制失败：").Append(ex.Message).AppendLine();
            }
        }

        // 2. 包内没有时，从本机已安装的 QQ 目录补齐
        if (missing.Count > 0)
        {
            var qqRoot = FindInstalledQQRoot();
            if (qqRoot == null)
            {
                sb.Append("· 未找到本机 QQ 安装目录，无法从 QQ 目录补齐。").AppendLine();
            }
            else
            {
                foreach (var file in missing.ToList())
                {
                    string? found = null;
                    try
                    {
                        found = Directory.EnumerateFiles(qqRoot, file, SearchOption.AllDirectories).FirstOrDefault();
                    }
                    catch
                    {
                        // 安装目录不可读时忽略
                    }
                    if (found == null) continue;
                    try
                    {
                        File.Copy(found, Path.Combine(napcatDir, file), overwrite: true);
                        sb.Append("· ").Append(file).Append(" 缺失，已从本机 QQ 安装目录复制补齐：").Append(found).AppendLine();
                        logAction?.Invoke($"[NapCat] 已从 QQ 安装目录补齐缺失的 {file}（完整性检验）");
                        missing.Remove(file);
                    }
                    catch (Exception ex)
                    {
                        sb.Append("· ").Append(file).Append(" 从 QQ 安装目录复制失败：").Append(ex.Message).AppendLine();
                    }
                }
            }
        }

        // 3. 汇总最终结果
        var stillMissing = all.Where(f => !Exists(f)).ToList();
        if (stillMissing.Count == 0)
        {
            sb.Append("✓ 补齐完成：crypto.dll 与 ssl.dll 现已全部就位，NapCat 可以正常启动。");
        }
        else
        {
            sb.Append("✗ 仍缺失：").Append(string.Join("、", stillMissing))
              .Append("。NapCat 包内与本机 QQ 安装目录都没有找到可复制的文件，NapCat 可能无法启动；请检查 NapCat 包是否完整，或安装 QQ 后重试。");
        }
        return sb.ToString();
    }

    /// <summary>启动前兜底：自动补齐缺失依赖（无缺失时直接返回，不打扰启动流程）</summary>
    private void EnsureWrapperDependencies(Action<string>? logAction)
    {
        try
        {
            _ = CheckWrapperDependencies(logAction);
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[NapCat] 补齐缺失依赖失败: {ex.Message}");
        }
    }

    /// <summary>通过注册表找到本机 QQ 安装目录（与 NapCat 官方 launcher.bat 同一路径来源）</summary>
    private static string? FindInstalledQQRoot()
    {
        try
        {
            const string wowKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\QQ";
            const string nativeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QQ";

            string? uninstall = null;
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(wowKey))
                uninstall = k?.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(uninstall))
            {
                using var k2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(nativeKey);
                uninstall = k2?.GetValue("UninstallString") as string;
            }

            if (!string.IsNullOrWhiteSpace(uninstall))
            {
                var dir = Path.GetDirectoryName(uninstall.Trim().Trim('"'));
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }

            // 注册表没找到时，扫描常见安装位置
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tencent", "QQNT"),
                @"C:\Program Files\Tencent\QQNT",
                @"C:\Program Files\Tencent\QQ",
                @"C:\Program Files (x86)\Tencent\QQ",
                @"D:\QQ",
                @"E:\QQ"
            };
            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ─────────────────────────── 状态与工具 ───────────────────────────

    private void UpdateState(bool running, string statusText)
    {
        string url;
        lock (_sync)
        {
            _statusText = statusText;
            url = _webUiUrl;
        }
        StateChanged?.Invoke(this, new NapCatState(running, statusText, url));
    }

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
        lock (_sync)
        {
            _serverCts?.Cancel();
            try { _listener?.Stop(); } catch { /* ignore */ }
            foreach (var client in _clients)
                client.Dispose();
            _clients.Clear();
        }
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                _process.Dispose();
            }
            catch { /* ignore */ }
            _process = null;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>NapCat 运行状态快照，供 UI 绑定/刷新</summary>
    public sealed class NapCatState
    {
        public bool Running { get; }
        public string StatusText { get; }
        public string WebUiUrl { get; }

        public NapCatState(bool running, string statusText, string webUiUrl)
        {
            Running = running;
            StatusText = statusText;
            WebUiUrl = webUiUrl;
        }
    }

    private sealed class WsClient : IDisposable
    {
        public TcpClient Tcp { get; }
        public NetworkStream Stream { get; }

        public WsClient(TcpClient tcp)
        {
            Tcp = tcp;
            Stream = tcp.GetStream();
        }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { /* ignore */ }
            try { Tcp.Dispose(); } catch { /* ignore */ }
        }
    }
}
