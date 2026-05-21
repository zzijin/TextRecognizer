using OcrClient.Core.Models;
using OcrClient.UI.ViewModels;
using System.Windows.Input;
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

    private void ConfirmTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && tb.DataContext is CrossValidateGroup group)
            ViewModel.ShowCropPreview(group);
    }
}
