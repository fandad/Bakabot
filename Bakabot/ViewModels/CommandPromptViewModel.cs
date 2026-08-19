using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Bakabot.Helpers;
using Bakabot.Models;
using Bakabot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bakabot.ViewModels;

/// <summary>
/// 命令提示词页：纯关键词模式。
/// 整句包含关键词时，机器人直接执行对应命令（不走 LLM）。
/// 配置全局一份，可指定生效范围（全部实例 / 单个实例），
/// 保存后写入各实例根目录的 quick_commands.json，机器人即时读取。
/// </summary>
public partial class CommandPromptViewModel : ObservableObject
{
    public const string AllLabel = "（全部实例）";

    private readonly SettingsService _settingsService;
    private readonly InstanceManager _instanceManager;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _selectedScope = AllLabel;

    [ObservableProperty]
    private bool _blockGame;

    [ObservableProperty]
    private bool _blockQq;

    [ObservableProperty]
    private bool _suppressGameReply;

    public ObservableCollection<string> ScopeOptions { get; } = new() { AllLabel };
    public ObservableCollection<QuickCommandRow> Rows { get; } = new();

    public CommandPromptViewModel(SettingsService settingsService, InstanceManager instanceManager)
    {
        _settingsService = settingsService;
        _instanceManager = instanceManager;

        var settings = _settingsService.Settings;
        _enabled = settings.QQQuickCommandsEnabled;
        _blockGame = settings.QQQuickBlockGame;
        _blockQq = settings.QQQuickBlockQq;
        _suppressGameReply = settings.QQQuickSuppressGameReply;
        _selectedScope = string.IsNullOrWhiteSpace(settings.QQQuickCommandsScope) ||
                         settings.QQQuickCommandsScope == "ALL"
            ? AllLabel
            : settings.QQQuickCommandsScope;

        foreach (var qc in settings.QQQuickCommands ?? new())
            Rows.Add(new QuickCommandRow(qc, SaveSettings));
        if (Rows.Count == 0)
            Rows.Add(new QuickCommandRow(new QuickCommand(), SaveSettings));

        _instanceManager.Instances.CollectionChanged += (_, _) =>
        {
            RefreshScopeOptions();
            SyncToInstances();
        };
        RefreshScopeOptions();
        SyncToInstances();
    }

    partial void OnEnabledChanged(bool value)
    {
        SaveSettings();
    }

    partial void OnSelectedScopeChanged(string value)
    {
        SaveSettings();
    }

    partial void OnBlockGameChanged(bool value)
    {
        SaveSettings();
    }

    partial void OnBlockQqChanged(bool value)
    {
        SaveSettings();
    }

    partial void OnSuppressGameReplyChanged(bool value)
    {
        SaveSettings();
    }

    private void RefreshScopeOptions()
    {
        var names = _instanceManager.Instances.Select(i => i.InstanceName).ToList();
        ScopeOptions.Clear();
        ScopeOptions.Add(AllLabel);
        foreach (var n in names)
            ScopeOptions.Add(n);
        // 保持当前选择有效（实例可能被删除）
        if (!ScopeOptions.Contains(SelectedScope))
            SelectedScope = AllLabel;
    }

    /// <summary>把当前配置写入设置，并把 quick_commands.json 同步到生效范围内的实例</summary>
    public void SaveSettings()
    {
        _settingsService.UpdateSettings(s =>
        {
            s.QQQuickCommandsEnabled = Enabled;
            s.QQQuickBlockGame = BlockGame;
            s.QQQuickBlockQq = BlockQq;
            s.QQQuickSuppressGameReply = SuppressGameReply;
            s.QQQuickCommandsScope = SelectedScope == AllLabel ? "ALL" : SelectedScope;
            s.QQQuickCommands = Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Keyword))
                .Select(r => new QuickCommand { Keyword = r.Keyword.Trim(), Command = r.Command.Trim() })
                .ToList();
        });
        SyncToInstances();
    }

    /// <summary>按生效范围写入/删除各实例根目录的 quick_commands.json（机器人每条消息都会重读）</summary>
    public void SyncToInstances()
    {
        try
        {
            var payload = new
            {
                enabled = Enabled,
                blockGame = BlockGame,
                blockQq = BlockQq,
                suppressGameReply = SuppressGameReply,
                commands = Rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.Keyword) && !string.IsNullOrWhiteSpace(r.Command))
                    .Select(r => new { keyword = r.Keyword.Trim(), command = r.Command.Trim() })
                    .ToList()
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var scope = SelectedScope == AllLabel ? null : SelectedScope;

            foreach (var inst in _instanceManager.Instances)
            {
                var inScope = scope == null || inst.InstanceName == scope;
                var file = Path.Combine(PathHelper.GetInstanceDir(inst.InstanceName), "quick_commands.json");
                if (!inScope || !Enabled)
                {
                    if (File.Exists(file)) File.Delete(file);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, json);
            }
        }
        catch
        {
            // 同步失败不阻塞界面，下次保存/实例变化会重试
        }
    }

    [RelayCommand]
    private void AddRow()
    {
        Rows.Add(new QuickCommandRow(new QuickCommand(), SaveSettings));
    }

    [RelayCommand]
    private void RemoveRow(QuickCommandRow? row)
    {
        if (row == null) return;
        Rows.Remove(row);
        SaveSettings();
    }
}

/// <summary>关键词命令行：编辑即保存（含 quick_commands.json 同步）</summary>
public partial class QuickCommandRow : ObservableObject
{
    private readonly QuickCommand _entry;
    private readonly Action _save;

    [ObservableProperty]
    private string _keyword;

    [ObservableProperty]
    private string _command;

    public QuickCommandRow(QuickCommand entry, Action save)
    {
        _entry = entry;
        _save = save;
        _keyword = entry.Keyword;
        _command = entry.Command;
    }

    partial void OnKeywordChanged(string value)
    {
        _entry.Keyword = value;
        _save();
    }

    partial void OnCommandChanged(string value)
    {
        _entry.Command = value;
        _save();
    }
}
