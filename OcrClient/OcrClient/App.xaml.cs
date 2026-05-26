using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OcrClient.Core.Models;
using OcrClient.Core.Services;
using OcrClient.UI.Services;
using OcrClient.UI.ViewModels;
using OcrClient.UI.Views;
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
        // Configuration — load first so logging can use it
        var configService = new AppConfigService(Microsoft.Extensions.Logging.Abstractions.NullLogger<AppConfigService>.Instance);
        services.AddSingleton(configService);
        services.AddSingleton(configService.Config);

        services.AddLogging(builder => builder.AddClientLogging(configService.Config.Logging));

        services.AddSingleton<ApplicationHostService>();
        services.AddHostedService(sp => sp.GetRequiredService<ApplicationHostService>());

        // Navigation
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<INavigationWindow, MainWindow>();
        services.AddNavigationViewPageProvider();

        // Windows & Pages
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<HomePage>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<SettingsViewModel>();

        // Core services
        services.AddSingleton<Services.ServerProcessState>();
        services.AddHttpClient<Core.Services.OcrApiClient>((sp, client) =>
        {
            var config = sp.GetRequiredService<AppConfig>();
            client.BaseAddress = new Uri(config.Server.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(config.Server.RequestTimeoutSeconds);
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
        await _host.StopAsync();
        _host.Dispose();
    }
}
