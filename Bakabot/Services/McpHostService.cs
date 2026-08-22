using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bakabot.Services;

/// <summary>
/// MCP 服务（内置于启动器）：
/// - HTTP 云端模式：启动器进程内托管 /mcp（Streamable HTTP），API Key + IP 白名单 + 调用日志；
/// - stdio 本地模式：由 `Bakabot.exe --mcp-stdio` 触发，供本机 MCP 客户端调用（连接启动器本地接口）。
/// 这样用户只需要「启动器 + 基础包」即可拥有全部功能。
/// </summary>
public class McpHostService
{
    private const int HttpPort = 8727;

    private readonly SettingsService _settingsService;
    private readonly InstanceManager _instanceManager;
    private readonly object _lock = new();
    private WebApplication? _app;

    public McpHostService(SettingsService settingsService, InstanceManager instanceManager)
    {
        _settingsService = settingsService;
        _instanceManager = instanceManager;
    }

    /// <summary>启动 HTTP 云端模式（设置里开了「云端 HTTP 访问」才生效）</summary>
    public async Task StartAsync()
    {
        lock (_lock)
        {
            if (_app != null) return;
            if (!_settingsService.Settings.MCPHttpEnabled) return;

            var apiKey = _settingsService.Settings.MCPApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = Guid.NewGuid().ToString("N");
                _settingsService.UpdateSettings(s => s.MCPApiKey = apiKey);
            }
        }

