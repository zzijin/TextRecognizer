using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;
using OcrClient.UI.Services;
using OcrClient.UI.ViewModels;
using OcrClient.UI.Views;
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
        services.AddHostedService<ApplicationHostService>();

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
        services.AddSingleton<Core.Services.OcrApiClient>();
        services.AddHttpClient<Core.Services.OcrApiClient>(client =>
        {
            client.BaseAddress = new Uri("http://localhost:8080");
            client.Timeout = TimeSpan.FromMinutes(15);
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
