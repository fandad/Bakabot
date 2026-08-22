using System.Net;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bakabot.Models;

namespace Bakabot.Services;

/// <summary>
/// MCP 本地接口（第一步）：仅监听 127.0.0.1，供 MCP 服务进程/外部 AI 调用机器人。
/// 认证：Authorization: Bearer &lt;token&gt; 或 ?token=；Token 首次启用自动生成并持久化到 settings.json。
/// 接口：
///   GET  /api/status                   → 服务信息
///   GET  /api/instances                → 实例列表与运行状态
///   POST /api/instances/{name}/command → body { "text": "指令" }，发送 QQCMD 并等待机器人回复
/// </summary>
public class LocalApiService : IDisposable
{
    private const int Port = 8726;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly SettingsService _settingsService;
    private readonly InstanceManager _instanceManager;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public LocalApiService(SettingsService settingsService, InstanceManager instanceManager)
    {
        _settingsService = settingsService;
        _instanceManager = instanceManager;
    }

    /// <summary>按设置启动（未启用则不监听）。Token 为空时自动生成。</summary>
    public void Start()
    {
        if (!_settingsService.Settings.MCPEnabled) return;
        if (_listener != null) return;

        var token = _settingsService.Settings.MCPToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Guid.NewGuid().ToString("N");
            _settingsService.UpdateSettings(s => s.MCPToken = token);
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            // 端口被占用/无权限时静默失败，不影响启动器
            _listener = null;
            System.Diagnostics.Debug.WriteLine($"[MCP] 本地接口启动失败: {ex.Message}");
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync();
            }
            catch
            {
                break;
            }
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!IsAuthorized(ctx.Request))
            {
                await WriteJsonAsync(ctx, 403, new { error = "unauthorized" });
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            if (method == "GET" && path == "/api/status")
            {
                await WriteJsonAsync(ctx, 200, new
                {
                    name = "Bakabot MCP Local API",
                    version = "2.5.0",
                    enabled = true,
                });
                return;
            }

            if (method == "GET" && path == "/api/instances")
            {
                var list = _instanceManager.Instances.Select(i => new
                {
                    name = i.InstanceName,
                    running = _instanceManager.RunningProcesses.ContainsKey(i.InstanceName),
                    status = i.Status.ToString(),
                    owner = i.McOwnerName,
                }).ToList();
                await WriteJsonAsync(ctx, 200, new { instances = list });
                return;
            }

            var m = Regex.Match(path, @"^/api/instances/([^/]+)/command$", RegexOptions.IgnoreCase);
            if (method == "POST" && m.Success)
            {
                var instanceName = Uri.UnescapeDataString(m.Groups[1].Value);
                await HandleCommandAsync(ctx, instanceName);
                return;
            }

            await WriteJsonAsync(ctx, 404, new { error = "not_found" });
        }
        catch (Exception ex)
        {
            try { await WriteJsonAsync(ctx, 500, new { error = ex.Message }); }
            catch { }
        }
    }

    private async Task HandleCommandAsync(HttpListenerContext ctx, string instanceName)
    {
        var body = await new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
        CommandRequest? req = null;
        try { req = JsonSerializer.Deserialize<CommandRequest>(body); } catch { }
        if (req == null || string.IsNullOrWhiteSpace(req.Text))
        {
            await WriteJsonAsync(ctx, 400, new { error = "body 需要 {\"text\":\"指令\"}" });
            return;
        }

        var instance = _instanceManager.Instances.FirstOrDefault(i => i.InstanceName == instanceName);
        if (instance == null)
        {
            await WriteJsonAsync(ctx, 404, new { error = $"实例 {instanceName} 不存在" });
            return;
        }
        if (!_instanceManager.RunningProcesses.TryGetValue(instanceName, out var pm))
        {
            await WriteJsonAsync(ctx, 409, new { error = $"实例 {instanceName} 未在运行" });
            return;
        }

        var payload = JsonSerializer.Serialize(new QQCmdMessage
        {
            QQ = "mcp",
            Player = string.IsNullOrWhiteSpace(instance.McOwnerName) ? "mcp" : instance.McOwnerName,
            Text = req.Text,
        });
        var line = "QQCMD " + payload;

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ConsoleEntry> handler = (_, entry) =>
        {
            if (!entry.Text.StartsWith("[QQ-OUT] ", StringComparison.Ordinal)) return;
            try
            {
                var json = entry.Text.Substring("[QQ-OUT] ".Length);
                var msg = JsonSerializer.Deserialize<QQOutMessage>(json);
                if (msg != null && !string.IsNullOrEmpty(msg.Text))
                    tcs.TrySetResult(msg.Text);
            }
            catch { }
        };
        pm.OutputReceived += handler;
        try
        {
            await pm.WriteInputAsync(line);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(CommandTimeout)) == tcs.Task;
            var reply = done ? await tcs.Task : null;
            if (reply == null)
            {
                await WriteJsonAsync(ctx, 504, new { error = "机器人响应超时（60 秒）", text = req.Text });
                return;
            }
            await WriteJsonAsync(ctx, 200, new { reply });
        }
        finally
        {
            pm.OutputReceived -= handler;
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var token = _settingsService.Settings.MCPToken;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var auth = request.Headers["Authorization"] ?? string.Empty;
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var provided = auth.Substring("Bearer ".Length).Trim();
            if (FixedTimeEquals(provided, token)) return true;
        }
        var query = request.QueryString["token"];
        if (!string.IsNullOrEmpty(query) && FixedTimeEquals(query, token)) return true;
        return false;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a ?? string.Empty),
            Encoding.UTF8.GetBytes(b ?? string.Empty));

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private sealed class CommandRequest
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>停止监听（保留服务实例，可再次 Start）</summary>
    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener?.Close();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }
}
