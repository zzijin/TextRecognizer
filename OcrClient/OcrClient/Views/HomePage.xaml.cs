using OcrClient.UI.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace OcrClient.UI.Views;

public partial class HomePage : INavigableView<HomeViewModel>
{
    public HomeViewModel ViewModel { get; }

    public HomePage(HomeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
