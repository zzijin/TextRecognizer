using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OcrClient.Core.Models;
using OcrClient.Core.Services;
using OcrClient.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace OcrClient.UI.ViewModels;

public partial class HomeViewModel : ViewModel
{
    private readonly OcrApiClient _ocrClient;
    private readonly ServerProcessState _serverState;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private ObservableCollection<ImageFileItem> _images = [];

    [ObservableProperty]
    private ImageFileItem? _selectedImage;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _canEdit = true;

    public string ServerStatusText => _serverState.StatusText;
    public bool IsServerReady => _serverState.IsReady;
    public bool IsServerStarting => _serverState.IsStarting;
    public bool IsServerError => _serverState.HasError;

    public HomeViewModel(OcrApiClient ocrClient, ServerProcessState serverState)
    {
        _ocrClient = ocrClient;
        _serverState = serverState;

        _serverState.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ServerStatusText));
            OnPropertyChanged(nameof(IsServerReady));
            OnPropertyChanged(nameof(IsServerStarting));
            OnPropertyChanged(nameof(IsServerError));
        };
    }

    partial void OnIsBusyChanged(bool value)
    {
        CanEdit = !value;
    }

    partial void OnCompletedCountChanged(int value)
    {
        TotalCount = Images.Count;
        if (TotalCount > 0)
            Progress = (double)value / TotalCount * 100;
        StatusText = IsBusy ? $"Processing {value}/{TotalCount}..." : "Ready";
    }

    partial void OnSelectedImageChanged(ImageFileItem? value)
    {
        HasSelection = value is not null;
    }

    [RelayCommand]
    private void ImportImages()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.tiff;*.tif|All Files|*.*",
            Title = "Select Images for OCR"
        };

        if (dialog.ShowDialog() != true)
            return;

        foreach (var filePath in dialog.FileNames)
        {
            var thumbnail = CreateThumbnail(filePath);
            Images.Add(new ImageFileItem
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Thumbnail = thumbnail,
                Status = ImageFileStatus.Pending
            });
        }

        TotalCount = Images.Count;
        CompletedCount = 0;
        StatusText = $"{Images.Count} image(s) loaded";
    }

    [RelayCommand]
    private void ClearImages()
    {
        _cts?.Cancel();
        Images.Clear();
        SelectedImage = null;
        TotalCount = 0;
        CompletedCount = 0;
        Progress = 0;
        StatusText = "Ready";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task StartRecognitionAsync()
    {
        if (Images.Count == 0) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        CompletedCount = 0;
        TotalCount = Images.Count;
        StatusText = $"Processing 0/{TotalCount}...";
        Progress = 0;

        try
        {
            foreach (var item in Images)
            {
                token.ThrowIfCancellationRequested();

                item.Status = ImageFileStatus.Processing;
                item.ErrorMessage = null;

                try
                {
                    var base64 = ConvertImageToBase64(item.FilePath);
                    item.Result = await _ocrClient.CrossValidateAsync(base64, token);
                    item.Status = ImageFileStatus.Completed;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item.Status = ImageFileStatus.Error;
                    item.ErrorMessage = ex.Message;
                    item.Result = null;
                }

                CompletedCount++;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsBusy = false;
            StatusText = $"Done: {CompletedCount}/{TotalCount}";
        }
    }

    private static BitmapImage? CreateThumbnail(string filePath)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(filePath);
            bmp.DecodePixelWidth = 120;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static string ConvertImageToBase64(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return Convert.ToBase64String(bytes);
    }
}
