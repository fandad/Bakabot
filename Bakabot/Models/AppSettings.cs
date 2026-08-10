namespace Bakabot.Models;

public class AppSettings
{
    public bool IsDarkMode { get; set; } = true;
    public double BgOpacity { get; set; } = 0.6;
    public string BackgroundImagePath { get; set; } = string.Empty;
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
}
