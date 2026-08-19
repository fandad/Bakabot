using System.IO;
using System.Text;
using Bakabot.Helpers;
using Bakabot.Models;

namespace Bakabot.Services;

/// <summary>
/// ���� BotInstance ģ���� .env �ļ�֮�����˫��ת����
/// ֧�ֶ�ȡ��д�롢�Լ�����ע���С�
/// </summary>
public class EnvManager
{
    /// <summary>
    /// �� BotInstance ������д�뵽��Ӧʵ��Ŀ¼�� .env �ļ���
    /// </summary> 
    public void WriteEnv(BotInstance instance)
    {
        var envPath = PathHelper.GetInstanceEnvPath(instance.InstanceName);
        var dir = Path.GetDirectoryName(envPath)!;
        Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("# �T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T");
        sb.AppendLine($"# Bakabot Instance Config: {instance.InstanceName}");
        sb.AppendLine($"# Generated at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("# �T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T�T");
        sb.AppendLine();

        sb.AppendLine("#  Minecraft Server ");
        sb.AppendLine($"MC_HOST={instance.McHost}");
        sb.AppendLine($"MC_PORT={instance.McPort}");
        sb.AppendLine($"MC_VERSION={instance.McVersion}");
        sb.AppendLine($"MC_USERNAME={instance.McUsername}");
        sb.AppendLine($"MC_AUTH_TYPE={instance.McAuthType}");
        sb.AppendLine($"MC_LOGIN_PASSWORD={instance.McLoginPassword}");
        sb.AppendLine($"AUTH_SERVER_URL={instance.AuthServerUrl}");
        sb.AppendLine($"REGISTER_URL={instance.RegisterUrl}");
        sb.AppendLine($"MC_OWNER_NAME={instance.McOwnerName}");
        sb.AppendLine();

        sb.AppendLine("#  AI Configuration ");
        sb.AppendLine($"LLM_API_KEY={instance.LlmApiKey}");
        sb.AppendLine($"LLM_API_URL={instance.LlmApiUrl}");
        sb.AppendLine($"LLM_MODEL={instance.LlmModel}");
        sb.AppendLine($"VISION_MODEL={instance.VisionModel}");
        sb.AppendLine($"VISION_API_URL={instance.VisionApiUrl}");
        sb.AppendLine($"VISION_API_KEY={instance.VisionApiKey}");
        sb.AppendLine($"AI_STYLE_PROMPT={instance.AiStylePrompt}");
        sb.AppendLine();

        sb.AppendLine("# ������ Behavior ������");
        sb.AppendLine($"TELL_MODE={instance.TellMode}");
        sb.AppendLine($"CHAT_TRIGGER={instance.ChatTrigger}");
        sb.AppendLine($"TRIGGER_MODE={instance.TriggerMode}");
        sb.AppendLine($"AUTO_DEFEND_ENABLED={BoolToEnv(instance.AutoDefendEnabled)}");
        sb.AppendLine($"INSTINCT_TP_ACCEPT={BoolToEnv(instance.AutoAcceptTpa)}");
        sb.AppendLine($"TPA_OWNER_ONLY={BoolToEnv(instance.TpaOwnerOnly)}");
        sb.AppendLine($"TPA_ACCEPT_TRIGGER={instance.TpaAcceptTrigger}");
        sb.AppendLine($"INSTINCT_AUTO_LOGIN={BoolToEnv(instance.AutoLogin)}");
        sb.AppendLine($"INSTINCT_DEATH_BACK={BoolToEnv(instance.DeathBack)}");
        sb.AppendLine($"INSTINCT_AUTO_EAT={BoolToEnv(instance.InstinctAutoEat)}");
        sb.AppendLine($"INSTINCT_AUTO_TOOL={BoolToEnv(instance.InstinctAutoTool)}");
        sb.AppendLine($"INSTINCT_AUTO_DUMP={BoolToEnv(instance.InstinctAutoDump)}");
        sb.AppendLine($"DEBUG_MODE={BoolToEnv(instance.DebugMode)}");
        sb.AppendLine($"AUTO_ACCEPT_RESOURCE_PACK={BoolToEnv(instance.AutoAcceptResourcePack)}");
        sb.AppendLine($"SUPPRESS_ACTION_CHAT={BoolToEnv(instance.SuppressActionChat)}");
        sb.AppendLine($"FORBID_MINING={BoolToEnv(instance.ForbidMining)}");

        File.WriteAllText(envPath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// �� .env �ļ���ȡ���ò���䵽 BotInstance��
    /// </summary>
    public BotInstance ReadEnv(string instanceName)
    {
        var envPath = PathHelper.GetInstanceEnvPath(instanceName);
        var instance = new BotInstance { InstanceName = instanceName };

        if (!File.Exists(envPath))
            return instance;

        var lines = File.ReadAllLines(envPath, Encoding.UTF8);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();

            // ȥ�����ܵ����Ű���
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            dict[key] = value;
        }

        // ӳ�䵽ģ��
        if (dict.TryGetValue("MC_HOST", out var v)) instance.McHost = v;
        if (dict.TryGetValue("MC_PORT", out v)) instance.McPort = v;
        if (dict.TryGetValue("MC_VERSION", out v)) instance.McVersion = v;
        if (dict.TryGetValue("MC_USERNAME", out v)) instance.McUsername = v;
        if (dict.TryGetValue("MC_AUTH_TYPE", out v)) instance.McAuthType = v;
        if (dict.TryGetValue("MC_LOGIN_PASSWORD", out v)) instance.McLoginPassword = v;
        if (dict.TryGetValue("AUTH_SERVER_URL", out v)) instance.AuthServerUrl = v;
        if (dict.TryGetValue("REGISTER_URL", out v)) instance.RegisterUrl = v;
        if (dict.TryGetValue("MC_OWNER_NAME", out v)) instance.McOwnerName = v;
        
        if (dict.TryGetValue("LLM_API_KEY", out v)) instance.LlmApiKey = v;
        else if (dict.TryGetValue("DEEPSEEK_API_KEY", out v)) instance.LlmApiKey = v; // Backward compatibility

        if (dict.TryGetValue("LLM_API_URL", out v)) instance.LlmApiUrl = v;
        if (dict.TryGetValue("LLM_MODEL", out v)) instance.LlmModel = v;
        if (dict.TryGetValue("VISION_MODEL", out v)) instance.VisionModel = v;
        if (dict.TryGetValue("VISION_API_URL", out v)) instance.VisionApiUrl = v;
        if (dict.TryGetValue("VISION_API_KEY", out v)) instance.VisionApiKey = v;
        if (dict.TryGetValue("AI_STYLE_PROMPT", out v)) instance.AiStylePrompt = v;
        if (dict.TryGetValue("TELL_MODE", out v)) instance.TellMode = v;
        if (dict.TryGetValue("CHAT_TRIGGER", out v)) instance.ChatTrigger = v;
        if (dict.TryGetValue("TRIGGER_MODE", out v)) instance.TriggerMode = v;
        if (dict.TryGetValue("AUTO_DEFEND_ENABLED", out v)) instance.AutoDefendEnabled = EnvToBool(v);
        if (dict.TryGetValue("INSTINCT_TP_ACCEPT", out v)) instance.AutoAcceptTpa = EnvToBool(v);
        if (dict.TryGetValue("TPA_OWNER_ONLY", out v)) instance.TpaOwnerOnly = EnvToBool(v);
        if (dict.TryGetValue("TPA_ACCEPT_TRIGGER", out v)) instance.TpaAcceptTrigger = v;
        if (dict.TryGetValue("INSTINCT_AUTO_LOGIN", out v)) instance.AutoLogin = EnvToBool(v);
        if (dict.TryGetValue("INSTINCT_DEATH_BACK", out v)) instance.DeathBack = EnvToBool(v);
        // 旧版单一开关 INSTINCT_AUTO_TP_LOGIN 迁移：新字段都没写时按旧值填充三项
        if (dict.TryGetValue("INSTINCT_AUTO_TP_LOGIN", out v) &&
            !dict.ContainsKey("INSTINCT_TP_ACCEPT") &&
            !dict.ContainsKey("INSTINCT_AUTO_LOGIN") &&
            !dict.ContainsKey("INSTINCT_DEATH_BACK"))
        {
            var legacy = EnvToBool(v);
            instance.AutoAcceptTpa = legacy;
            instance.AutoLogin = legacy;
            instance.DeathBack = legacy;
        }
        if (dict.TryGetValue("INSTINCT_AUTO_EAT", out v)) instance.InstinctAutoEat = EnvToBool(v);
        if (dict.TryGetValue("INSTINCT_AUTO_TOOL", out v)) instance.InstinctAutoTool = EnvToBool(v);
        if (dict.TryGetValue("INSTINCT_AUTO_DUMP", out v)) instance.InstinctAutoDump = EnvToBool(v);
        if (dict.TryGetValue("DEBUG_MODE", out v)) instance.DebugMode = EnvToBool(v);
        if (dict.TryGetValue("AUTO_ACCEPT_RESOURCE_PACK", out v)) instance.AutoAcceptResourcePack = EnvToBool(v);
        if (dict.TryGetValue("SUPPRESS_ACTION_CHAT", out v)) instance.SuppressActionChat = EnvToBool(v);
        if (dict.TryGetValue("FORBID_MINING", out v)) instance.ForbidMining = EnvToBool(v);

        return instance;
    }

    private static string BoolToEnv(bool value) => value ? "true" : "false";

    private static bool EnvToBool(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value == "1" ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
