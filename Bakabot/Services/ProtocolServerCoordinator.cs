namespace Bakabot.Services;

/// <summary>
/// 协议端互斥协调：NapCat 与外部 OneBot11 反向接入不能同时运行，
/// 后启动的一方会先顶掉先启动的一方（在服务启动层面强制检查）。
/// </summary>
public static class ProtocolServerCoordinator
{
    /// <summary>当前注册的外部 OneBot11 反向接入服务（由 OneBot11ServerService 构造时挂载）</summary>
    public static OneBot11ServerService? OneBotServer { get; set; }
}
