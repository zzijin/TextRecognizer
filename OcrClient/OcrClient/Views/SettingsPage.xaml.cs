using Microsoft.Extensions.Logging;
using OcrClient.UI.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace OcrClient.UI.Views;

public partial class SettingsPage : INavigableView<SettingsViewModel>
{
    private readonly ILogger<SettingsPage> _logger;
    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel, ILogger<SettingsPage> logger)
    {
        ViewModel = viewModel;
        _logger = logger;
        DataContext = this;
        InitializeComponent();
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
