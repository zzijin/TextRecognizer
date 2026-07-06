using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OcrClient.Core.Models;
using OcrClient.Core.Onnx;
using OcrClient.Core.Services;
using OcrClient.UI.Services;
using OcrClient.UI.ViewModels;
using OcrClient.UI.Views;
using System.IO;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace OcrClient.UI;

public partial class App : Application
{
    private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureServices((context, services) =>
        {
            ConfigureServices(services);
        })
        .Build();

    private static void ConfigureServices(IServiceCollection services)
    {
        // 配置 — 先加载以便日志系统可以使用
        var configService = new AppConfigService(Microsoft.Extensions.Logging.Abstractions.NullLogger<AppConfigService>.Instance);
        services.AddSingleton(configService);
        services.AddSingleton(configService.Config);

        services.AddLogging(builder => builder.AddClientLogging(configService.Config.Logging));

        services.AddSingleton<ApplicationHostService>();
        services.AddHostedService(sp => sp.GetRequiredService<ApplicationHostService>());

        // 导航
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<INavigationWindow, MainWindow>();
        services.AddNavigationViewPageProvider();

        // 窗口和页面
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<HomePage>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<SettingsViewModel>();

        // 核心服务
        services.AddSingleton<Services.ServerProcessState>();
        services.AddHttpClient<Core.Services.OcrApiClient>((sp, client) =>
        {
            var config = sp.GetRequiredService<AppConfig>();
            client.BaseAddress = new Uri(config.Server.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(config.Server.RequestTimeoutSeconds);
        });

        services.AddHttpClient<Core.Services.BaiduOcrClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        // ONNX C# 引擎（仅当引擎来源为 onnx_csharp 时注册）
        services.AddSingleton<OnnxOcrEngine>(sp =>
        {
            var config = sp.GetRequiredService<AppConfig>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OnnxOcrEngine>>();

            // 解析模型路径
            var serverDir = ApplicationHostService.ResolveServiceDirectory(config.OcrService.ServiceDirectory);
            var onnxDir = Path.IsPathRooted(config.OcrService.OnnxModelsDir)
                ? config.OcrService.OnnxModelsDir
                : Path.Combine(serverDir, config.OcrService.OnnxModelsDir);
            var charDictDir = Path.IsPathRooted(config.OcrService.CharDictDir)
                ? config.OcrService.CharDictDir
                : Path.Combine(serverDir, config.OcrService.CharDictDir);

            return new OnnxOcrEngine(onnxDir, charDictDir, config.Server.OnnxCsharpGpuId, logger);
        });
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        await _host.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        _host.Dispose();
    }
}
