using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bakabot.Models;

namespace Bakabot.Services;

/// <summary>
/// 通用 OneBot11 反向 WebSocket 接入服务（端口固定默认 6700）：
/// 供 llbot、Lagrange 等外部协议端以 WebSocket 客户端身份接入，
/// 复用现有 QQService 的白名单 / 群过滤 / 触发词 / 转发逻辑。
/// - 群消息事件 → QQService.HandleGroupMessageAsync（与 NapCat 接入一致）
/// - 机器人 [QQ-OUT] 消息 → send_group_msg / send_private_msg 动作
/// - 可选 Token 鉴权：客户端通过 Authorization: Bearer 或 ?access_token= 携带
/// - 与 NapCat 互斥：本服务启动时停 NapCat；NapCat 启动时也会顶掉本服务
/// </summary>
public class OneBot11ServerService : IDisposable
{
    private const int DefaultPort = 6700;

    private readonly SettingsService _settingsService;
    private readonly QQService _qqService;
    private readonly NapCatService _napCatService;

    private readonly object _sync = new();
    private readonly List<WsClient> _clients = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private long _echoSeq;
    private int _port = DefaultPort;
    private string _token = string.Empty;
    private string _statusText = "未启动";

    /// <summary>状态变化事件（启停、客户端数量、端口等），供 UI 刷新</summary>
    public event EventHandler<OneBot11State>? StateChanged;

    public OneBot11ServerService(SettingsService settingsService, QQService qqService, NapCatService napCatService)
    {
        _settingsService = settingsService;
        _qqService = qqService;
        _napCatService = napCatService;
        ProtocolServerCoordinator.OneBotServer = this;
    }

    public bool IsRunning
    {
        get { lock (_sync) return _listener != null; }
    }

    public int Port
    {
        get { lock (_sync) return _port; }
    }

    public int ClientCount
    {
        get { lock (_sync) return _clients.Count; }
    }

    public string StatusText
    {
        get { lock (_sync) return _statusText; }
    }

    /// <summary>应用启动时调用：订阅机器人 QQ 输出，准备发回外部协议端</summary>
    public void Initialize()
    {
        _qqService.QQOutReceived += (_, msg) =>
        {
            _ = SendQQOutAsync(msg);
        };

        // 互斥保险：NapCat 真正运行起来时（含启动过程中被并发拉起的情况），
        // 顶掉外部 OneBot11 反向接入，避免两边同时收发导致重复转发/重复回复。
        _napCatService.StateChanged += (_, state) =>
        {
            if (state.Running)
                _ = StopAsync();
        };
    }

    /// <summary>启动反向 WS 服务端（固定端口 6700）；若 NapCat 正在运行则先停掉它</summary>
    public async Task StartAsync(Action<string>? logAction = null)
    {
        if (IsRunning)
        {
            UpdateState("已在运行");
            return;
        }

        // 互斥：外部接入启用时顶掉 NapCat
        if (_napCatService.IsRunning)
            await _napCatService.StopAsync();

        var port = _settingsService.Settings.OneBotPort;
        if (port is < 1 or > 65535) port = DefaultPort;
        var token = _settingsService.Settings.OneBotToken?.Trim() ?? string.Empty;

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (Exception ex)
        {
            var message = $"启动失败：端口 {port} 被占用或不可用（{ex.Message}）";
            UpdateState(message);
            throw new InvalidOperationException($"OneBot 反向接入{message}", ex);
        }

        lock (_sync)
        {
            _serverCts?.Cancel();
            _serverCts?.Dispose();
            _serverCts = new CancellationTokenSource();
            _listener = listener;
            _port = port;
            _token = token;
            _statusText = $"已启动，等待外部客户端接入（ws://127.0.0.1:{port}）";
        }
        var ct = _serverCts.Token;
        _ = AcceptLoopAsync(listener, ct);

        logAction?.Invoke($"[OneBot11] 反向 WS 服务端已启动: ws://127.0.0.1:{port}");
        UpdateState();
    }

    /// <summary>停止反向 WS 服务端并断开所有客户端</summary>
    public Task StopAsync()
    {
        TcpListener? listener;
        lock (_sync)
        {
            listener = _listener;
            _serverCts?.Cancel();
        }

        try { listener?.Stop(); } catch { /* ignore */ }

        lock (_sync)
        {
            foreach (var client in _clients)
                client.Dispose();
            _clients.Clear();
            _serverCts?.Dispose();
            _serverCts = null;
            _listener = null;
            _statusText = "已停止";
        }
        UpdateState();
        return Task.CompletedTask;
    }

