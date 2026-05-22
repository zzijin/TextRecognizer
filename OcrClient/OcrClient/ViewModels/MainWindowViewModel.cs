using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace OcrClient.UI.ViewModels;

public partial class MainWindowViewModel : ViewModel
{
    private readonly INavigationService _navigationService;
    private readonly ILogger<MainWindowViewModel> _logger;
    private bool _isInitialized;

    [ObservableProperty]
    private string _applicationTitle = "TextRecognizer";

    [ObservableProperty]
    private ObservableCollection<object> _navigationItems = [];

    [ObservableProperty]
    private ObservableCollection<object> _navigationFooter = [];

    [ObservableProperty]
    private ObservableCollection<MenuItem> _trayMenuItems = [];

    public MainWindowViewModel(INavigationService navigationService, ILogger<MainWindowViewModel> logger)
    {
        _navigationService = navigationService;
        _logger = logger;
        if (!_isInitialized)
            InitializeViewModel();
    }

    private void InitializeViewModel()
    {
        NavigationItems =
        [
            new NavigationViewItem
            {
                Content = "图形识别",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Home24 },
                TargetPageType = typeof(Views.HomePage),
            }
        ];

        NavigationFooter =
        [
            new NavigationViewItem
            {
                Content = "应用配置",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.SettingsPage),
            }
        ];

        TrayMenuItems = [new MenuItem { Header = "图形识别", Tag = "tray_home" }];

        _isInitialized = true;
    }
}
