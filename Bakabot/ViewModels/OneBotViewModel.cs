using System.Windows;
using Bakabot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bakabot.ViewModels;

/// <summary>
/// OneBot 接入页：通用 OneBot11 反向 WebSocket 服务端开关、端口、Token 与状态。
/// 与 NapCat 互斥（服务层强制，后启动的顶掉先启动的）。
/// 需要 QQ 功能（NapCat）页的总开关保持开启，消息才会走白名单/转发逻辑。
/// </summary>
public partial class OneBotViewModel : ObservableObject
{
    private const int DefaultPort = 6700;

    private readonly OneBot11ServerService _oneBotService;
    private readonly SettingsService _settingsService;
    private bool _busy;

    [ObservableProperty]
    private bool _oneBotEnabled;

    [ObservableProperty]
    private string _portText = DefaultPort.ToString();

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private string _statusText = "未启动";

    [ObservableProperty]
    private int _clientCount;

    [ObservableProperty]
    private string _listenAddress = $"ws://127.0.0.1:{DefaultPort}";

    [ObservableProperty]
    private bool _isRunning;

    public OneBotViewModel(OneBot11ServerService oneBotService, SettingsService settingsService)
    {
        _oneBotService = oneBotService;
        _settingsService = settingsService;

        var settings = _settingsService.Settings;
        _oneBotEnabled = settings.OneBotEnabled;
        _portText = settings.OneBotPort is >= 1 and <= 65535 ? settings.OneBotPort.ToString() : DefaultPort.ToString();
        _token = settings.OneBotToken ?? string.Empty;
        _listenAddress = $"ws://127.0.0.1:{_portText}";

        _oneBotService.StateChanged += (_, state) =>
            Application.Current.Dispatcher.BeginInvoke(() => ApplyState(state));

        RefreshStatus();
    }

    partial void OnOneBotEnabledChanged(bool value)
    {
        _settingsService.UpdateSettings(s => s.OneBotEnabled = value);
        if (value)
            _ = StartAsync();
        else
            _ = StopAsync();
    }

    partial void OnPortTextChanged(string value)
    {
        if (!TryParsePort(value, out var port)) return;
        _settingsService.UpdateSettings(s => s.OneBotPort = port);
        ListenAddress = $"ws://127.0.0.1:{port}";
        if (IsRunning && !_busy)
            _ = RestartAsync();
    }

    partial void OnTokenChanged(string value)
    {
        _settingsService.UpdateSettings(s => s.OneBotToken = value.Trim());
        if (IsRunning && !_busy)
            _ = RestartAsync();
    }

    private void ApplyState(OneBot11ServerService.OneBot11State state)
    {
        IsRunning = state.Running;
        ClientCount = state.ClientCount;
        StatusText = state.StatusText;
        ListenAddress = $"ws://127.0.0.1:{state.Port}";
    }

    /// <summary>页面加载时刷新一次状态（服务已在后台运行则直接显示）</summary>
    public void RefreshStatus()
    {
        IsRunning = _oneBotService.IsRunning;
        ClientCount = _oneBotService.ClientCount;
        StatusText = _oneBotService.StatusText;
        ListenAddress = $"ws://127.0.0.1:{_oneBotService.Port}";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            StatusText = "正在启动...";
            await _oneBotService.StartAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            StatusText = "正在停止...";
            await _oneBotService.StopAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RestartAsync()
    {
        await _oneBotService.StopAsync();
        try
        {
            await _oneBotService.StartAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private static bool TryParsePort(string value, out int port)
    {
        port = 0;
        return int.TryParse(value?.Trim(), out port) && port is >= 1 and <= 65535;
    }
}
