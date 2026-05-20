using OcrClient.UI.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace OcrClient.UI.Views;

public partial class SettingsPage : INavigableView<SettingsViewModel>
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
