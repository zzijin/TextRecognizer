using CommunityToolkit.Mvvm.ComponentModel;

namespace OcrClient.UI.Services;

public partial class ServerProcessState : ObservableObject
{
    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private bool _isStarting;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusText = "";
}
