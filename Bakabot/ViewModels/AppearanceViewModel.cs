using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Bakabot.Helpers;
using Bakabot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Appearance;

namespace Bakabot.ViewModels;

/// <summary>
/// 外观设置页 ViewModel：暗色模式、背景遮罩、自定义背景（拖入/选择/切换）、按钮强调色。
/// </summary>
public partial class AppearanceViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private bool _isDarkMode;

    [ObservableProperty]
    private double _bgOpacity;

    [ObservableProperty]
    private string _backgroundImagePath;

    [ObservableProperty]
    private string _selectedAccentColor;

    /// <summary>背景图库：持久化在数据目录 backgrounds 下的所有图片</summary>
    public ObservableCollection<BackgroundItem> GalleryImages { get; } = new();

    /// <summary>可选的按钮/主题强调色（基础色板）</summary>
    public ObservableCollection<AccentColorOption> AccentColors { get; } = new()
    {
        new AccentColorOption("默认蓝", "#0078D7"),
        new AccentColorOption("红色", "#E81123"),
        new AccentColorOption("绿色", "#107C10"),
        new AccentColorOption("紫色", "#886CE4"),
        new AccentColorOption("橙色", "#FF8C00"),
        new AccentColorOption("粉色", "#E3008C"),
        new AccentColorOption("青色", "#00B294"),
        new AccentColorOption("金色", "#FFB900"),
    };

    public AppearanceViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;

        var settings = _settingsService.Settings;
        _isDarkMode = settings.IsDarkMode;
        _bgOpacity = settings.BgOpacity;
        // 旧版内置预设图（pack://）已移除，历史设置直接回退为无背景
        _backgroundImagePath = settings.BackgroundImagePath?.StartsWith("pack:") == true
            ? string.Empty
            : settings.BackgroundImagePath ?? string.Empty;
        _selectedAccentColor = settings.AccentColor ?? string.Empty;

        foreach (var option in AccentColors)
            option.IsSelected = string.Equals(option.Hex, SelectedAccentColor, StringComparison.OrdinalIgnoreCase);
        if (!AccentColors.Any(o => o.IsSelected) && string.IsNullOrEmpty(SelectedAccentColor))
            AccentColors[0].IsSelected = true;

        RefreshGallery();
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        ApplicationThemeManager.Apply(
            value ? ApplicationTheme.Dark : ApplicationTheme.Light
        );
        _settingsService.UpdateSettings(s => s.IsDarkMode = value);
        ApplyAccentColorInternal(SelectedAccentColor); // 主题切换后重新应用强调色
    }

    partial void OnBgOpacityChanged(double value)
    {
        Application.Current.Resources["GlobalOverlayOpacity"] = value;
        _settingsService.UpdateSettings(s => s.BgOpacity = value);
    }

    partial void OnBackgroundImagePathChanged(string value)
    {
        if (Application.Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.UpdateBackground(value);
        }
        _settingsService.UpdateSettings(s => s.BackgroundImagePath = value);
        RefreshGallerySelection();
    }

    /// <summary>刷新背景图库列表</summary>
    public void RefreshGallery()
    {
        GalleryImages.Clear();
        try
        {
            var files = Directory.GetFiles(PathHelper.BackgroundsDir, "*.*")
                .Where(f => IsImageFile(f))
                .OrderBy(f => f);
            foreach (var file in files)
                GalleryImages.Add(new BackgroundItem(file));
        }
        catch
        {
            // 目录读取失败则保持空图库
        }
        RefreshGallerySelection();
    }

    private void RefreshGallerySelection()
    {
        foreach (var item in GalleryImages)
            item.IsSelected = !string.IsNullOrEmpty(BackgroundImagePath)
                && string.Equals(item.FilePath, BackgroundImagePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>将外部图片复制到背景图库并立即应用</summary>
    public string ImportAndApplyImage(string sourcePath)
    {
        if (!IsImageFile(sourcePath))
            throw new InvalidOperationException("不支持的图片格式，请使用 jpg / jpeg / png / bmp / webp / gif");

        var destPath = Path.Combine(PathHelper.BackgroundsDir, Path.GetFileName(sourcePath));
        // 同名文件避免重复复制（同路径直接使用）
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
        {
            destPath = GetUniquePath(destPath);
            File.Copy(sourcePath, destPath, true);
        }

        BackgroundImagePath = destPath;
        RefreshGallery();
        return destPath;
    }

    /// <summary>切换背景到图库中的某张图</summary>
    [RelayCommand]
    private void ApplyGalleryImage(BackgroundItem? item)
    {
        if (item != null)
            BackgroundImagePath = item.FilePath;
    }

    /// <summary>清除背景（恢复默认 Mica 材质）</summary>
    [RelayCommand]
    private void ClearBackground()
    {
        BackgroundImagePath = string.Empty;
    }

    /// <summary>通过文件对话框选择背景图</summary>
    [RelayCommand]
    private void SelectImageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif",
            Title = "选择背景图片"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                ImportAndApplyImage(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入背景失败: {ex.Message}");
            }
        }
    }

    /// <summary>选择按钮强调色</summary>
    [RelayCommand]
    private void ApplyAccentColor(AccentColorOption? option)
    {
        if (option == null) return;

        foreach (var o in AccentColors)
            o.IsSelected = ReferenceEquals(o, option);

        SelectedAccentColor = option.Hex;
        _settingsService.UpdateSettings(s => s.AccentColor = option.Hex);
        ApplyAccentColorInternal(option.Hex);
    }

    /// <summary>启动时应用已保存的强调色</summary>
    public void ApplyAccentColorInternal(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var theme = IsDarkMode ? ApplicationTheme.Dark : ApplicationTheme.Light;
            RefreshThemeResources(theme);
            ApplicationAccentColorManager.Apply(color, theme, true);
            OverrideAccentBrushes(color, theme);
        }
        catch
        {
            // 颜色解析失败时保持系统默认色
        }
    }

    /// <summary>
    /// 自动刷新：重新加载当前主题的字典，强制所有 DynamicResource 重新解析，
    /// 效果等同于手动切换一次深浅色，换色后无需任何操作即可全界面生效。
    /// （源码确认：ApplicationThemeManager.Apply 对同主题重复调用不是 no-op，
    /// 会无条件重建主题字典实例；updateAccent 必须传 false，否则强调色会被系统色重置）
    /// 注意：若个别控件仍未换色，重启启动器后将完全生效。
    /// </summary>
    private static void RefreshThemeResources(ApplicationTheme theme)
    {
        try
        {
            ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
        }
        catch
        {
            // 刷新失败时退回仅覆盖资源的方式，不至于影响换色主流程
        }
    }

    /// <summary>
    /// WPF-UI 按钮模板的换色杠杆（源码确认）：
    /// - Info 外观内联 SolidColorBrush，Color 绑定 {DynamicResource PaletteLightBlueColor}（固定浅蓝，与强调色无关）→ 覆盖该 Color 键即可；
    /// - Primary 外观用 AccentButtonBackground 系列画刷（DynamicResource）→ 覆盖该系列；
    /// - 其余强调控件用 AccentFillColor* / SystemAccentColor* 系列。
    /// 模板引用均为 DynamicResource，覆盖后立即生效；对 StaticResource 持有者则通过修改共享画刷实例补上。
    /// </summary>
    private static void OverrideAccentBrushes(Color accent, ApplicationTheme theme)
    {
        // 暗色主题悬停/按压往白色提亮，亮色主题往黑色压暗（模拟 WinUI 派生色）
        var mixTarget = theme == ApplicationTheme.Dark ? Colors.White : Colors.Black;
        var hover = Blend(accent, mixTarget, 0.15);
        var pressed = Blend(accent, mixTarget, 0.30);

        var res = Application.Current.Resources;

        // Info 按钮（启动/发送/下载/导入插件等）：唯一杠杆是这个 Color 键
        res["PaletteLightBlueColor"] = accent;

        // Primary 按钮（一键下载等）
        SetBrush("AccentButtonBackground", accent, 1.0);
        SetBrush("AccentButtonBackgroundPointerOver", hover, 0.9);
        SetBrush("AccentButtonBackgroundPressed", pressed, 0.8);

        // 其余强调填充/文本画刷（开关、进度条、选中色等）
        SetBrush("AccentFillColorDefaultBrush", accent, 1.0);
        SetBrush("AccentFillColorSecondaryBrush", accent, 0.9);
        SetBrush("AccentFillColorTertiaryBrush", accent, 0.8);
        SetBrush("SystemAccentColorPrimaryBrush", accent, 1.0);
        SetBrush("SystemAccentColorSecondaryBrush", hover, 0.9);
        SetBrush("SystemAccentColorTertiaryBrush", pressed, 0.8);
        SetBrush("SystemAccentColorBrush", accent, 1.0);
        SetBrush("AccentTextFillColorPrimaryBrush", hover, 1.0);
        SetBrush("AccentTextFillColorSecondaryBrush", hover, 1.0);
        SetBrush("AccentTextFillColorTertiaryBrush", pressed, 1.0);

        // App.xaml 自定义的强调画刷（卡片悬停边框等）
        SetBrush("AccentBrush", accent, 1.0);
        SetBrush("AccentHoverBrush", hover, 1.0);
    }

    /// <summary>优先修改共享画刷实例（StaticResource 持有者也立即更新），找不到才新建写入应用级资源</summary>
    private static void SetBrush(string key, Color color, double opacity)
    {
        var res = Application.Current.Resources;
        var mutated = false;

        if (res[key] is SolidColorBrush own && !own.IsFrozen)
        {
            own.Color = color;
            own.Opacity = opacity;
            mutated = true;
        }

        foreach (var dict in res.MergedDictionaries)
        {
            if (dict[key] is SolidColorBrush themeBrush && !themeBrush.IsFrozen)
            {
                themeBrush.Color = color;
                themeBrush.Opacity = opacity;
                mutated = true;
            }
        }

        if (!mutated)
            res[key] = new SolidColorBrush(color) { Opacity = opacity };
    }

    private static Color Blend(Color source, Color target, double amount)
    {
        byte Mix(byte a, byte b) => (byte)(a + (b - a) * amount);
        return Color.FromRgb(Mix(source.R, target.R), Mix(source.G, target.G), Mix(source.B, target.B));
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif";
    }
}

/// <summary>背景图库条目</summary>
public partial class BackgroundItem : ObservableObject
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    [ObservableProperty]
    private bool _isSelected;

    public BackgroundItem(string filePath)
    {
        FilePath = filePath;
    }
}

/// <summary>强调色选项</summary>
public partial class AccentColorOption : ObservableObject
{
    public string Name { get; }
    public string Hex { get; }

    [ObservableProperty]
    private bool _isSelected;

    public AccentColorOption(string name, string hex)
    {
        Name = name;
        Hex = hex;
    }
}
