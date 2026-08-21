namespace Bakabot.Models;

public class AppSettings
{
    public bool IsDarkMode { get; set; } = true;
    public double BgOpacity { get; set; } = 0.6;
    public string BackgroundImagePath { get; set; } = string.Empty;
    /// <summary>是否已应用过内置默认背景（首次启动应用一次，之后交给外观设置，恢复无背景仍生效）</summary>
    public bool BundledBackgroundApplied { get; set; } = false;
    /// <summary>按钮/主题强调色（十六进制，空字符串=跟随系统默认蓝色）</summary>
    public string AccentColor { get; set; } = string.Empty;

    // --- 基础包设置 ---
    public bool UseCustomBaseAgent { get; set; } = false;
    public string CustomBaseAgentPath { get; set; } = string.Empty;

    // --- 弹窗设置 ---
    public bool DisableAfdianPopup { get; set; } = false;

    // --- 更新设置 ---
    public string SkippedVersion { get; set; } = string.Empty;

    // --- ViaProxy 协议代理设置（用于原生支持 26.x 等新年份命名版本）---
    /// <summary>是否启用 ViaProxy 协议代理功能</summary>
    public bool EnableViaProxy { get; set; } = true;
    /// <summary>自定义 java.exe 路径（留空则自动检测）</summary>
    public string JavaPath { get; set; } = string.Empty;

    // --- QQ 桥接设置 ---
    /// <summary>是否启用 QQ 桥接功能</summary>
    public bool QQEnabled { get; set; } = false;
    /// <summary>机器人自己的 QQ 号（NapCat 快速登录与 OneBot 配置文件命名）</summary>
    public string QQBotNumber { get; set; } = string.Empty;
    /// <summary>是否允许所有 QQ 用户使用（开启后绕过白名单拦截，未绑定玩家按未绑定处理）</summary>
    public bool QQAllowAll { get; set; } = false;
    /// <summary>生效的 QQ 群 ID，逗号分隔；留空 = 所有群</summary>
    public string QQGroupIds { get; set; } = string.Empty;
    /// <summary>群内触发关键词，逗号分隔；留空 = 仅 @ 机器人触发</summary>
    public string QQTriggerKeywords { get; set; } = string.Empty;

    // --- OneBot11 反向 WS 接入设置（llbot / Lagrange 等外部协议端）---
    /// <summary>是否启用外部 OneBot11 反向 WebSocket 接入（与 NapCat 互斥）</summary>
    public bool OneBotEnabled { get; set; } = false;
    /// <summary>反向 WebSocket 监听端口（固定默认 6700）</summary>
    public int OneBotPort { get; set; } = 6700;
    /// <summary>可选鉴权 Token（留空 = 不校验；客户端需通过 Authorization Bearer 或 access_token 携带）</summary>
    public string OneBotToken { get; set; } = string.Empty;

    // --- 命令提示词设置（全局）---
    /// <summary>是否启用命令提示词（纯关键词模式）</summary>
    public bool QQQuickCommandsEnabled { get; set; } = false;
    /// <summary>生效范围："ALL" = 全部实例，否则为指定实例名</summary>
    public string QQQuickCommandsScope { get; set; } = "ALL";
    /// <summary>屏蔽关键词命令（游戏内触发）：命中即吞掉消息，不执行也不思考</summary>
    public bool QQQuickBlockGame { get; set; } = false;
    /// <summary>屏蔽关键词命令（QQ 触发）：命中即吞掉消息，不执行也不思考</summary>
    public bool QQQuickBlockQq { get; set; } = false;
    /// <summary>屏蔽关键词行动公屏播报：游戏内触发时不发“已执行”回执，QQ 回执照常</summary>
    public bool QQQuickSuppressGameReply { get; set; } = false;
    /// <summary>关键词 -> 命令 映射表</summary>
    public List<QuickCommand> QQQuickCommands { get; set; } = new();
}
