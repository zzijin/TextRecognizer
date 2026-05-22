using OcrClient.Core.Models;
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

    private void TextBox_GotFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && tb.DataContext is CrossValidateGroup group)
            ViewModel.ShowCropPreview(group);
    }

    private void TextBox_LostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.HideCropPreview();
    }

    private void ItemsControl_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ItemsControl ic)
        {
            var sv = FindParent<System.Windows.Controls.ScrollViewer>(ic);
            if (sv is not null)
            {
                var args = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = System.Windows.UIElement.MouseWheelEvent
                };
                sv.RaiseEvent(args);
                e.Handled = true;
            }
        }
    }

    private static T? FindParent<T>(System.Windows.DependencyObject child) where T : System.Windows.DependencyObject
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T t) return t;
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
    }
}
