using System;
using System.Diagnostics;
using System.Windows;
using Bakabot.Models;

namespace Bakabot.Views.Dialogs;

/// <summary>
/// 「发现新版本」提示窗。
///
/// 与旧版的区别：旧版会下载一个 .exe 并用批处理替换自身（那套流程来自 ARCbot 时代）。
/// 本项目的发布产物是 **zip 包**（内含 exe + README + 基础包），自替换对 zip 无意义
/// 且会破坏安装。因此现在只做两件事：展示更新说明、打开下载页由用户自行更新。
/// </summary>
public partial class UpdateDialog : Window
{
    /// <summary>用户是否勾选了"跳过此版本"</summary>
    public bool IsSkipChecked => SkipCheckBox.IsChecked == true;

    private readonly UpdateInfo _updateInfo;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _updateInfo = info;
        TitleText.Text = $"发现新版本：v{info.Version}";
        ReleaseNotesBox.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "（本次发布没有填写更新说明，点「去下载」可查看发布页面）"
            : info.ReleaseNotes;
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(_updateInfo.DownloadUrl)
            ? "https://github.com/fandad/Bakabot/releases/latest"
            : _updateInfo.DownloadUrl;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开下载页失败：{ex.Message}\n\n请手动访问：\n{url}", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 点了去下载就关掉，不再自动更新（由用户自行下载解压覆盖）
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
