using CommunityToolkit.Mvvm.ComponentModel;
using OcrClient.Core.Models;
using System.Windows.Media;

namespace OcrClient.UI.ViewModels;

public enum ImageFileStatus
{
    Pending,
    Processing,
    Completed,
    Error
}

public partial class ImageFileItem : ObservableObject
{
    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private ImageFileStatus _status = ImageFileStatus.Pending;

    [ObservableProperty]
    private CrossValidateResult? _result;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasResult => Result is not null;

    partial void OnResultChanged(CrossValidateResult? value)
    {
        OnPropertyChanged(nameof(HasResult));
    }
}
