using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bakabot.Helpers;
using Bakabot.Models;

namespace Bakabot.Services;

/// <summary>
/// QQ 桥接服务（启动器侧）：
/// - 维护全局白名单（QQ 号 → 玩家名 / 绑定实例 / 启用开关）
/// - 群消息入口：群过滤 → 触发词剥除 → 白名单拦截 → 按绑定实例转发到机器人 stdin
/// - 监听机器人 stdout 的 [QQ-OUT] 标记行，转交 NapCat 接入层发送到 QQ
/// - 机器人补丁负责会话通道与输出分流，本服务不解析指令内容
/// </summary>
public class QQService
{
    private readonly InstanceManager _instanceManager;
    private readonly SettingsService _settingsService;

    /// <summary>全局白名单（ObservableCollection 供 UI 直接绑定）</summary>
    public ObservableCollection<QQWhitelistEntry> Whitelist { get; } = new();

    /// <summary>机器人输出到 QQ 的消息（NapCat 接入层订阅后发送）</summary>
    public event EventHandler<QQOutMessage>? QQOutReceived;

    /// <summary>白名单变化（UI 刷新 / 持久化提示）</summary>
    public event EventHandler? WhitelistChanged;

    public QQService(InstanceManager instanceManager, SettingsService settingsService)
    {
        _instanceManager = instanceManager;
        _settingsService = settingsService;
    }

    /// <summary>应用启动时调用：加载白名单并订阅实例进程输出</summary>
    public void Initialize()
    {
        LoadWhitelist();
        _instanceManager.ProcessStarted += OnProcessStarted;
    }

    // ─── 机器人输出监听 ───

    private void OnProcessStarted(string instanceName, NodeProcessManager pm)
    {
        pm.OutputReceived += (_, entry) =>
        {
            if (entry.Text.StartsWith("[QQ-OUT] ", StringComparison.Ordinal))
                ParseQQOut(entry.Text);
        };
    }

    private void ParseQQOut(string line)
    {
        try
        {
            var json = line.Substring("[QQ-OUT] ".Length);
            var msg = JsonSerializer.Deserialize<QQOutMessage>(json);
            if (msg == null || string.IsNullOrEmpty(msg.Text)) return;
            QQOutReceived?.Invoke(this, msg);
        }
        catch
        {
            // 解析失败忽略（不影响日志输出）
        }
    }

    // ─── 群消息入口 ───

    /// <summary>
    /// 群消息入口（NapCat 接入层收到群消息后调用）：
    /// 群过滤 → 触发词剥除 → 白名单拦截 → 转发。
    /// </summary>
    public async Task HandleGroupMessageAsync(string groupId, string qq, string text)
    {
        if (!_settingsService.Settings.QQEnabled) return;
        if (string.IsNullOrWhiteSpace(text)) return;

        // 群过滤：配置了群 ID 时只处理指定群；留空 = 所有群
        var groupIds = SplitCsv(_settingsService.Settings.QQGroupIds);
        if (groupIds.Count > 0 && !groupIds.Contains(groupId)) return;

        // 剥除 CQ at 码（@机器人）
        var wasAt = text.TrimStart().StartsWith("[CQ:at", StringComparison.OrdinalIgnoreCase);
        var stripped = Regex.Replace(text, @"\[CQ:at[^\]]*\]", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(stripped)) return;

        // 触发判定：@机器人 始终有效；配置了关键词时，句首命中关键词也算触发并剥除
        var keywords = SplitCsv(_settingsService.Settings.QQTriggerKeywords);
        var triggered = wasAt;
        foreach (var kw in keywords)
        {
            if (stripped.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
            {
                stripped = stripped.Substring(kw.Length).Trim();
                triggered = true;
                break;
            }
        }
        if (!triggered) return;
        if (string.IsNullOrWhiteSpace(stripped)) return;

        // 白名单拦截：明确加入且被禁用的用户始终拦截；
        // 未加白名单的用户仅在“允许所有人”开启时放行（无玩家绑定，转发所有实例）
        var entry = Whitelist.FirstOrDefault(w => w.QQ == qq);
        if (entry != null)
        {
            if (!entry.Enabled) return;
            await ForwardAsync(entry, groupId, stripped);
            return;
        }

        if (_settingsService.Settings.QQAllowAll)
        {
            await ForwardAsync(new QQWhitelistEntry { QQ = qq, Enabled = true }, groupId, stripped);
        }
    }

    /// <summary>按白名单条目的绑定实例转发；实例为空 = 转发所有运行中的实例</summary>
    private async Task ForwardAsync(QQWhitelistEntry entry, string groupId, string text)
    {
        var targets = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.InstanceName))
        {
            targets.Add(entry.InstanceName);
        }
        else
        {
            targets.AddRange(_instanceManager.RunningProcesses.Keys);
        }

        var payload = JsonSerializer.Serialize(new QQCmdMessage
        {
            QQ = entry.QQ,
            GroupId = groupId,
            Player = entry.PlayerName,
            Text = text
        });
        var line = "QQCMD " + payload;

        foreach (var instance in targets)
        {
            if (!_instanceManager.RunningProcesses.TryGetValue(instance, out var pm)) continue;
            try { await pm.WriteInputAsync(line); }
            catch { /* 单个实例转发失败不影响其他实例 */ }
        }
    }

    // ─── 白名单 CRUD 与持久化 ───

    public void AddOrUpdate(QQWhitelistEntry entry)
    {
        var existing = Whitelist.FirstOrDefault(w => w.QQ == entry.QQ);
        if (existing != null)
        {
            var index = Whitelist.IndexOf(existing);
            Whitelist[index] = entry;
        }
        else
        {
            Whitelist.Add(entry);
        }
        SaveWhitelist();
        WhitelistChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string qq)
    {
        var existing = Whitelist.FirstOrDefault(w => w.QQ == qq);
        if (existing != null)
        {
            Whitelist.Remove(existing);
            SaveWhitelist();
            WhitelistChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>随时切换某人的启用状态（开关即时生效，不触发列表重建）</summary>
    public void SetEnabled(string qq, bool enabled)
    {
        var entry = Whitelist.FirstOrDefault(w => w.QQ == qq);
        if (entry != null)
        {
            entry.Enabled = enabled;
            SaveWhitelist();
        }
    }

    /// <summary>保存条目修改（绑定实例/玩家名等），不触发 WhitelistChanged，避免 UI 列表重建</summary>
    public void SaveEntry(QQWhitelistEntry entry) => SaveWhitelist();

    private void LoadWhitelist()
    {
        Whitelist.Clear();
        if (!File.Exists(PathHelper.QQWhitelistPath)) return;
        try
        {
            var json = File.ReadAllText(PathHelper.QQWhitelistPath);
            var list = JsonSerializer.Deserialize<List<QQWhitelistEntry>>(json);
            if (list == null) return;
            foreach (var entry in list)
                Whitelist.Add(entry);
        }
        catch
        {
            // 白名单损坏时从空列表开始，不阻塞启动
        }
    }

    private void SaveWhitelist()
    {
        try
        {
            Directory.CreateDirectory(PathHelper.RootDir);
            var json = JsonSerializer.Serialize(Whitelist.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathHelper.QQWhitelistPath, json);
        }
        catch
        {
            // 保存失败不阻塞功能
        }
    }

    private static List<string> SplitCsv(string? value) =>
        (value ?? string.Empty)
            .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
