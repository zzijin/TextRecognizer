using OcrClient.Core.Models;
using OcrClient.UI.ViewModels;
using System.Linq;
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
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.SelectAll();
            if (tb.DataContext is CrossValidateGroup group)
                ViewModel.ShowCropPreview(group);
        }
    }

    private void TextBox_LostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.HideCropPreview();
    }

    private void ConfirmationTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;

        if (sender is not System.Windows.Controls.TextBox tb
            || tb.DataContext is not CrossValidateGroup group) return;

        // Confirm current item
        group.IsConfirmed = true;
        ViewModel.NotifyAllConfirmed();
        e.Handled = true;

        // Find parent ItemsControl and jump to next unconfirmed item
        var ic = FindParent<System.Windows.Controls.ItemsControl>(tb);
        if (ic is null) return;

        var items = ic.Items.Cast<CrossValidateGroup>().ToList();
        int currentIdx = items.IndexOf(group);
        if (currentIdx < 0) return;

        // Remember current layout position relative to ItemsControl content
        double prevY = tb.TranslatePoint(new System.Windows.Point(0, 0), ic).Y;

        // Find next unconfirmed item after current
        for (int i = currentIdx + 1; i < items.Count; i++)
        {
            if (items[i].NeedsConfirmation)
            {
                var container = ic.ItemContainerGenerator.ContainerFromIndex(i);
                if (container is System.Windows.FrameworkElement fe)
                {
                    var nextTb = FindChild<System.Windows.Controls.TextBox>(fe);
                    if (nextTb is not null)
                    {
                        nextTb.Focus();
                        var sv = FindParent<System.Windows.Controls.ScrollViewer>(ic);
                        if (sv is not null)
                        {
                            double delta = nextTb.TranslatePoint(new System.Windows.Point(0, 0), ic).Y - prevY;
                            sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
                        }
                    }
                }
                break;
            }
        }
    }

    private static T? FindChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindChild<T>(child);
            if (found is not null) return found;
        }
        return null;
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
