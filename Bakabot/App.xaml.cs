using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Bakabot.Helpers;
using Bakabot.Services;
using Bakabot.ViewModels;
using Bakabot.Views.Pages;
using Bakabot.Views.Dialogs;
using Wpf.Ui.Appearance; // 引入 WPF UI 外观命名空间

namespace Bakabot;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        PathHelper.EnsureDirectories();

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        // 加载并应用设置
        var settingsService = Services.GetRequiredService<SettingsService>();
        var settings = settingsService.Settings;

        // 内置默认背景：先解压到数据目录 backgrounds，保证外观页画廊首次启动就能看到
        var bundledBackground = EnsureBundledBackground();

        // 应用主题
        ApplicationThemeManager.Apply(
            settings.IsDarkMode ? ApplicationTheme.Dark : ApplicationTheme.Light
        );

        // 应用全局透明度
        Application.Current.Resources["GlobalOverlayOpacity"] = settings.BgOpacity;

        // 应用按钮/主题强调色
        var appearanceViewModel = Services.GetRequiredService<AppearanceViewModel>();
        appearanceViewModel.ApplyAccentColorInternal(settings.AccentColor);

        var mainWindow = Services.GetRequiredService<MainWindow>();

        // 初始化 QQ 桥接（加载白名单、订阅实例输出、监听 NapCat 上报）
        Services.GetRequiredService<QQService>().Initialize();
        var napCatService = Services.GetRequiredService<NapCatService>();
        napCatService.Initialize();

        // 设置里开着 QQ 桥接且 NapCat 已下载时，随启动器自动拉起（失败不阻塞主界面）
        if (settings.QQEnabled && napCatService.IsAvailable)
            _ = StartNapCatSafelyAsync(napCatService);

        // 应用背景图（旧版内置预设图已移除，历史 pack:// 路径回退为无背景）
        var backgroundPath = settings.BackgroundImagePath;
        if (backgroundPath?.StartsWith("pack:") == true)
        {
            backgroundPath = string.Empty;
            settingsService.UpdateSettings(s => s.BackgroundImagePath = string.Empty);
        }
        if (string.IsNullOrEmpty(backgroundPath) && bundledBackground != null && !settings.BundledBackgroundApplied)
        {
            // 通过外观设置走正常流程（ViewModel 状态与窗口同步，恢复默认/清空才真正生效）
            backgroundPath = bundledBackground;
            appearanceViewModel.BackgroundImagePath = bundledBackground;
            settingsService.UpdateSettings(s =>
            {
                s.BundledBackgroundApplied = true;
            });
        }

        mainWindow.Loaded += async (s, ev) =>
        {
            mainWindow.UpdateBackground(backgroundPath ?? string.Empty);

            // 检查更新
            var updateService = Services.GetRequiredService<UpdateService>();
            await updateService.CheckUpdateAsync();
        };

        mainWindow.Show();
    }

    /// <summary>把内置默认背景解压到数据目录 backgrounds（已存在则直接返回路径）</summary>
    private static string? EnsureBundledBackground()
    {
        try
        {
            var dest = System.IO.Path.Combine(PathHelper.BackgroundsDir, "default_background.jpeg");
            if (System.IO.File.Exists(dest)) return dest;

            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Bakabot.Assets.default_background.jpeg");
            if (stream == null) return null;

            System.IO.Directory.CreateDirectory(PathHelper.BackgroundsDir);
            using var fs = new System.IO.FileStream(dest, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            stream.CopyTo(fs);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    private static async Task StartNapCatSafelyAsync(NapCatService napCatService)
    {
        try
        {
            await napCatService.StartAsync();
        }
        catch
        {
            // 自动启动失败时状态文本已由服务更新，不打断用户操作
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ─── 单例服务 ───
        services.AddSingleton<AuthInterceptor>();
        services.AddSingleton<EnvManager>();
        services.AddSingleton<DownloadService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<PatchService>();
        services.AddSingleton<ViaProxyService>();
        services.AddSingleton<InstanceManager>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<QQService>();
        services.AddSingleton<NapCatService>();

        // ─── ViewModels ───
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<CreateInstanceViewModel>();
        services.AddSingleton<ConsoleViewModel>();
        services.AddTransient<PluginsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AppearanceViewModel>();
        services.AddSingleton<QQViewModel>();
        services.AddSingleton<CommandPromptViewModel>();

        // ─── Pages（注册到 DI 以便 PageService 解析） ───
        services.AddTransient<HomePage>();
        services.AddTransient<ConsolePage>();
        services.AddTransient<PluginsPage>();
        services.AddTransient<MarketPage>();
        services.AddTransient<ExperiencePage>();
        services.AddTransient<DocsPage>();
        services.AddTransient<AppearancePage>();
        services.AddSingleton<SettingsPage>();
        services.AddTransient<QQPage>();
        services.AddTransient<CommandPromptPage>();

        // ─── 窗口 ───
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable disposable)
        {
            // 停止所有运行中的进程
            var instanceManager = Services.GetRequiredService<InstanceManager>();
            foreach (var pm in instanceManager.RunningProcesses.Values)
            {
                pm.Dispose();
            }
            // 停止 NapCat 协议端
            Services.GetRequiredService<NapCatService>().Dispose();
        }

        base.OnExit(e);
    }
}
