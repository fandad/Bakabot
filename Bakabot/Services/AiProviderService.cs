using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bakabot.Models;

namespace Bakabot.Services;

/// <summary>
/// AI 服务商预设服务：
///   1. 从内嵌资源读取预设表（Resources/ai-providers.json）；
///   2. 按 OpenAI 兼容的 /v1/models 端点自动拉取服务商可用模型，
///      免去用户手抄模型 ID。
/// </summary>
public class AiProviderService
{
    private const string ResourceName = "Bakabot.Resources.ai-providers.json";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private List<AiProviderPreset>? _cache;

    public AiProviderService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Bakabot/1.0");
    }

    /// <summary>读取全部预设（带缓存；资源缺失或解析失败时返回空列表，不抛异常打断界面）</summary>
    public List<AiProviderPreset> GetPresets()
    {
        if (_cache != null) return _cache;

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                _cache = new List<AiProviderPreset>();
                return _cache;
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var file = JsonSerializer.Deserialize<AiProviderPresetFile>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _cache = file?.Providers ?? new List<AiProviderPreset>();
        }
        catch
        {
            _cache = new List<AiProviderPreset>();
        }

        return _cache;
    }

    /// <summary>
    /// 从服务商的 OpenAI 兼容端点拉取可用模型 ID 列表。
    /// 请求 GET {apiUrl}/models（自动去掉末尾斜杠与已带的 /chat/completions）。
    /// </summary>
    /// <param name="apiUrl">用户填写的 API URL（基址）</param>
    /// <param name="apiKey">API Key（本地模型可留空）</param>
    public async Task<List<string>> FetchModelsAsync(string apiUrl, string? apiKey, CancellationToken ct = default)
    {
        var baseUrl = NormalizeBaseUrl(apiUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("请先填写 API URL。");

        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"服务商返回 {(int)response.StatusCode}。可能原因：地址不对、Key 无效，或该服务商不支持 /models 端点（那就手动填模型名）。");
        }

        var models = ParseModelIds(body);
        if (models.Count == 0)
            throw new InvalidOperationException("服务商返回内容里没解析到模型列表，请手动填写模型名。");

        return OrderModels(models);
    }

    /// <summary>把用户填的 API URL 归一化成基址（能拼 /models）</summary>
    private static string NormalizeBaseUrl(string? apiUrl)
    {
        var url = (apiUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length == 0) return string.Empty;

        // 用户可能填了完整端点，剥掉已知的补全路径
        foreach (var suffix in new[] { "/chat/completions", "/completions", "/responses", "/messages" })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        return url;
    }

    /// <summary>解析 OpenAI 风格 { "data": [ { "id": ... } ] }，兼容 Gemini 的 { "models": [ { "name": ... } ] }</summary>
    private static List<string> ParseModelIds(string body)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            JsonElement array;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                array = data;
            else if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                array = models;
            else
                return result;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s!);
                    continue;
                }

                // OpenAI: id / Gemini: name（形如 "models/gemini-2.0-flash"）
                string? id = null;
                if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    id = idEl.GetString();
                else if (item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    id = nameEl.GetString();

                if (string.IsNullOrWhiteSpace(id)) continue;
                id = id!.Trim();
                if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                    id = id["models/".Length..];
                result.Add(id);
            }
        }
        catch { /* 非 JSON 或结构不符，返回已解析到的内容 */ }

        return result;
    }

    /// <summary>
    /// 排序：把适合聊天的模型排前面（纯 embedding/语音/图像模型靠后），其余按字母序。
    /// 服务商动辄返回上百个模型，不排序用户根本找不到。
    /// </summary>
    private static List<string> OrderModels(List<string> models)
    {
        static int Rank(string id)
        {
            var s = id.ToLowerInvariant();
            // 0 = 通用对话/推理（最可能要用）
            if (s.Contains("embed") || s.Contains("rerank") || s.Contains("bge-") || s.Contains("m3e"))
                return 3;
            if (s.Contains("whisper") || s.Contains("tts") || s.Contains("speech") || s.Contains("audio"))
                return 3;
            if (s.Contains("dall-e") || s.Contains("stable-diffusion") || s.Contains("flux") || s.Contains("image"))
                return 3;
            if (s.Contains("moderation") || s.Contains("guard"))
                return 3;
            if (s.Contains("vl") || s.Contains("vision") || s.Contains("omni"))
                return 2;   // 视觉模型：能用但不是首选
            return 0;
        }

        return models
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Rank)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
