using Bakabot.Models;
using Bakabot.Helpers;
using Bakabot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Bakabot.ViewModels;

/// <summary>
/// 创建/编辑实例表单的 ViewModel。
/// 支持新建和编辑两种模式。
/// </summary>
public partial class CreateInstanceViewModel : ObservableObject
{
    private readonly InstanceManager _instanceManager;
    private readonly AiProviderService _aiProviderService;
    private readonly SettingsService _settingsService;

    /// <summary>
    /// 标记位：正在由"选择服务商"自动填充字段。
    /// 自动填充期间不要清空已填的 API Key、也不要把选择重置成"自定义"。
    /// </summary>
    private bool _applyingPreset;

    /// <summary>是否为编辑模式</summary>
    [ObservableProperty]
    private bool _isEditMode = false;

    /// <summary>原始实例名（编辑模式下不可更改）</summary>
    [ObservableProperty]
    private string _originalInstanceName = string.Empty;

    // ─── 表单字段 ───

    [ObservableProperty]
    private string _instanceName = string.Empty;

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

    [ObservableProperty]
    private string _visionModel = string.Empty;

    [ObservableProperty]
    private string _visionApiUrl = string.Empty;

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

    [ObservableProperty]
    private string _triggerMode = "hybrid";

    [ObservableProperty]
    private bool _autoDefendEnabled = false;

    [ObservableProperty]
    private bool _autoAcceptTpa = false;

    [ObservableProperty]
    private bool _tpaOwnerOnly = true;

    [ObservableProperty]
    private string _tpaAcceptTrigger = string.Empty;

    [ObservableProperty]
    private bool _autoLogin = false;

    [ObservableProperty]
    private bool _deathBack = false;

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

    [ObservableProperty]
    private bool _suppressActionChat = false;

    [ObservableProperty]
    private bool _forbidMining = false;

    /// <summary>狂暴清障最小间隔（秒），0=不限制</summary>
    [ObservableProperty]
    private int _berserkCooldownSeconds = 15;

    /// <summary>进服问候语：进服时是否发送“主人好，底层框架已启动，等待指令！”</summary>
    [ObservableProperty]
    private bool _startupGreeting = true;

    [ObservableProperty]
    private string _aiStylePrompt = "你说话要像一个活泼可爱的猫娘，结尾喜欢带上\"喵~\"。";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError = false;

    // ─── 下拉框选项 ───

    public ObservableCollection<string> AuthTypes { get; } = new() { "microsoft", "yggdrasil", "offline" };
    public ObservableCollection<string> TellModes { get; } = new() { "whisper", "msg", "public" };
    public ObservableCollection<string> TriggerModes { get; } = new() { "hybrid", "owner_only", "keyword_only", "owner_keyword" };
    public ObservableCollection<string> Versions { get; } = new(VersionMapper.CommonVersions);

    /// <summary>保存成功事件（通知 View 关闭对话框）</summary>
    public event Action? SaveCompleted;

    public CreateInstanceViewModel(InstanceManager instanceManager, AiProviderService aiProviderService,
        SettingsService settingsService)
    {
        _instanceManager = instanceManager;
        _aiProviderService = aiProviderService;
        _settingsService = settingsService;

        foreach (var p in _aiProviderService.GetPresets()) AiProviders.Add(p);

        // 默认选中"上次用过的服务商"，没有记录才回退到默认厂商。
        // 注意：这里直接写字段（而不是走属性 setter），避免触发自动填充把 URL/模型覆盖成预设值。
        var lastId = settingsService.Settings.LastAiProviderId;
        _selectedAiProvider = AiProviders.FirstOrDefault(p => p.Id == lastId)
                              ?? AiProviders.FirstOrDefault(p => p.Id == "volcengine")
                              ?? AiProviders.FirstOrDefault();
    }

