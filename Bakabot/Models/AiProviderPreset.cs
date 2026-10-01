using System.Collections.Generic;

namespace Bakabot.Models;

/// <summary>
/// AI 服务商预设（来自内嵌资源 Resources/ai-providers.json）。
/// 供「创建/编辑实例」的 AI 配置界面下拉选择，选中后自动填充 API URL 与模型名。
/// </summary>
public class AiProviderPreset
{
    /// <summary>稳定标识（用于下拉选中项判断）</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分类：cn=国内官方 / intl=国际官方 / hub=聚合中转 / local=本地部署</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>OpenAI 兼容基址（Bakabot 会拼 /chat/completions）</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>常用模型名，第一个作为默认选中</summary>
    public List<string> Models { get; set; } = new();

    /// <summary>注册/申请 Key 的地址（可为空）</summary>
    public string RegisterUrl { get; set; } = string.Empty;

    /// <summary>备注说明（显示在界面上的小字提示）</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>ai-providers.json 的根对象</summary>
public class AiProviderPresetFile
{
    public List<AiProviderPreset> Providers { get; set; } = new();
}
