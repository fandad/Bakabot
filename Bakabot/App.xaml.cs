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

        // --mcp-stdio：MCP 客户端以 Bakabot.exe --mcp-stdio 拉起时不显示界面，
        // 直接以标准 MCP 协议连接本机正在运行的主启动器。
        var isMcpStdio = e.Args.Any(a => string.Equals(a, "--mcp-stdio", StringComparison.OrdinalIgnoreCase));

        PathHelper.EnsureDirectories();

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        if (isMcpStdio)
        {
            var stdioSettingsService = Services.GetRequiredService<SettingsService>();
            // 不能同步阻塞 UI 线程等待 stdio（会死锁）：先返回启动消息循环，
            // stdio 循环结束后在 UI 线程上退出。
            _ = RunStdioAndExitAsync(stdioSettingsService);
            return;
        }

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
        var oneBotService = Services.GetRequiredService<OneBot11ServerService>();
        oneBotService.Initialize();

        // MCP 本地接口（仅本机，供外部 AI 通过 MCP 调用机器人）
        if (settings.MCPEnabled || settings.MCPHttpEnabled)
            Services.GetRequiredService<LocalApiService>().Start();

        // MCP 云端 HTTP 接口（启动器进程内托管 /mcp）
        if (settings.MCPHttpEnabled)
            _ = StartMcpHostSafelyAsync();

        // 设置里开着 OneBot 反向接入时随启动器自动拉起；
        // 否则 QQ 桥接开着且 NapCat 已下载时才自动拉起 NapCat（失败均不阻塞主界面）
        if (settings.OneBotEnabled)
            _ = StartOneBotSafelyAsync(oneBotService);
        else if (settings.QQEnabled && napCatService.IsAvailable)
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

    private static async Task StartOneBotSafelyAsync(OneBot11ServerService oneBotService)
    {
        try
        {
            await oneBotService.StartAsync();
        }
        catch
        {
            // 自动启动失败时状态文本已由服务更新，不打断用户操作
        }
    }

    private static async Task StartMcpHostSafelyAsync()
    {
        try
        {
            await Services.GetRequiredService<McpHostService>().StartAsync();
        }
        catch
        {
            // 自动启动失败不打断主界面
        }
    }

    private static async Task RunStdioAndExitAsync(SettingsService settingsService)
    {
        try
        {
            var exitCode = await Task.Run(() => McpHostService.RunStdioAsync(settingsService));
            Application.Current.Shutdown(exitCode);
        }
        catch
        {
            Application.Current.Shutdown(1);
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
        services.AddSingleton<OneBot11ServerService>();
        services.AddSingleton<LocalApiService>();
        services.AddSingleton<McpHostService>();

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
        services.AddSingleton<OneBotViewModel>();

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
        services.AddTransient<OneBotPage>();

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
            // 停止外部 OneBot11 反向接入
            Services.GetRequiredService<OneBot11ServerService>().Dispose();
            // 停止 MCP 本地接口
            Services.GetRequiredService<LocalApiService>().Dispose();
            // 停止 MCP 云端接口
            try { Services.GetRequiredService<McpHostService>().StopAsync().GetAwaiter().GetResult(); } catch { }
        }

        base.OnExit(e);
    }
}