        var apiKey2 = _settingsService.Settings.MCPApiKey;
        var whitelist = ParseIpWhitelist(_settingsService.Settings.MCPIpWhitelist);
        var logger = new CallLogger(GetLogPath());
        var token = _settingsService.Settings.MCPToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{HttpPort}");
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = "Bakabot MCP",
                Version = "2.5.0",
            };
        })
        .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
        .WithTools(new BakabotMcpTools("http://127.0.0.1:8726", token, logger));

        var app = builder.Build();

        // 中间件：IP 白名单 → API Key 校验 → 请求日志
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

            if (path == "/healthz")
            {
                await next();
                return;
            }

            if (!IsIpAllowed(ip, whitelist))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("ip not allowed");
                logger.Log("http", ip, ctx.Request.Method + " " + path, "403 ip-blocked");
                return;
            }

            var key = ExtractApiKey(ctx.Request);
            if (string.IsNullOrEmpty(key) || !FixedTimeEquals(key, apiKey2))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("unauthorized");
                logger.Log("http", ip, ctx.Request.Method + " " + path, "401 auth-failed");
                return;
            }

            logger.Log("http", ip, ctx.Request.Method + " " + path, "ok");
            await next();
        });

        app.MapGet("/healthz", () => Results.Json(new { ok = true, name = "Bakabot MCP", version = "2.5.0" }));
        app.MapMcp("/mcp");

        try
        {
            await app.StartAsync();
            lock (_lock) { _app = app; }
            System.Diagnostics.Debug.WriteLine($"[MCP] HTTP 模式监听 http://0.0.0.0:{HttpPort}/mcp");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MCP] HTTP 模式启动失败: {ex.Message}");
            try { await app.DisposeAsync(); } catch { }
        }
    }

    /// <summary>停止 HTTP 云端模式</summary>
    public async Task StopAsync()
    {
        WebApplication? app;
        lock (_lock) { app = _app; _app = null; }
        if (app == null) return;
        try { await app.StopAsync(); } catch { }
        try { await app.DisposeAsync(); } catch { }
    }

    /// <summary>按新配置重启 HTTP 云端模式（白名单/Key 变化时调用）</summary>
    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    /// <summary>
    /// stdio 本地模式：由 `Bakabot.exe --mcp-stdio` 调用。
    /// 连接主启动器的本地接口（127.0.0.1:8726），要求主启动器已在运行。
    /// </summary>
    public static async Task<int> RunStdioAsync(SettingsService settingsService)
    {
        var token = settingsService.Settings.MCPToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("[MCP] 未找到访问 Token：请先启动 Bakabot 并在设置里开启「MCP 本地接口」。");
            return 1;
        }

        var logger = new CallLogger(GetLogPath());
        var services = new ServiceCollection();
        var builder = services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = "Bakabot MCP",
                Version = "2.5.0",
            };
        });
        // 用显式标准流而不是 StdioServerTransport：
        // 单文件 WinExe（GUI 子系统）在重定向 stdin 时 Console.IsInputRedirected 可能为假，
        // 显式传流可保证 MCP 客户端（Claude Desktop 等）用管道拉起时能正常读写。
        builder.WithStreamServerTransport(Console.OpenStandardInput(), Console.OpenStandardOutput());
        builder.WithTools(new BakabotMcpTools("http://127.0.0.1:8726", token, logger));

        var server = services.BuildServiceProvider().GetRequiredService<McpServer>();
        await server.RunAsync();
        return 0;
    }

    private static string GetLogPath()
    {
        var env = Environment.GetEnvironmentVariable("MCP_LOG");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bakabot", "mcp_calls.log");
    }

    private static List<(IPAddress Addr, int Prefix)> ParseIpWhitelist(string? raw)
    {
        var list = new List<(IPAddress, int)>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = item.IndexOf('/');
            if (slash > 0 &&
                IPAddress.TryParse(item[..slash].Trim(), out var addr) &&
                int.TryParse(item[(slash + 1)..].Trim(), out var prefix))
            {
                list.Add((addr, prefix));
            }
            else if (IPAddress.TryParse(item, out var single))
            {
                list.Add((single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
        }
        return list;
    }

    private static bool IsIpAllowed(string ip, List<(IPAddress Addr, int Prefix)> whitelist)
    {
        if (whitelist.Count == 0) return true;
        if (!IPAddress.TryParse(ip, out var remote)) return false;
        foreach (var (addr, prefix) in whitelist)
        {
            if (addr.AddressFamily != remote.AddressFamily) continue;
            var a = addr.GetAddressBytes();
            var b = remote.GetAddressBytes();
            var fullBytes = prefix / 8;
            var remBits = prefix % 8;
            var ok = true;
            for (var i = 0; i < fullBytes; i++)
            {
                if (a[i] != b[i]) { ok = false; break; }
            }
            if (ok && remBits > 0)
            {
                var mask = (byte)(0xFF << (8 - remBits));
                if ((a[fullBytes] & mask) != (b[fullBytes] & mask)) ok = false;
            }
            if (ok) return true;
        }
        return false;
    }

    private static string ExtractApiKey(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var xKey = request.Headers["X-Api-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(xKey)) return xKey.Trim();
        var q = request.Query["key"].ToString();
        return q.Trim();
    }

    private static bool FixedTimeEquals(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>简单文件调用日志</summary>
public sealed class CallLogger
{
    private readonly string _path;
    private readonly object _lock = new();

    public CallLogger(string path)
    {
        _path = path;
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { }
    }

    public void Log(string source, string remote, string what, string detail)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] {remote} {what} {detail}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写失败不影响服务
        }
    }
}

/// <summary>MCP 工具：通过启动器本地接口调用机器人</summary>
[McpServerToolType]
public sealed class BakabotMcpTools
{
    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly CallLogger? _logger;

    public BakabotMcpTools(string apiBase, string token, CallLogger? logger = null)
    {
        _apiBase = apiBase;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>列出所有机器人实例及运行状态</summary>
    [McpServerTool, Description("列出所有机器人实例、运行状态与绑定的游戏主人")]
    public async Task<string> ListInstances()
    {
        _logger?.Log("tool", "mcp", "list_instances", "");
        try
        {
            var resp = await _http.GetAsync($"{_apiBase}/api/instances");
            var text = await resp.Content.ReadAsStringAsync();
            var result = resp.IsSuccessStatusCode
                ? text
                : $"错误({(int)resp.StatusCode}): {text}";
            _logger?.Log("tool", "mcp", "list_instances", "ok");
            return result;
        }
        catch (Exception ex)
        {
            var msg = $"错误: 无法连接启动器本地接口（{ex.Message}）。请确认 Bakabot 已启动且「MCP 本地接口」已开启。";
            _logger?.Log("tool", "mcp", "list_instances", "error " + ex.Message);
            return msg;
        }
    }

    /// <summary>向指定机器人实例发送自然语言指令，并返回机器人的回复</summary>
    [McpServerTool, Description("向指定机器人实例发送一条自然语言指令（如“去挖 3 个铁矿”“传送过来”“看看周围”），等待机器人执行并返回回复")]
    public async Task<string> SendInstruction(
        [Description("实例名称，先调用 list_instances 获取")] string instance,
        [Description("要机器人执行的自然语言指令")] string instruction)
    {
        if (string.IsNullOrWhiteSpace(instance) || string.IsNullOrWhiteSpace(instruction))
            return "错误: 需要提供 instance 和 instruction。";

        _logger?.Log("tool", "mcp", $"send_instruction {instance}", $"指令: {instruction}");
        try
        {
            var url = $"{_apiBase}/api/instances/{Uri.EscapeDataString(instance)}/command";
            var resp = await _http.PostAsJsonAsync(url, new { text = instruction });
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger?.Log("tool", "mcp", $"send_instruction {instance}", $"error {resp.StatusCode} {json}");
                return $"错误({(int)resp.StatusCode}): {json}";
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("reply", out var reply))
            {
                var text = reply.GetString() ?? string.Empty;
                _logger?.Log("tool", "mcp", $"send_instruction {instance}", "ok");
                return text;
            }
            return json;
        }
        catch (Exception ex)
        {
            _logger?.Log("tool", "mcp", $"send_instruction {instance}", "error " + ex.Message);
            return $"错误: 调用机器人失败（{ex.Message}）。请确认实例正在运行。";
        }
    }
}
