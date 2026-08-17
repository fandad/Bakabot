using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Bakabot.Helpers;
using Bakabot.Models;
using Bakabot.Services;

namespace Bakabot.ViewModels;

using System.Text;
using Microsoft.Win32;

/// <summary>
/// 首页 ViewModel：展示实例列表，提供启动/编辑/删除/创建功能。
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly InstanceManager _instanceManager;
    private readonly ConsoleViewModel _consoleViewModel;

    public ObservableCollection<BotInstance> Instances => _instanceManager.Instances;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>请求打开创建对话框的事件（由 View 订阅）</summary>
    public event Action? RequestCreateDialog;

    /// <summary>请求编辑实例的事件</summary>
    public event Action<BotInstance>? RequestEditDialog;

    /// <summary>请求导航到控制台页面的事件</summary>
    public event Action<string>? RequestNavigateToConsole;

    public HomeViewModel(InstanceManager instanceManager, ConsoleViewModel consoleViewModel)
    {
        _instanceManager = instanceManager;
        _consoleViewModel = consoleViewModel;
    }

    /// <summary>加载所有实例</summary>
    [RelayCommand]
    private void LoadInstances()
    {
        IsLoading = true;
        try
        {
            _instanceManager.LoadAllInstances();
            StatusMessage = $"已加载 {Instances.Count} 个实例";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>启动指定实例</summary>
    [RelayCommand]
    private async Task StartInstanceAsync(BotInstance instance)
    {
        try
        {
            instance.Status = BotStatus.Starting;
            StatusMessage = $"正在启动 '{instance.InstanceName}'...";
            await _instanceManager.StartInstanceAsync(instance.InstanceName);
            instance.Status = BotStatus.Running;
            StatusMessage = $"实例 '{instance.InstanceName}' 已启动";

            // 请求跳转到控制台
            RequestNavigateToConsole?.Invoke(instance.InstanceName);
        }
        catch (Exception ex)
        {
            instance.Status = BotStatus.Error;
            StatusMessage = $"启动失败: {ex.Message}";
            MessageBox.Show($"启动实例失败:\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>停止指定实例</summary>
    [RelayCommand]
    private async Task StopInstanceAsync(BotInstance instance)
    {
        try
        {
            await _instanceManager.StopInstanceAsync(instance.InstanceName);
            instance.Status = BotStatus.Stopped;
            StatusMessage = $"⬛ 实例 '{instance.InstanceName}' 已停止";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 停止失败: {ex.Message}";
        }
    }

    /// <summary>删除指定实例</summary>
    [RelayCommand]
    private async Task DeleteInstanceAsync(BotInstance instance)
    {
        var result = MessageBox.Show(
            $"确定要删除实例 '{instance.InstanceName}' 吗？\n此操作将删除该实例的所有文件，不可恢复。",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            await _instanceManager.DeleteInstanceAsync(instance.InstanceName);
            StatusMessage = $"🗑️ 实例 '{instance.InstanceName}' 已删除";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 删除失败: {ex.Message}";
        }
    }

    /// <summary>复制实例（新实例名为“原名_copy”）</summary>
    [RelayCommand]
    private void DuplicateInstance(BotInstance instance)
    {
        try
        {
            StatusMessage = $"正在复制实例 '{instance.InstanceName}'...";
            var newName = _instanceManager.DuplicateInstance(instance.InstanceName);
            StatusMessage = $"已复制为新实例 '{newName}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制实例失败:\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>一键刷新所有实例的补丁（资源包等）</summary>
    [RelayCommand]
    private void RepatchAll()
    {
        try
        {
            var count = _instanceManager.RepatchAllInstances();
            StatusMessage = $"已刷新 {count} 个实例的补丁（运行中的实例已跳过）";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"刷新补丁失败:\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>清除指定实例的控制台日志（内存显示 + 磁盘 console.log）</summary>
    [RelayCommand]
    private void ClearInstanceLog(BotInstance instance)
    {
        try
        {
            _consoleViewModel.ClearLogFor(instance.InstanceName);

            var logFile = Path.Combine(PathHelper.GetInstanceDir(instance.InstanceName), "console.log");
            if (File.Exists(logFile))
            {
                File.Delete(logFile);
                StatusMessage = $"🧹 已清除实例 '{instance.InstanceName}' 的日志";
            }
            else
            {
                StatusMessage = $"🧹 已清空实例 '{instance.InstanceName}' 的控制台显示";
            }
        }
        catch (IOException)
        {
            // 实例运行中时 console.log 被占用，仅清内存显示；文件会在下次启动时自动重置
            StatusMessage = $"🧹 已清空屏幕显示；日志文件被运行中的实例占用，重启后自动重置";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 清除日志失败: {ex.Message}";
        }
    }

    /// <summary>导出指定实例的调试日志 txt（调试台记录 + 故障诊断信息）</summary>
    [RelayCommand]
    private void ExportInstanceLog(BotInstance instance)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== Bakabot 实例诊断日志 =====");
            sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            var appVersion = typeof(HomeViewModel).Assembly.GetName().Version?.ToString(3) ?? "未知";
            sb.AppendLine($"应用版本: {appVersion}");
            sb.AppendLine();

            // ─── 实例基本信息 ───
            sb.AppendLine("【实例信息】");
            sb.AppendLine($"实例名: {instance.InstanceName}");
            sb.AppendLine($"服务器: {instance.McHost}:{instance.McPort}");
            sb.AppendLine($"版本: {instance.McVersion}");
            sb.AppendLine($"账号: {instance.McUsername} ({instance.McAuthType})");
            sb.AppendLine($"状态: {instance.Status}");
            sb.AppendLine();

            // ─── 运行环境诊断 ───
            sb.AppendLine("【环境诊断】");
            sb.AppendLine($"Node.js: {(File.Exists(PathHelper.NodeExePath) ? "已安装" : "缺失")}");
            var srcDir = PathHelper.GetInstanceSrcDir(instance.InstanceName);
            sb.AppendLine($"入口文件: {(File.Exists(Path.Combine(srcDir, "index.js")) ? "存在" : "缺失")}");
            sb.AppendLine($"ViaProxy JAR: {(File.Exists(PathHelper.ViaProxyJarPath) ? "存在" : "未下载")}");
            var envPath = PathHelper.GetInstanceEnvPath(instance.InstanceName);
            sb.AppendLine($"配置文件: {(File.Exists(envPath) ? "存在" : "缺失")}");
            sb.AppendLine();

            // ─── 敏感信息脱敏后的 .env 关键项 ───
            sb.AppendLine("【配置摘要（敏感字段已脱敏）】");
            try
            {
                if (File.Exists(envPath))
                {
                    var sensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "MC_LOGIN_PASSWORD", "LLM_API_KEY", "VISION_API_KEY", "AUTH_SERVER_URL",
                        "REGISTER_URL", "LLM_API_URL", "VISION_API_URL"
                    };
                    foreach (var raw in File.ReadAllLines(envPath))
                    {
                        var line = raw.Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                        var idx = line.IndexOf('=');
                        if (idx <= 0) continue;
                        var key = line[..idx].Trim();
                        var value = line[(idx + 1)..].Trim();
                        if (sensitiveKeys.Contains(key))
                            value = value.Length <= 3 ? "***" : value[..2] + "***" + value[^2..];
                        sb.AppendLine($"{key}={value}");
                    }
                }
                else
                {
                    sb.AppendLine("(未找到 .env 文件)");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(读取 .env 失败: {ex.Message})");
            }
            sb.AppendLine();

            // ─── 调试台记录（内存 + 磁盘 console.log 合并，按时间序） ───
            sb.AppendLine("【调试台记录】");
            var lines = new List<string>(_consoleViewModel.GetLogLines(instance.InstanceName));
            var logFile = Path.Combine(PathHelper.GetInstanceDir(instance.InstanceName), "console.log");
            if (File.Exists(logFile))
            {
                try
                {
                    foreach (var raw in File.ReadAllLines(logFile))
                    {
                        if (!string.IsNullOrWhiteSpace(raw))
                            lines.Add(raw);
                    }
                }
                catch (IOException)
                {
                    sb.AppendLine("(console.log 被运行中的实例占用，无法读取文件，已使用内存记录)");
                }
            }

            if (lines.Count == 0)
            {
                sb.AppendLine("(暂无调试台记录)");
            }
            else
            {
                foreach (var line in lines.Distinct())
                    sb.AppendLine(line);
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出调试日志",
                Filter = "文本文件 (*.txt)|*.txt",
                FileName = $"{instance.InstanceName}-调试日志-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                StatusMessage = $"📤 日志已导出到 {dialog.FileName}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 导出日志失败: {ex.Message}";
        }
    }

    /// <summary>删除指定实例的调试台记录（内存显示 + 磁盘 console.log，不影响实例数据）</summary>
    [RelayCommand]
    private void DeleteInstanceDebugLog(BotInstance instance)
    {
        try
        {
            _consoleViewModel.ClearLogFor(instance.InstanceName);

            var logFile = Path.Combine(PathHelper.GetInstanceDir(instance.InstanceName), "console.log");
            if (File.Exists(logFile))
            {
                File.Delete(logFile);
                StatusMessage = $"🗑️ 已删除实例 '{instance.InstanceName}' 的调试记录";
            }
            else
            {
                StatusMessage = $"🗑️ 已清空实例 '{instance.InstanceName}' 的调试台显示";
            }
        }
        catch (IOException)
        {
            StatusMessage = $"🗑️ 已清空屏幕显示；日志文件被运行中的实例占用，重启后自动重置";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 删除调试记录失败: {ex.Message}";
        }
    }

    /// <summary>一键删除所有实例的调试台记录（内存显示 + 磁盘 console.log）</summary>
    [RelayCommand]
    private void ClearAllDebugLogs()
    {
        try
        {
            var deleted = 0;
            foreach (var instance in Instances)
            {
                _consoleViewModel.ClearLogFor(instance.InstanceName);
                var logFile = Path.Combine(PathHelper.GetInstanceDir(instance.InstanceName), "console.log");
                try
                {
                    if (File.Exists(logFile))
                    {
                        File.Delete(logFile);
                        deleted++;
                    }
                }
                catch (IOException)
                {
                    // 运行中的实例文件被占用，跳过（下次启动自动覆盖）
                }
            }
            StatusMessage = deleted > 0
                ? $"🗑️ 已删除 {deleted} 个实例的调试记录"
                : "🗑️ 调试台记录已清空（无磁盘日志文件）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 一键删除调试记录失败: {ex.Message}";
        }
    }

    /// <summary>打开创建对话框</summary>
    [RelayCommand]
    private void OpenCreateDialog()
    {
        RequestCreateDialog?.Invoke();
    }

    /// <summary>打开编辑对话框</summary>
    [RelayCommand]
    private void EditInstance(BotInstance instance)
    {
        RequestEditDialog?.Invoke(instance);
    }
}