    /// <summary>把机器人的 [QQ-OUT] 消息发回外部协议端：带群号走 send_group_msg，只有 QQ 号走 send_private_msg</summary>
    private async Task SendQQOutAsync(QQOutMessage msg)
    {
        if (string.IsNullOrEmpty(msg.Text)) return;

        try
        {
            if (!string.IsNullOrEmpty(msg.GroupId))
                await SendGroupMessageAsync(msg.GroupId, msg.Text);
            else if (!string.IsNullOrEmpty(msg.QQ))
                await SendPrivateMessageAsync(msg.QQ, msg.Text);
        }
        catch
        {
            // 单条发送失败不影响后续
        }
    }

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

    // ─── 反向 WS 服务端 ───

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

            // 可选 Token 鉴权：不匹配直接 401 拒绝
            if (!ValidateToken(header))
            {
                var reject = "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                var rejectBytes = Encoding.ASCII.GetBytes(reject);
                await stream.WriteAsync(rejectBytes.AsMemory(0, rejectBytes.Length), ct);
                return;
            }

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
            UpdateState($"外部客户端已接入（ws://127.0.0.1:{Port}，当前 {ClientCount} 个）");

            await ReadFramesAsync(client, ct);
        }
        catch
        {
            // 连接断开/取消，忽略
        }
        finally
        {
            bool hasOthers;
            lock (_sync)
            {
                _clients.Remove(client);
                hasOthers = _clients.Count > 0;
            }
            client.Dispose();
            UpdateState(hasOthers ? null : $"已启动，等待外部客户端接入（ws://127.0.0.1:{Port}）");
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
            if (!root.TryGetProperty("post_type", out var postType) ||
                postType.GetString() != "message") return;
            if (!root.TryGetProperty("message_type", out var msgType)) return;

            if (msgType.GetString() == "group")
            {
                var groupId = root.TryGetProperty("group_id", out var g) ? g.GetInt64().ToString() : string.Empty;
                var userId = root.TryGetProperty("user_id", out var u) ? u.GetInt64().ToString() : string.Empty;
                var raw = root.TryGetProperty("raw_message", out var rm) ? rm.GetString() ?? string.Empty : string.Empty;

                // 容错：raw_message 缺失时回退到 string 格式的 message 字段
                if (string.IsNullOrEmpty(raw) &&
                    root.TryGetProperty("message", out var msgEl) &&
                    msgEl.ValueKind == JsonValueKind.String)
                    raw = msgEl.GetString() ?? string.Empty;

                if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(userId)) return;

                // 白名单过滤与转发逻辑全部在 QQService 里，这里只做桥接
                _ = _qqService.HandleGroupMessageAsync(groupId, userId, raw);
            }
            // 私聊暂不处理（与 NapCat 接入保持一致）
        }
        catch
        {
            // 非 JSON / 非事件帧，忽略
        }
    }

    private bool ValidateToken(string header)
    {
        string token;
        lock (_sync) token = _token;
        if (string.IsNullOrEmpty(token)) return true;

        var authMatch = Regex.Match(header, @"Authorization:\s*Bearer\s+([^\s\r\n]+)", RegexOptions.IgnoreCase);
        if (authMatch.Success && authMatch.Groups[1].Value == token) return true;

        var queryMatch = Regex.Match(header, @"access_token=([^&\s\r\n]+)", RegexOptions.IgnoreCase);
        if (queryMatch.Success)
        {
            try
            {
                if (Uri.UnescapeDataString(queryMatch.Groups[1].Value) == token) return true;
            }
            catch
            {
                // 忽略解析失败
            }
        }
        return false;
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

    // ─── 状态与工具 ───

    private void UpdateState(string? statusText = null)
    {
        int count;
        bool running;
        int port;
        lock (_sync)
        {
            if (statusText != null)
                _statusText = statusText;
            count = _clients.Count;
            running = _listener != null;
            port = _port;
        }
        StateChanged?.Invoke(this, new OneBot11State(running, count, _statusText, port));
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
            _serverCts?.Dispose();
            _serverCts = null;
            _listener = null;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>OneBot 反向接入运行状态快照，供 UI 绑定/刷新</summary>
    public sealed class OneBot11State
    {
        public bool Running { get; }
        public int ClientCount { get; }
        public string StatusText { get; }
        public int Port { get; }

        public OneBot11State(bool running, int clientCount, string statusText, int port)
        {
            Running = running;
            ClientCount = clientCount;
            StatusText = statusText;
            Port = port;
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
