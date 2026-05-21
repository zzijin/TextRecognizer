using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OcrClient.Core.Models;
using OcrClient.Core.Services;
using OcrClient.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
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
    private string _statusText = "就绪";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _canEdit = true;

    [ObservableProperty]
    private RecognitionMode _selectedMode = RecognitionMode.CrossValidate;

    public List<RecognitionModeOption> ModeOptions { get; } =
    [
        new(RecognitionMode.CrossValidate, "交叉验证（三模型）"),
        new(RecognitionMode.ServerRec, "PP-OCRv5_server_rec"),
        new(RecognitionMode.MobileRec, "PP-OCRv5_mobile_rec"),
        new(RecognitionMode.EnMobileRec, "en_PP-OCRv5_mobile_rec"),
    ];

    public bool IsCrossValidate => SelectedMode == RecognitionMode.CrossValidate;

    public string SingleModelLabel => SelectedMode switch
    {
        RecognitionMode.ServerRec => "PP-OCRv5_server_rec",
        RecognitionMode.MobileRec => "PP-OCRv5_mobile_rec",
        RecognitionMode.EnMobileRec => "en_PP-OCRv5_mobile_rec",
        _ => ""
    };

    partial void OnSelectedModeChanged(RecognitionMode value)
    {
        OnPropertyChanged(nameof(IsCrossValidate));
        OnPropertyChanged(nameof(SingleModelLabel));
        OnPropertyChanged(nameof(SingleResultItems));
        RebuildCachedGroups();
    }

    private List<CrossValidateGroup>? _cachedGroups;

    partial void OnSelectedImageChanged(ImageFileItem? value)
    {
        HasSelection = value is not null;
        OnPropertyChanged(nameof(SingleResultItems));
        RebuildCachedGroups();
    }

    private void RebuildCachedGroups()
    {
        _cachedGroups = (SelectedImage?.Result is { } r && IsCrossValidate)
            ? CrossValidateAligner.Align(r) : null;

        if (_cachedGroups is not null)
        {
            var imagePath = SelectedImage?.FilePath ?? "";
            double scale = 1.0;
            if (!string.IsNullOrEmpty(imagePath))
            {
                try
                {
                    var bmp = new BitmapImage(new Uri(imagePath));
                    if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                        scale = 1024.0 / Math.Max(bmp.PixelWidth, bmp.PixelHeight);
                }
                catch { scale = 1.0; }
            }

            foreach (var g in _cachedGroups)
            {
                g.SourceImagePath = imagePath;
                g.ImageScale = scale;
                g.ToggleConfirmCommand = new RelayCommand<CrossValidateGroup>(group =>
                {
                    if (group is null) return;
                    group.IsConfirmed = !group.IsConfirmed;
                    OnPropertyChanged(nameof(AllConfirmed));
                }, _ => true);

                var capturedGroup = g;
                g.TogglePopupCommand = new RelayCommand(() =>
                {
                    if (IsCropPreviewVisible)
                        HideCropPreview();
                    else
                        ShowCropPreview(capturedGroup);
                });
            }
        }

        OnPropertyChanged(nameof(CrossValidateGroups));
        OnPropertyChanged(nameof(AllConfirmed));
    }

    public List<CrossValidateGroup>? CrossValidateGroups => _cachedGroups;

    [ObservableProperty]
    private ImageSource? _cropPreviewSource;

    [ObservableProperty]
    private bool _isCropPreviewVisible;

    public bool AllConfirmed =>
        CrossValidateGroups is { Count: > 0 } g && g.All(x => x.IsConfirmed);

    public List<OcrItem>? SingleResultItems => SelectedImage?.Result switch
    {
        null => null,
        _ => SelectedMode switch
        {
            RecognitionMode.ServerRec => SelectedImage.Result.ServerRec?.Items,
            RecognitionMode.MobileRec => SelectedImage.Result.MobileRec?.Items,
            RecognitionMode.EnMobileRec => SelectedImage.Result.EnMobileRec?.Items,
            _ => null
        }
    };

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
        StatusText = IsBusy ? $"识别中 {value}/{TotalCount}..." : "就绪";
    }

    [RelayCommand]
    private void ImportImages()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.tiff;*.tif|所有文件|*.*",
            Title = "选择 OCR 识别图片"
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
        StatusText = $"已加载 {Images.Count} 张图片";
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
        StatusText = "就绪";
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
        StatusText = $"识别中 0/{TotalCount}...";
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
                    item.Result = SelectedMode switch
                    {
                        RecognitionMode.CrossValidate => await _ocrClient.CrossValidateAsync(base64, token),
                        RecognitionMode.ServerRec => WrapSingle(await _ocrClient.RecognizeServerAsync(base64, token), RecognitionMode.ServerRec),
                        RecognitionMode.MobileRec => WrapSingle(await _ocrClient.RecognizeMobileAsync(base64, token), RecognitionMode.MobileRec),
                        RecognitionMode.EnMobileRec => WrapSingle(await _ocrClient.RecognizeEnMobileAsync(base64, token), RecognitionMode.EnMobileRec),
                        _ => throw new InvalidOperationException("Unknown recognition mode")
                    };
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
            StatusText = $"完成: {CompletedCount}/{TotalCount}";
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

    [RelayCommand]
    private void ConfirmGroup(CrossValidateGroup? group)
    {
        if (group is null) return;
        group.IsConfirmed = !group.IsConfirmed;
        OnPropertyChanged(nameof(AllConfirmed));
    }

    public void ShowCropPreview(CrossValidateGroup group)
    {
        CropPreviewSource = CreateCropPreview(group);
        if (CropPreviewSource is not null)
            IsCropPreviewVisible = true;
    }

    public void HideCropPreview()
    {
        IsCropPreviewVisible = false;
        CropPreviewSource = null;
    }

    [RelayCommand]
    private void ExportResults()
    {
        if (_cachedGroups is null) return;

        var imageName = Path.GetFileNameWithoutExtension(SelectedImage?.FilePath ?? "result");
        var dialog = new SaveFileDialog
        {
            Filter = "文本文件|*.txt",
            Title = "导出确认结果",
            FileName = $"{imageName}_ocr.txt"
        };
        if (dialog.ShowDialog() != true) return;

        var lines = _cachedGroups
            .Where(g => g.IsConfirmed && !string.IsNullOrEmpty(g.ConfirmedText))
            .Select(g => g.ConfirmedText);
        File.WriteAllLines(dialog.FileName, lines);
    }

    private static CrossValidateResult WrapSingle(OcrSingleResult result, RecognitionMode mode)
    {
        var wrapper = new CrossValidateResult();
        switch (mode)
        {
            case RecognitionMode.ServerRec: wrapper.ServerRec = result; break;
            case RecognitionMode.MobileRec: wrapper.MobileRec = result; break;
            case RecognitionMode.EnMobileRec: wrapper.EnMobileRec = result; break;
        }
        return wrapper;
    }

    private static BitmapSource? CreateCropPreview(CrossValidateGroup group)
    {
        if (string.IsNullOrEmpty(group.SourceImagePath)) return null;
        try
        {
            var bmp = new BitmapImage(new Uri(group.SourceImagePath));
            int x = group.ScaledUnionRect.X, y = group.ScaledUnionRect.Y;
            int w = group.ScaledUnionRect.Width, h = group.ScaledUnionRect.Height;
            if (w <= 0 || h <= 0) return null;
            if (x < 0) x = 0; if (y < 0) y = 0;
            if (x + w > bmp.PixelWidth) w = bmp.PixelWidth - x;
            if (y + h > bmp.PixelHeight) h = bmp.PixelHeight - y;
            if (w <= 0 || h <= 0) return null;
            return new CroppedBitmap(bmp, new Int32Rect(x, y, w, h));
        }
        catch { return null; }
    }

    private static string ConvertImageToBase64(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return Convert.ToBase64String(bytes);
    }
}
