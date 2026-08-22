using System.Collections.ObjectModel;
using System.Windows;
using Bakabot.Models;
using Bakabot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bakabot.ViewModels;

/// <summary>
/// QQ 功能（NapCat）页：总开关、群配置、NapCat 下载状态、全局白名单管理。
/// 白名单行由 WhitelistRow 包装，提供实例下拉选项与保存回调。
/// </summary>
public partial class QQViewModel : ObservableObject
{
    public const string AllInstancesLabel = "（全部）";

    private readonly QQService _qqService;
    private readonly SettingsService _settingsService;
    private readonly InstanceManager _instanceManager;
    private readonly DownloadService _downloadService;
    private readonly NapCatService _napCatService;

    /// <summary>白名单行（含实例下拉选项），绑定到页面列表</summary>
    public ObservableCollection<WhitelistRow> Rows { get; } = new();

    /// <summary>绑定实例下拉选项（首项"（全部）"代表空）</summary>
    public ObservableCollection<string> BindInstanceOptions { get; } = new() { AllInstancesLabel };

    [ObservableProperty]
    private bool _qqEnabled;

    [ObservableProperty]
    private string _qqGroupIds = string.Empty;

    [ObservableProperty]
    private string _qqTriggerKeywords = string.Empty;

    [ObservableProperty]
    private string _newQQ = string.Empty;

    [ObservableProperty]
    private string _newPlayerName = string.Empty;

    [ObservableProperty]
    private string _newInstanceName = string.Empty;

    [ObservableProperty]
    private string _botQQ = string.Empty;

    [ObservableProperty]
    private bool _qqAllowAll;

    /// <summary>QQ 只回 AI 消息：开启后屏蔽行动播报/报错/系统提示，只发 AI 回复</summary>
    [ObservableProperty]
    private bool _qqSuppressNonAI;

    [ObservableProperty]
    private bool _isNapCatDownloaded;

    [ObservableProperty]
    private bool _isNapCatRunning;

    [ObservableProperty]
    private bool _isNapCatAvailable;

    [ObservableProperty]
    private bool _isDownloadingNapCat;

    [ObservableProperty]
    private double _napCatProgress;

    [ObservableProperty]
    private string _webUiUrl = string.Empty;

    [ObservableProperty]
    private string _napCatStatusText = "NapCat 未下载";

    public QQViewModel(QQService qqService, SettingsService settingsService,
        InstanceManager instanceManager, DownloadService downloadService, NapCatService napCatService)
    {
        _qqService = qqService;
        _settingsService = settingsService;
        _instanceManager = instanceManager;
        _downloadService = downloadService;
        _napCatService = napCatService;

        var settings = _settingsService.Settings;
        _qqEnabled = settings.QQEnabled;
        _botQQ = settings.QQBotNumber ?? string.Empty;
        _qqAllowAll = settings.QQAllowAll;
        _qqGroupIds = settings.QQGroupIds ?? string.Empty;
        _qqTriggerKeywords = settings.QQTriggerKeywords ?? string.Empty;
        _qqSuppressNonAI = settings.QQSuppressNonAI;

        _instanceManager.Instances.CollectionChanged += (_, _) => RefreshInstanceOptions();
        _qqService.WhitelistChanged += (_, _) => RebuildRows();
        _napCatService.StateChanged += (_, state) =>
            Application.Current.Dispatcher.BeginInvoke(() => ApplyNapCatState(state));

        RefreshInstanceOptions();
        RebuildRows();
        RefreshStatus();
    }

    partial void OnQqEnabledChanged(bool value)
    {
        _settingsService.UpdateSettings(s => s.QQEnabled = value);
        if (value)
            _ = StartNapCatAsync();
        else
            _ = StopNapCatAsync();
    }

    partial void OnBotQQChanged(string value)
    {
        _settingsService.UpdateSettings(s => s.QQBotNumber = value.Trim());
    }

    partial void OnQqAllowAllChanged(bool value)
    {
        _settingsService.UpdateSettings(s => s.QQAllowAll = value);
    }

    partial void OnQqGroupIdsChanged(string value)
    {
        _settingsService.UpdateSettings(s => s.QQGroupIds = value);
    }

    partial void OnQqTriggerKeywordsChanged(string value)
    {
        _settingsService.UpdateSettings(s => s.QQTriggerKeywords = value);
    }

    partial void OnQqSuppressNonAIChanged(bool value)
    {
        _settingsService.UpdateSettings(s => s.QQSuppressNonAI = value);
    }

