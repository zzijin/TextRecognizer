using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace OcrClient.UI.ViewModels;

public partial class MainWindowViewModel : ViewModel
{
    private bool _isInitialized;

    [ObservableProperty]
    private string _applicationTitle = "NumberRecognizer";

    [ObservableProperty]
    private ObservableCollection<object> _navigationItems = [];

    [ObservableProperty]
    private ObservableCollection<object> _navigationFooter = [];

    [ObservableProperty]
    private ObservableCollection<MenuItem> _trayMenuItems = [];

    public MainWindowViewModel(INavigationService navigationService)
    {
        if (!_isInitialized)
            InitializeViewModel();
    }

    private void InitializeViewModel()
    {
        NavigationItems =
        [
            new NavigationViewItem
            {
                Content = "Home",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Home24 },
                TargetPageType = typeof(Views.HomePage),
            }
        ];

        NavigationFooter =
        [
            new NavigationViewItem
            {
                Content = "Settings",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.SettingsPage),
            }
        ];

        TrayMenuItems = [new MenuItem { Header = "Home", Tag = "tray_home" }];

        _isInitialized = true;
    }
}