    /// <summary>记住用户选过的服务商，供下次新建实例时默认选中</summary>
    private void RememberSelectedProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        if (_settingsService.Settings.LastAiProviderId == providerId) return;
        _settingsService.UpdateSettings(s => s.LastAiProviderId = providerId);
    }

    /// <summary>
    /// 按实例已保存的 API URL 反查对应的服务商预设。
    /// 用于编辑实例时让下拉框显示正确的厂商，而不是永远显示默认那个。
    /// 找不到匹配（自建中转、自定义地址）就返回"自定义"。
    /// </summary>
    private AiProviderPreset? MatchProviderByUrl(string? apiUrl)
    {
        var custom = AiProviders.FirstOrDefault(p => p.Id == "custom");

        var target = NormalizeForMatch(apiUrl);
        if (target.Length == 0) return custom;

        var exact = AiProviders.FirstOrDefault(p => NormalizeForMatch(p.BaseUrl) == target);
        if (exact != null) return exact;

        // 宽松匹配：去掉协议和结尾的 /v1、/v3 之类版本段再比一次，
        // 覆盖用户手填时多写/少写 /v1 的情况
        var loose = LooseKey(target);
        var looseHit = AiProviders.FirstOrDefault(p =>
        {
            var k = LooseKey(NormalizeForMatch(p.BaseUrl));
            return k.Length > 0 && k == loose;
        });
        return looseHit ?? custom;
    }

    /// <summary>URL 归一化：去空白、去末尾斜杠、小写</summary>
    private static string NormalizeForMatch(string? url)
        => (url ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>宽松键：去掉协议前缀与结尾的 /v数字 版本段</summary>
    private static string LooseKey(string url)
    {
        var s = url;
        foreach (var prefix in new[] { "https://", "http://" })
            if (s.StartsWith(prefix, StringComparison.Ordinal)) { s = s[prefix.Length..]; break; }

        s = System.Text.RegularExpressions.Regex.Replace(s, @"/v\d+$", "");
        return s.TrimEnd('/');
    }

    // ─── AI 服务商预设 ───

    /// <summary>全部服务商预设（下拉数据源）</summary>
    public ObservableCollection<AiProviderPreset> AiProviders { get; } = new();

    /// <summary>「拉取模型」得到的模型列表（下拉数据源）</summary>
    public ObservableCollection<string> FetchedModels { get; } = new();

    /// <summary>当前选中的服务商（绑定下拉框）</summary>
    /// <remarks>
    /// 刻意不用 [ObservableProperty]：本类有两处需要"只改选中项、不触发自动填充"的场景
    /// （编辑实例时按已存 URL 反查厂商、新建时回填上次选择），直接写字段是必须的。
    /// 写法上用私有字段 + 手写属性，setter 里再触发自动填充。
    /// </remarks>
    private AiProviderPreset? _selectedAiProvider;

    public AiProviderPreset? SelectedAiProvider
    {
        get => _selectedAiProvider;
        set
        {
            if (ReferenceEquals(_selectedAiProvider, value)) return;
            _selectedAiProvider = value;
            OnPropertyChanged();
            ApplyProviderPreset(value);
        }
    }

    /// <summary>是否正在拉取模型列表</summary>
    [ObservableProperty]
    private bool _isFetchingModels;

    /// <summary>拉取结果提示</summary>
    [ObservableProperty]
    private string _modelFetchStatus = string.Empty;

    /// <summary>
    /// 选择服务商后自动填充 API URL 与模型名（**不动 API Key**，避免手滑清空已填内容）。
    /// 由 <see cref="SelectedAiProvider"/> 的 setter 调用。
    /// </summary>
    private void ApplyProviderPreset(AiProviderPreset? value)
    {
        if (value == null) return;

        ModelFetchStatus = string.Empty;
        FetchedModels.Clear();

        // 记住选择，下次新建实例默认选中它
        if (!_applyingPreset) RememberSelectedProvider(value.Id);

        if (value.Id == "custom") return;   // 自定义：什么都不动

        _applyingPreset = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(value.BaseUrl)) LlmApiUrl = value.BaseUrl;
            if (value.Models.Count > 0) LlmModel = value.Models[0];
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    /// <summary>拉取该服务商的可用模型列表（调 OpenAI 兼容的 /models 端点）</summary>
    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (IsFetchingModels) return;

        IsFetchingModels = true;
        ModelFetchStatus = "正在拉取模型列表...";
        try
        {
            var models = await _aiProviderService.FetchModelsAsync(LlmApiUrl, LlmApiKey);
            FetchedModels.Clear();
            foreach (var m in models) FetchedModels.Add(m);
            ModelFetchStatus = $"共 {models.Count} 个模型，可在下方下拉里选（已把对话模型排在前面）";
        }
        catch (Exception ex)
        {
            FetchedModels.Clear();
            ModelFetchStatus = $"拉取失败：{ex.Message}";
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    /// <summary>打开该服务商的注册/申请 Key 页面</summary>
    [RelayCommand]
    private void OpenProviderSite()
    {
        var url = SelectedAiProvider?.RegisterUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { /* 打不开就算了，不影响填写 */ }
    }

    /// <summary>用户手动改动 API URL 时，把"自定义"标记和拉取结果复位</summary>
    partial void OnLlmApiUrlChanged(string value)
    {
        if (_applyingPreset) return;
        FetchedModels.Clear();
        ModelFetchStatus = string.Empty;
    }

    /// <summary>
    /// 以编辑模式加载现有实例数据。
    /// </summary>
    public void LoadForEdit(BotInstance instance)
    {
        IsEditMode = true;
        OriginalInstanceName = instance.InstanceName;

        InstanceName = instance.InstanceName;
        McHost = instance.McHost;
        McPort = instance.McPort;
        McVersion = instance.McVersion;
        McUsername = instance.McUsername;
        LlmApiKey = instance.LlmApiKey;

        // 回填已保存的 AI 配置：期间屏蔽预设自动填充与"改动 URL 就清空拉取结果"的逻辑，
        // 否则会把实例里真实的 URL/模型覆盖成默认厂商的值。
        _applyingPreset = true;
        try
        {
            LlmApiUrl = instance.LlmApiUrl;
            LlmModel = instance.LlmModel;
            VisionModel = instance.VisionModel;
            VisionApiUrl = instance.VisionApiUrl;
            VisionApiKey = instance.VisionApiKey;
        }
        finally
        {
            _applyingPreset = false;
        }

        // 按已保存的 URL 反查厂商，让下拉框显示真实对应的服务商
        // （直接写字段，不触发 setter，避免反手把 URL 覆盖成预设值）
        _selectedAiProvider = MatchProviderByUrl(instance.LlmApiUrl);
        OnPropertyChanged(nameof(SelectedAiProvider));

        McOwnerName = instance.McOwnerName;
        McLoginPassword = instance.McLoginPassword;
        McAuthType = instance.McAuthType;
        AuthServerUrl = instance.AuthServerUrl;
        RegisterUrl = instance.RegisterUrl;
        TellMode = instance.TellMode;
        ChatTrigger = instance.ChatTrigger;
        TriggerMode = string.IsNullOrWhiteSpace(instance.TriggerMode) ? "hybrid" : instance.TriggerMode;
        AutoDefendEnabled = instance.AutoDefendEnabled;
        AutoAcceptTpa = instance.AutoAcceptTpa;
        TpaOwnerOnly = instance.TpaOwnerOnly;
        TpaAcceptTrigger = instance.TpaAcceptTrigger;
        AutoLogin = instance.AutoLogin;
        DeathBack = instance.DeathBack;
        InstinctAutoEat = instance.InstinctAutoEat;
        InstinctAutoTool = instance.InstinctAutoTool;
        InstinctAutoDump = instance.InstinctAutoDump;
        DebugMode = instance.DebugMode;
        AutoAcceptResourcePack = instance.AutoAcceptResourcePack;
        SuppressActionChat = instance.SuppressActionChat;
        ForbidMining = instance.ForbidMining;
        BerserkCooldownSeconds = instance.BerserkCooldownSeconds;
        StartupGreeting = instance.StartupGreeting;
        AiStylePrompt = instance.AiStylePrompt;
    }

    /// <summary>重置为新建模式</summary>
    public void ResetForCreate()
    {
        IsEditMode = false;
        OriginalInstanceName = string.Empty;
        InstanceName = string.Empty;
        McHost = "myarc.fun";
        McPort = "25565";
        McVersion = "1.20.1";
        McUsername = string.Empty;
        LlmApiKey = string.Empty;

        // 新建时按"上次用过的服务商"填默认 URL 与模型（无记录则回退到默认厂商）。
        // 直接写字段，避免 setter 之间的相互影响。
        var remembered = AiProviders.FirstOrDefault(p => p.Id == _settingsService.Settings.LastAiProviderId)
                         ?? AiProviders.FirstOrDefault(p => p.Id == "volcengine")
                         ?? AiProviders.FirstOrDefault();
        _selectedAiProvider = remembered;
        LlmApiUrl = remembered?.BaseUrl ?? string.Empty;
        LlmModel = remembered?.Models.Count > 0 ? remembered.Models[0] : string.Empty;
        OnPropertyChanged(nameof(SelectedAiProvider));

        VisionModel = string.Empty;
        VisionApiUrl = string.Empty;
        VisionApiKey = string.Empty;
        McOwnerName = "_FENTAI_";
        McLoginPassword = string.Empty;
        McAuthType = "microsoft";
        AuthServerUrl = "https://littleskin.cn/api/yggdrasil";
        RegisterUrl = "https://littleskin.cn/auth/register";
        TellMode = "whisper";
        ChatTrigger = string.Empty;
        TriggerMode = "hybrid";
        AutoDefendEnabled = false;
        AutoAcceptTpa = false;
        TpaOwnerOnly = true;
        TpaAcceptTrigger = string.Empty;
        AutoLogin = false;
        DeathBack = false;
        InstinctAutoEat = false;
        InstinctAutoTool = false;
        InstinctAutoDump = false;
        DebugMode = false;
        AutoAcceptResourcePack = true;
        SuppressActionChat = false;
        ForbidMining = false;
        BerserkCooldownSeconds = 15;
        StartupGreeting = true;
        AiStylePrompt = "你说话要像一个活泼可爱的猫娘，结尾喜欢带上\"喵~\"。";
        ErrorMessage = string.Empty;
        HasError = false;
    }

    /// <summary>验证并保存</summary>
    [RelayCommand]
    private void Save()
    {
        // ─── 表单验证 ───
        if (string.IsNullOrWhiteSpace(InstanceName))
        {
            SetError("实例名称不能为空。");
            return;
        }

        if (InstanceName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            SetError("实例名称包含非法字符。");
            return;
        }

        if (string.IsNullOrWhiteSpace(McUsername))
        {
            SetError("Minecraft 用户名不能为空。");
            return;
        }

        // ─── 模型 ID 格式校验：仅小写字母/数字/连字符/点，不含斜杠、空格 ───
        if (!IsValidModelId(LlmModel))
        {
            ShowModelIdError("模型名称 (LLM_MODEL)", LlmModel);
            return;
        }
        if (!string.IsNullOrWhiteSpace(VisionModel) && !IsValidModelId(VisionModel))
        {
            ShowModelIdError("视觉看图模型 (VISION_MODEL)", VisionModel);
            return;
        }

        // 构建模型
        var instance = new BotInstance
        {
            InstanceName = InstanceName.Trim(),
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
            AuthServerUrl = AuthServerUrl,
            RegisterUrl = RegisterUrl,
            McLoginPassword = McLoginPassword,
            McAuthType = McAuthType,
            TellMode = TellMode,
            ChatTrigger = ChatTrigger,
            TriggerMode = TriggerMode,
            AutoDefendEnabled = AutoDefendEnabled,
            AutoAcceptTpa = AutoAcceptTpa,
            TpaOwnerOnly = TpaOwnerOnly,
            TpaAcceptTrigger = TpaAcceptTrigger,
            AutoLogin = AutoLogin,
            DeathBack = DeathBack,
            InstinctAutoEat = InstinctAutoEat,
            InstinctAutoTool = InstinctAutoTool,
            InstinctAutoDump = InstinctAutoDump,
            AutoAcceptResourcePack = AutoAcceptResourcePack,
            SuppressActionChat = SuppressActionChat,
            ForbidMining = ForbidMining,
            BerserkCooldownSeconds = BerserkCooldownSeconds,
            StartupGreeting = StartupGreeting,
            DebugMode = DebugMode,
            AiStylePrompt = AiStylePrompt
        };

        try
        {
            if (IsEditMode)
            {
                _instanceManager.UpdateInstance(instance);
            }
            else
            {
                _instanceManager.CreateInstance(instance);
            }

            HasError = false;
            ErrorMessage = string.Empty;
            SaveCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void SetError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }

    /// <summary>校验模型 ID 格式：字母/数字/连字符/点/下划线/斜杠/冒号/@，不含空格</summary>
    private static bool IsValidModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        foreach (var ch in modelId)
        {
            if (char.IsAsciiLetter(ch) || char.IsAsciiDigit(ch) ||
                ch == '-' || ch == '.' || ch == '_' || ch == '/' || ch == ':' || ch == '@')
                continue;
            return false;
        }
        return true;
    }

    /// <summary>模型 ID 非法时弹窗提示（错误信息较长，红字会被截断，弹窗更清晰）</summary>
    private static void ShowModelIdError(string fieldName, string modelId)
    {
        var message =
            $"{fieldName} 格式不正确：\n\n" +
            $"你填写的是：{modelId}\n\n" +
            "模型 ID 支持大小写字母、数字、连字符(-)、点(.)、下划线(_)、\n" +
            "斜杠(/)（如 minimaxai/minimax-m3）、冒号(:)和 @，不能包含空格。\n\n" +
            "示例：deepseek-v4-flash-ga-260731 / minimaxai/minimax-m3";
        System.Windows.MessageBox.Show(
            message,
            "模型 ID 格式错误",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }
}
