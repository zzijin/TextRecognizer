using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Abstractions.Controls;

namespace OcrClient.UI.ViewModels;

public class ViewModel : ObservableObject, INavigationAware
{
    public virtual Task OnNavigatedToAsync()
    {
        OnNavigatedTo();
        return Task.CompletedTask;
    }

    public virtual void OnNavigatedTo() { }

    public virtual Task OnNavigatedFromAsync()
    {
        OnNavigatedFrom();
        return Task.CompletedTask;
    }

    public virtual void OnNavigatedFrom() { }
}
