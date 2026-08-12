using CommunityToolkit.Mvvm.ComponentModel;

namespace Bakabot.Models;

/// <summary>������ʵ������״̬</summary>
public enum BotStatus
{
    Stopped,
    Running,
    Starting,
    Error
}

/// <summary>
/// ����һ�� Minecraft AI ������ʵ������������ģ�͡�
/// ʹ�� ObservableObject ��֧�� UI ʵʱ�󶨡�
/// </summary>
public partial class BotInstance : ObservableObject
{
    // ������ ������Ϣ ������
    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private BotStatus _status = BotStatus.Stopped;

    // ������ .env ӳ���ֶ� ������
    [ObservableProperty]
    private string _mcHost = "myarc.fun";

    [ObservableProperty]
    private string _mcPort = "25565";

    [ObservableProperty]
    private string _mcVersion = "1.20.1";

    [ObservableProperty]
    private string _mcUsername = string.Empty;

    [ObservableProperty]
    private string _llmApiKey = string.Empty;

    [ObservableProperty]
    private string _llmApiUrl = "https://ark.cn-beijing.volces.com/api/v3";

    [ObservableProperty]
    private string _llmModel = "doubao-seed-2-0-lite-260215";

    /// <summary>视觉看图模型（留空则禁用截图看图功能）</summary>
    [ObservableProperty]
    private string _visionModel = string.Empty;

    /// <summary>视觉模型 API 地址（留空复用 LLM_API_URL）</summary>
    [ObservableProperty]
    private string _visionApiUrl = string.Empty;

    /// <summary>视觉模型 API Key（留空复用 LLM_API_KEY）</summary>
    [ObservableProperty]
    private string _visionApiKey = string.Empty;

    [ObservableProperty]
    private string _mcOwnerName = "_FENTAI_";

    [ObservableProperty]
    private string _mcLoginPassword = string.Empty;

    [ObservableProperty]
    private string _mcAuthType = "microsoft";
    [ObservableProperty]
    private string _authServerUrl = "https://littleskin.cn/api/yggdrasil";

    [ObservableProperty]
    private string _registerUrl = "https://littleskin.cn/auth/register";

    [ObservableProperty]
    private string _tellMode = "whisper";
    [ObservableProperty]
    private string _chatTrigger = string.Empty;

    /// <summary>聊天触发模式：owner_only / keyword_only / hybrid</summary>
    [ObservableProperty]
    private string _triggerMode = "hybrid";

    [ObservableProperty]
    private bool _autoDefendEnabled = false;

    [ObservableProperty]
    private bool _instinctAutoTpLogin = false;

    [ObservableProperty]
    private bool _instinctAutoEat = false;

    [ObservableProperty]
    private bool _instinctAutoTool = false;

    [ObservableProperty]
    private bool _instinctAutoDump = false;

    [ObservableProperty]
    private bool _debugMode = false;

    [ObservableProperty]
    private bool _autoAcceptResourcePack = true;

    /// <summary>抑制基础包行动开始/完成时的强制公屏播报</summary>
    [ObservableProperty]
    private bool _suppressActionChat = false;

    /// <summary>禁止挖掘：开启后机器人禁用一切挖掘功能</summary>
    [ObservableProperty]
    private bool _forbidMining = false;

    [ObservableProperty]
    private string _aiStylePrompt = "��˵��Ҫ��һ�����ÿɰ���è���βϲ������\"��~\"��";

    /// <summary>����һ����������ڱ༭ʱ��Ӱ��ԭʼ���ݣ�</summary>
    public BotInstance Clone()
    {
        return new BotInstance
        {
            InstanceName = InstanceName,
            Status = Status,
            McHost = McHost,
            McPort = McPort,
            McVersion = McVersion,
            McUsername = McUsername,
            LlmApiKey = LlmApiKey,
            LlmApiUrl = LlmApiUrl,
            LlmModel = LlmModel,
            VisionModel = VisionModel,
            VisionApiUrl = VisionApiUrl,
            VisionApiKey = VisionApiKey,
            McOwnerName = McOwnerName,
            McLoginPassword = McLoginPassword,
            McAuthType = McAuthType,
            AuthServerUrl = AuthServerUrl,
            RegisterUrl = RegisterUrl,
            TellMode = TellMode,
            ChatTrigger = ChatTrigger,
            TriggerMode = TriggerMode,
            AutoDefendEnabled = AutoDefendEnabled,
            InstinctAutoTpLogin = InstinctAutoTpLogin,
            InstinctAutoEat = InstinctAutoEat,
            InstinctAutoTool = InstinctAutoTool,
            InstinctAutoDump = InstinctAutoDump,
            DebugMode = DebugMode,
            AutoAcceptResourcePack = AutoAcceptResourcePack,
            SuppressActionChat = SuppressActionChat,
            ForbidMining = ForbidMining,
            AiStylePrompt = AiStylePrompt
        };
    }
}