using System;
using System.Windows;
using System.IO;
using System.Threading.Tasks;
using Bakabot;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Bakabot.Helpers;
using Bakabot.Services;

namespace Bakabot.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly DownloadService _downloadService;
    private readonly SettingsService _settingsService;
    private readonly ViaProxyService _viaProxyService;

    // ─── Node.js 运行时 ───
    [ObservableProperty]
    private bool _isNodeInstalled;

    [ObservableProperty]
    private bool _isDownloading = false;

    [ObservableProperty]
    private double _downloadProgress = 0;

    [ObservableProperty]
    private string _downloadStatusText = string.Empty;

    // ─── 基础包 ───
    [ObservableProperty]
    private bool _isBaseAgentDownloaded;

    [ObservableProperty]
    private bool _isDownloadingBaseAgent = false;

    [ObservableProperty]
    private double _baseAgentProgress = 0;

    [ObservableProperty]
    private string _baseAgentStatusText = string.Empty;

    // ─── 自定义基础包 ───
    [ObservableProperty]
    private bool _useCustomBaseAgent;

    [ObservableProperty]
    private string _customBaseAgentPath = string.Empty;

    [ObservableProperty]
    private bool _isCustomBaseAgentImported;

    // ─── 弹窗设置 ───
    [ObservableProperty]
    private bool _disableAfdianPopup;

    // ─── ViaProxy 协议代理（26.x 直连支持）───
    [ObservableProperty]
    private bool _enableViaProxy;

    [ObservableProperty]
    private string _javaPath = string.Empty;

    [ObservableProperty]
    private bool _isViaProxyDownloaded;

    [ObservableProperty]
    private bool _isJavaAvailable;

    [ObservableProperty]
    private string _javaStatusText = string.Empty;

    [ObservableProperty]
    private string _viaProxyStatusText = string.Empty;

    [ObservableProperty]
    private bool _isDownloadingViaProxy = false;

    [ObservableProperty]
    private double _viaProxyProgress = 0;

    [ObservableProperty]
    private string _viaProxyAccountStatusText = string.Empty;

    // ─── 关于 ───
    public string AppVersion => "2.2.0";
    public string DotNetVersion => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string AppDataPath => PathHelper.RootDir;

    public SettingsViewModel(DownloadService downloadService, SettingsService settingsService,
        ViaProxyService viaProxyService)
    {
        _downloadService = downloadService;
        _settingsService = settingsService;
        _viaProxyService = viaProxyService;

        // 从设置服务初始化
        var settings = _settingsService.Settings;
        _useCustomBaseAgent = settings.UseCustomBaseAgent;
        _customBaseAgentPath = settings.CustomBaseAgentPath;
        _disableAfdianPopup = settings.DisableAfdianPopup;
        _enableViaProxy = settings.EnableViaProxy;
        _javaPath = settings.JavaPath;

        RefreshStatus();
    }

    partial void OnDisableAfdianPopupChanged(bool value)
    {
        _settingsService.UpdateSettings(s => s.DisableAfdianPopup = value);
    }

    [RelayCommand]
    private void OpenAfdian()
    {
        OpenUrl("https://ifdian.net/a/fentai2333");
    }

    [RelayCommand]
    private void OpenGithub()
    {
        OpenUrl("https://github.com/FENTAIIII");
    }

    [RelayCommand]
    private void OpenBakabotGithub()
    {
        OpenUrl("https://github.com/fandad/Bakabot");
    }

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开链接: {ex.Message}");
        }
    }

    partial void OnUseCustomBaseAgentChanged(bool value)
    {
        _settingsService.UpdateSettings(s => s.UseCustomBaseAgent = value);
    }

    [RelayCommand]
    private void ImportCustomBaseAgent()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "压缩文件|*.zip",
            Title = "选择自定义基础包"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.Copy(dialog.FileName, PathHelper.CustomBaseAgentZipPath, true);
                CustomBaseAgentPath = dialog.FileName;
                _settingsService.UpdateSettings(s => s.CustomBaseAgentPath = dialog.FileName);
                RefreshStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        IsNodeInstalled = _downloadService.IsNodeInstalled();
        IsBaseAgentDownloaded = _downloadService.IsBaseAgentDownloaded();
        IsCustomBaseAgentImported = File.Exists(PathHelper.CustomBaseAgentZipPath);
        IsViaProxyDownloaded = _downloadService.IsViaProxyDownloaded();
        IsJavaAvailable = JavaHelper.IsJavaAvailable(JavaPath);

        DownloadStatusText = IsNodeInstalled ? "Node.js 运行时已就绪" : "未安装 Node.js 运行时";
        BaseAgentStatusText = IsBaseAgentDownloaded ? "基础包已下载" : "基础包未下载";
        ViaProxyStatusText = IsViaProxyDownloaded ? "ViaProxy.jar 已就绪" : "ViaProxy.jar 未下载";
        JavaStatusText = JavaHelper.GetStatusText(JavaPath);

        var accountCount = _viaProxyService.GetAccountCount();
        ViaProxyAccountStatusText = accountCount > 0
            ? $"已配置 {accountCount} 个正版账号，可进入正版验证服务器"
            : "未配置正版账号（仅离线服直连需要；进正版服前请先添加）";
    }
    /// <summary>一键下载：依次触发 Node.js 运行时与 ViaProxy 下载（不含基础包，原两个下载按钮保持不变）</summary>
    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        if (IsDownloading || IsDownloadingViaProxy) return;

        await DownloadNodeAsync();
        await DownloadViaProxyAsync();
        RefreshStatus();
    }

    /// <summary>下载 Node.js 运行时</summary>
    [RelayCommand]
    private async Task DownloadNodeAsync()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatusText = "正在下载 Node.js 运行时...";

        try
        {
            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total > 0)
                {
                    DownloadProgress = (double)p.downloaded / p.total * 100;
                    DownloadStatusText = $"下载中... {p.downloaded / 1024 / 1024}MB / {p.total / 1024 / 1024}MB ({DownloadProgress:F1}%)";
                }
                else
                {
                    DownloadStatusText = $"下载中... {p.downloaded / 1024 / 1024}MB";
                }
            });

            await _downloadService.DownloadNodeRuntimeAsync(progress);

            IsNodeInstalled = true;
            DownloadStatusText = "Node.js 运行时下载完成！"; // 去除 Emoji
        }
        catch (Exception ex)
        {
            DownloadStatusText = $"下载失败: {ex.Message}"; // 去除 Emoji
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>下载机器人基础包</summary>
    [RelayCommand]
    private async Task DownloadBaseAgentAsync()
    {
        if (IsDownloadingBaseAgent) return;
        IsDownloadingBaseAgent = true;
        BaseAgentProgress = 0;
        BaseAgentStatusText = "正在下载基础包...";

        try
        {
            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total > 0)
                {
                    BaseAgentProgress = (double)p.downloaded / p.total * 100;
                    BaseAgentStatusText = $"下载中... {p.downloaded / 1024 / 1024}MB / {p.total / 1024 / 1024}MB ({BaseAgentProgress:F1}%)";
                }
                else
                {
                    BaseAgentStatusText = $"下载中... {p.downloaded / 1024}KB";
                }
            });

            await _downloadService.DownloadBaseAgentAsync(progress);

            IsBaseAgentDownloaded = true;
            BaseAgentStatusText = "基础包下载完成！"; // 去除 Emoji
        }
        catch (Exception ex)
        {
            BaseAgentStatusText = $"下载失败: {ex.Message}"; // 去除 Emoji
        }
        finally
        {
            IsDownloadingBaseAgent = false;
        }
    }



    /// <summary>打开 AppData 目录</summary>
    [RelayCommand]
    private void OpenAppDataFolder()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = PathHelper.RootDir,
            UseShellExecute = true
        });
    }

    // ─── ViaProxy 设置事件 ───

    partial void OnEnableViaProxyChanged(bool value)
    {
        _settingsService.UpdateSettings(s => s.EnableViaProxy = value);
    }

    partial void OnJavaPathChanged(string value)
    {
        _settingsService.UpdateSettings(s => s.JavaPath = value);
        IsJavaAvailable = JavaHelper.IsJavaAvailable(value);
        JavaStatusText = JavaHelper.GetStatusText(value);
    }

    /// <summary>下载 ViaProxy JAR</summary>
    [RelayCommand]
    private async Task DownloadViaProxyAsync()
    {
        if (IsDownloadingViaProxy) return;
        IsDownloadingViaProxy = true;
        ViaProxyProgress = 0;
        ViaProxyStatusText = "正在获取最新版本信息...";

        try
        {
            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total > 0)
                {
                    ViaProxyProgress = (double)p.downloaded / p.total * 100;
                    ViaProxyStatusText = $"下载中... {p.downloaded / 1024 / 1024}MB / {p.total / 1024 / 1024}MB ({ViaProxyProgress:F1}%)";
                }
                else
                {
                    ViaProxyStatusText = $"下载中... {p.downloaded / 1024 / 1024}MB";
                }
            });

            await _downloadService.DownloadViaProxyAsync(progress);

            IsViaProxyDownloaded = true;
            ViaProxyStatusText = "ViaProxy.jar 下载完成！";
        }
        catch (Exception ex)
        {
            ViaProxyStatusText = $"下载失败: {ex.Message}";
        }
        finally
        {
            IsDownloadingViaProxy = false;
        }
    }

    /// <summary>浏览自定义 java.exe 路径</summary>
    [RelayCommand]
    private void BrowseJavaPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Java 可执行文件|java.exe",
            Title = "选择 java.exe"
        };

        if (dialog.ShowDialog() == true)
        {
            JavaPath = dialog.FileName;
        }
    }

    /// <summary>打开 ViaProxy 图形界面，在 Accounts 页签添加/管理微软正版账号</summary>
    [RelayCommand]
    private void OpenViaProxyAccountManager()
    {
        try
        {
            _viaProxyService.OpenAccountManagerUi();
            MessageBox.Show(
                "已打开 ViaProxy 窗口。\n\n" +
                "请在 Accounts 页签中添加你的微软正版账号，然后关闭该窗口。\n" +
                "关闭后回到本页面点击「刷新账号状态」即可。\n\n" +
                "注意：请勿在机器人运行期间操作该窗口。\n\n" +
                "另：添加账号时若 ViaProxy 报错，属于其连接微软服务偶发波动，报错即登录失败，多试几次即可。",
                "配置正版账号", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开 ViaProxy 账号管理界面: {ex.Message}");
        }
    }
}
