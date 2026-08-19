using System.Text.Json.Serialization;

namespace Bakabot.Models;

/// <summary>
/// QQ 白名单条目（全局一份）：QQ 号 → 可空游戏玩家名 / 可空绑定实例 / 启用开关。
/// 不在白名单或开关关闭的消息在启动器层面直接丢弃，不进入机器人。
/// </summary>
public class QQWhitelistEntry
{
    public string QQ { get; set; } = string.Empty;

    /// <summary>绑定的游戏玩家名（可空：该 QQ 用户未绑定游戏账号）</summary>
    public string? PlayerName { get; set; }

    /// <summary>绑定的实例名（可空 = 转发给所有运行中的实例）</summary>
    public string? InstanceName { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>启动器 → 机器人 的 QQ 指令消息（stdin 行 "QQCMD {json}"）</summary>
public class QQCmdMessage
{
    [JsonPropertyName("qq")]
    public string QQ { get; set; } = string.Empty;

    [JsonPropertyName("groupId")]
    public string? GroupId { get; set; }

    [JsonPropertyName("player")]
    public string? Player { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>机器人 → 启动器 的 QQ 输出消息（stdout 行 "[QQ-OUT] {json}"）</summary>
public class QQOutMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "msg";

    [JsonPropertyName("qq")]
    public string? QQ { get; set; }

    [JsonPropertyName("groupId")]
    public string? GroupId { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
