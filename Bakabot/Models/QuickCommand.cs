namespace Bakabot.Models;

/// <summary>命令提示词：整句包含关键词时直接执行对应命令（不走 LLM）</summary>
public class QuickCommand
{
    public string Keyword { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
}
