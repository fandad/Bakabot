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
    private readonly QQService _qqService;

    private readonly object _sync = new();
    private readonly List<WsClient> _clients = new();

    private Process? _process;
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private long _echoSeq;
    private string _webUiUrl = $"http://127.0.0.1:{WebUiPort}/webui?token={WebUiToken}";
    private string _statusText = "NapCat 未启动";

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
    public async Task StartAsync(Action<string>? logAction = null)
    {
        if (IsRunning)
        {
            UpdateState(running: true, "NapCat 已在运行");
            return;
        }

        if (!IsAvailable)
            throw new InvalidOperationException(
                "NapCat 尚未下载，请先在「设置」或「QQ 功能」页下载后再启动。");

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

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            logAction?.Invoke($"[NapCat] {e.Data}");
            TryParseWebUiUrl(e.Data);
            if (e.Data.Contains("WebUi", StringComparison.OrdinalIgnoreCase) &&
                e.Data.Contains("Url", StringComparison.OrdinalIgnoreCase))
                readyTcs.TrySetResult(true);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            logAction?.Invoke($"[NapCat] {e.Data}");
            TryParseWebUiUrl(e.Data);
        };

        process.Exited += (_, _) =>
        {
            lock (_sync)
            {
                _process = null;
            }
            readyTcs.TrySetResult(false);
            UpdateState(running: false, "NapCat 已退出");
            logAction?.Invoke($"[NapCat] 进程退出（码 {process.ExitCode}）");
        };

        process.Start();
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
                UpdateState(running: true, $"NapCat 已启动，登录页: {WebUiUrl}");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                UpdateState(running: true, $"NapCat 运行中，登录页: {WebUiUrl}");
        }
    }

    /// <summary>停止 NapCat 进程并关闭反向 WS 服务端</summary>
    public async Task StopAsync()
    {
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
            UpdateState(IsRunning, $"NapCat 已连接（ws://127.0.0.1:{localPort}）");

            await ReadFramesAsync(client, ct);
        }
        catch { /* 连接断开/取消，忽略 */ }
        finally
        {
            lock (_sync)
                _clients.Remove(client);
            client.Dispose();
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

    /// <summary>组装入口参数：填了机器人 QQ 号时传 -q 走快速登录（本地有该号会话则免扫码）</summary>
    private static string BuildIndexJsArgs(string botQQ)
    {
        var digits = new string((botQQ ?? string.Empty).Where(char.IsDigit).ToArray());
        return string.IsNullOrEmpty(digits) ? "index.js" : $"index.js -q {digits}";
    }

    /// <summary>
    /// NapCat v4.18.x 的 Windows.Node 包漏打包 crypto.dll/ssl.dll，
    /// 而 wrapper.node 启动时静态依赖它们（缺失会直接报“找不到指定的模块”）。
    /// 缺失时按 NapCat 官方启动器同样的方式，从注册表定位本机 QQ 安装目录并补齐。
    /// </summary>
    private void EnsureWrapperDependencies(Action<string>? logAction)
    {
        try
        {
            var missing = new[] { "crypto.dll", "ssl.dll" }
                .Where(f => !File.Exists(Path.Combine(PathHelper.NapCatDir, f)))
                .ToList();
            if (missing.Count == 0) return;

            // 1. 先在 NapCat 包内递归找：后续版本可能把依赖放进子目录（如 versions/<ver>/resources/app/）
            foreach (var file in missing.ToList())
            {
                var nested = Directory.EnumerateFiles(PathHelper.NapCatDir, file, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (nested == null) continue;
                File.Copy(nested, Path.Combine(PathHelper.NapCatDir, file), overwrite: true);
                missing.Remove(file);
                logAction?.Invoke($"[NapCat] 已在 NapCat 包内找到并补齐缺失的 {file}");
            }
            if (missing.Count == 0) return;

            // 2. 从本机已安装的 QQ 目录补齐
            var qqRoot = FindInstalledQQRoot();
            if (qqRoot == null)
            {
                logAction?.Invoke($"[NapCat] 警告：wrapper.node 缺少 {string.Join("/", missing)}，" +
                                  "且未找到本机 QQ 安装目录，NapCat 可能无法启动");
                return;
            }

            foreach (var file in missing)
            {
                var src = Directory.EnumerateFiles(qqRoot, file, SearchOption.AllDirectories).FirstOrDefault();
                if (src == null) continue;
                File.Copy(src, Path.Combine(PathHelper.NapCatDir, file), overwrite: true);
                logAction?.Invoke($"[NapCat] 已从 QQ 安装目录补齐缺失的 {file}");
            }
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
