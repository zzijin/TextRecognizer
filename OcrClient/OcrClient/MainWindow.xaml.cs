using OcrClient.UI.ViewModels;
using OcrClient.UI.Views;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace OcrClient.UI;

public partial class MainWindow : FluentWindow, INavigationWindow
{
    private readonly INavigationViewPageProvider _pageProvider;
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(MainWindowViewModel viewModel, INavigationViewPageProvider pageProvider)
    {
        ViewModel = viewModel;
        _pageProvider = pageProvider;
        DataContext = this;
        InitializeComponent();
        SetPageService(_pageProvider);
    }

    public INavigationView GetNavigation() => RootNavigation;

    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) =>
        RootNavigation.SetPageProviderService(navigationViewPageProvider);

    public void SetServiceProvider(IServiceProvider serviceProvider) { }

    public void ShowWindow() => Show();

    public void CloseWindow() => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