    private void RefreshInstanceOptions()
    {
        var names = _instanceManager.Instances.Select(i => i.InstanceName).ToList();
        BindInstanceOptions.Clear();
        BindInstanceOptions.Add(AllInstancesLabel);
        foreach (var n in names)
            BindInstanceOptions.Add(n);
        if (Rows.Count > 0)
            RebuildRows();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var entry in _qqService.Whitelist)
            Rows.Add(new WhitelistRow(entry, BindInstanceOptions, AllInstancesLabel,
                e => _qqService.SaveEntry(e)));
    }

    private void ApplyNapCatState(NapCatService.NapCatState state)
    {
        IsNapCatRunning = state.Running;
        WebUiUrl = state.WebUiUrl;
        NapCatStatusText = state.StatusText;
    }

    /// <summary>添加白名单：QQ 号必填，玩家名/绑定实例可空（空实例 = 全部）</summary>
    [RelayCommand]
    private void AddWhitelist()
    {
        var qq = NewQQ?.Trim() ?? string.Empty;
        if (qq.Length == 0)
        {
            MessageBox.Show("请先输入要添加的 QQ 号。", "添加白名单",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _qqService.AddOrUpdate(new QQWhitelistEntry
        {
            QQ = qq,
            PlayerName = string.IsNullOrWhiteSpace(NewPlayerName) ? null : NewPlayerName.Trim(),
            InstanceName = string.IsNullOrWhiteSpace(NewInstanceName) ? null : NewInstanceName.Trim(),
            Enabled = true
        });

        NewQQ = string.Empty;
        NewPlayerName = string.Empty;
    }

    [RelayCommand]
    private void RemoveWhitelist(string? qq)
    {
        if (!string.IsNullOrEmpty(qq))
            _qqService.Remove(qq);
    }

    /// <summary>切换某行启用状态（开关事件回调）</summary>
    public void SetEntryEnabled(WhitelistRow row, bool enabled)
        => _qqService.SetEnabled(row.Entry.QQ, enabled);

    [RelayCommand]
    private void RefreshStatus()
    {
        IsNapCatDownloaded = _downloadService.IsNapCatDownloaded();
        IsNapCatAvailable = _napCatService.IsAvailable;
        IsNapCatRunning = _napCatService.IsRunning;
        WebUiUrl = _napCatService.WebUiUrl;
        NapCatStatusText = IsNapCatRunning
            ? _napCatService.StatusText
            : IsNapCatDownloaded
                ? "NapCat 已下载，未启动"
                : "NapCat 未下载";
    }

    /// <summary>启动 NapCat 协议端（反向 WS 与进程由 NapCatService 托管）</summary>
    [RelayCommand]
    private async Task StartNapCatAsync()
    {
        if (IsDownloadingNapCat || IsNapCatRunning) return;
        NapCatStatusText = "正在启动 NapCat...";
        try
        {
            await _napCatService.StartAsync();
        }
        catch (Exception ex)
        {
            NapCatStatusText = $"NapCat 启动失败: {ex.Message}";
        }
    }

    /// <summary>停止 NapCat 协议端</summary>
    [RelayCommand]
    private async Task StopNapCatAsync()
    {
        if (!IsNapCatRunning) return;
        NapCatStatusText = "正在停止 NapCat...";
        await _napCatService.StopAsync();
    }

    /// <summary>在浏览器打开 NapCat WebUI（扫码登录/管理配置）</summary>
    [RelayCommand]
    private void OpenWebUI()
    {
        if (string.IsNullOrEmpty(WebUiUrl))
            WebUiUrl = _napCatService.WebUiUrl;
        if (string.IsNullOrEmpty(WebUiUrl))
        {
            MessageBox.Show("NapCat 尚未启动，无法打开登录页。", "打开登录页",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = WebUiUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开浏览器: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DownloadNapCatAsync()
    {
        if (IsDownloadingNapCat) return;
        IsDownloadingNapCat = true;
        NapCatProgress = 0;
        NapCatStatusText = "正在获取最新版本信息...";

        try
        {
            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total > 0)
                {
                    NapCatProgress = (double)p.downloaded / p.total * 100;
                    NapCatStatusText = $"正在下载 NapCat... {p.downloaded / 1024 / 1024}MB / {p.total / 1024 / 1024}MB ({NapCatProgress:F1}%)";
                }
                else
                {
                    NapCatStatusText = $"正在下载 NapCat... {p.downloaded / 1024 / 1024}MB";
                }
            });

            await _downloadService.DownloadNapCatAsync(progress);

            IsNapCatDownloaded = true;
            NapCatStatusText = "NapCat 下载完成";
        }
        catch (Exception ex)
        {
            NapCatStatusText = $"NapCat 下载失败: {ex.Message}";
        }
        finally
        {
            IsDownloadingNapCat = false;
        }
    }
}

/// <summary>
/// 白名单列表行：包装条目并提供实例下拉选项；实例选择变化时更新条目并静默保存。
/// </summary>
public partial class WhitelistRow : ObservableObject
{
    private readonly Action<QQWhitelistEntry> _save;
    private readonly string _allLabel;

    public QQWhitelistEntry Entry { get; }
    public ObservableCollection<string> InstanceOptions { get; }

    public string QQDisplay => Entry.QQ;
    public string PlayerNameDisplay => string.IsNullOrEmpty(Entry.PlayerName) ? "（未绑定）" : Entry.PlayerName!;

    [ObservableProperty]
    private string _selectedInstance;

    public WhitelistRow(QQWhitelistEntry entry, ObservableCollection<string> options,
        string allLabel, Action<QQWhitelistEntry> save)
    {
        Entry = entry;
        InstanceOptions = options;
        _allLabel = allLabel;
        _save = save;
        _selectedInstance = string.IsNullOrEmpty(entry.InstanceName) ? _allLabel : entry.InstanceName!;
    }

    partial void OnSelectedInstanceChanged(string value)
    {
        Entry.InstanceName = value == _allLabel ? null : value;
        _save(Entry);
    }
}
