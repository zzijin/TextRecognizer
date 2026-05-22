using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using OcrClient.Core.Models;
using OcrClient.Core.Services;
using OcrClient.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace OcrClient.UI.ViewModels;

public partial class HomeViewModel : ViewModel
{
    private readonly OcrApiClient _ocrClient;
    private readonly ServerProcessState _serverState;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly ApplicationHostService _appHost;
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
    private string _elapsedTime = "";

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
                        scale = 1.0;  // server returns boxes in original image coords, no scaling needed
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

    public bool CanStartRecognition => _serverState.IsReady && !IsBusy && Images.Count > 0;

    [RelayCommand]
    private void RestartServer()
    {
        _serverState.StatusText = "Restarting...";
        _serverState.IsReady = false;
        _serverState.IsStarting = true;
        _serverState.HasError = false;
        OnPropertyChanged(nameof(CanStartRecognition));
        _appHost.Restart();
    }

    public string ServerStatusText => _serverState.StatusText;
    public bool IsServerReady => _serverState.IsReady;
    public bool IsServerStarting => _serverState.IsStarting;
    public bool IsServerError => _serverState.HasError;

    public HomeViewModel(OcrApiClient ocrClient, ServerProcessState serverState, ILogger<HomeViewModel> logger, ApplicationHostService appHost)
    {
        _ocrClient = ocrClient;
        _serverState = serverState;
        _logger = logger;
        _appHost = appHost;

        _serverState.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ServerStatusText));
            OnPropertyChanged(nameof(IsServerReady));
            OnPropertyChanged(nameof(IsServerStarting));
            OnPropertyChanged(nameof(IsServerError));
            OnPropertyChanged(nameof(CanStartRecognition));
        };
    }

    partial void OnIsBusyChanged(bool value)
    {
        CanEdit = !value;
        OnPropertyChanged(nameof(CanStartRecognition));
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

        var existing = Images.Select(i => i.FilePath).ToHashSet();
        int added = 0, skipped = 0;
        foreach (var filePath in dialog.FileNames)
        {
            if (existing.Contains(filePath)) { skipped++; continue; }
            var thumbnail = CreateThumbnail(filePath);
            Images.Add(new ImageFileItem
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Thumbnail = thumbnail,
                Status = ImageFileStatus.Pending
            });
            added++;
        }

        TotalCount = Images.Count;
        CompletedCount = 0;
        OnPropertyChanged(nameof(CanStartRecognition));
        StatusText = skipped > 0
            ? $"已加载 {added} 张，跳过 {skipped} 张重复"
            : $"已加载 {Images.Count} 张图片";
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
        OnPropertyChanged(nameof(CanStartRecognition));
    }

    [RelayCommand]
    private async Task StartRecognitionAsync()
    {
        if (Images.Count == 0 || !CanStartRecognition) return;

        _logger.LogInformation("Recognition started: {Count} images, mode={Mode}", Images.Count, SelectedMode);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        CompletedCount = 0;
        TotalCount = Images.Count;
        StatusText = $"识别中 0/{TotalCount}...";
        Progress = 0;
        int skipped = 0;

        var startTime = DateTime.Now;
        var timerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!timerCts.Token.IsCancellationRequested)
            {
                var elapsed = DateTime.Now - startTime;
                ElapsedTime = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
                try { await Task.Delay(1000, timerCts.Token); } catch { break; }
            }
        }, timerCts.Token);

        try
        {
            foreach (var item in Images)
            {
                token.ThrowIfCancellationRequested();

                // Skip if already recognized in this mode
                if (item.CompletedModes.Contains(SelectedMode))
                {
                    skipped++;
                    CompletedCount++;
                    continue;
                }

                _logger.LogInformation("Processing: {FileName}", item.FileName);
                item.Status = ImageFileStatus.Processing;
                item.ErrorMessage = null;

                try
                {
                    var base64 = ConvertImageToBase64(item.FilePath);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    switch (SelectedMode)
                    {
                        case RecognitionMode.CrossValidate:
                            var cvResult = await _ocrClient.CrossValidateAsync(base64, token);
                            item.Result = cvResult;
                            item.CompletedModes.Add(RecognitionMode.CrossValidate);
                            item.CompletedModes.Add(RecognitionMode.ServerRec);
                            item.CompletedModes.Add(RecognitionMode.MobileRec);
                            item.CompletedModes.Add(RecognitionMode.EnMobileRec);
                            break;
                        case RecognitionMode.ServerRec:
                            item.Result = MergeResult(item.Result, RecognitionMode.ServerRec, await _ocrClient.RecognizeServerAsync(base64, token));
                            item.CompletedModes.Add(RecognitionMode.ServerRec);
                            break;
                        case RecognitionMode.MobileRec:
                            item.Result = MergeResult(item.Result, RecognitionMode.MobileRec, await _ocrClient.RecognizeMobileAsync(base64, token));
                            item.CompletedModes.Add(RecognitionMode.MobileRec);
                            break;
                        case RecognitionMode.EnMobileRec:
                            item.Result = MergeResult(item.Result, RecognitionMode.EnMobileRec, await _ocrClient.RecognizeEnMobileAsync(base64, token));
                            item.CompletedModes.Add(RecognitionMode.EnMobileRec);
                            break;
                    }
                    sw.Stop();
                    _logger.LogInformation("Done: {FileName} in {ElapsedMs}ms, {Count} items",
                        item.FileName, sw.ElapsedMilliseconds,
                        item.Result?.ServerRec?.Count ?? item.Result?.MobileRec?.Count ?? item.Result?.EnMobileRec?.Count ?? 0);

                    if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                        LogResultDetails(item);
                    item.Status = ImageFileStatus.Completed;
                    if (item == SelectedImage)
                    {
                        RebuildCachedGroups();
                        OnPropertyChanged(nameof(SingleResultItems));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed: {FileName}", item.FileName);
                    item.Status = ImageFileStatus.Error;
                    item.ErrorMessage = ex.Message;
                    item.Result = null;
                }

                CompletedCount++;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Recognition cancelled");
        }
        finally
        {
            timerCts.Cancel();
            ElapsedTime = "";
            IsBusy = false;
            StatusText = skipped > 0
                ? $"完成: {CompletedCount}/{TotalCount} (跳过 {skipped} 张已识别)"
                : $"完成: {CompletedCount}/{TotalCount}";
            _logger.LogInformation("Recognition finished: {Completed}/{Total} (skipped {Skipped})", CompletedCount, TotalCount, skipped);
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
        IsCropPreviewVisible = CropPreviewSource is not null;
    }

    public void HideCropPreview()
    {
        IsCropPreviewVisible = false;
        CropPreviewSource = null;
    }

    [RelayCommand]
    private void ExportSingleResults()
    {
        var items = SingleResultItems;
        if (items is null || items.Count == 0) return;

        var imageName = Path.GetFileNameWithoutExtension(SelectedImage?.FilePath ?? "result");
        var modelSuffix = SelectedMode switch
        {
            RecognitionMode.ServerRec => "server_rec",
            RecognitionMode.MobileRec => "mobile_rec",
            RecognitionMode.EnMobileRec => "en_mobile_rec",
            _ => ""
        };
        var dialog = new SaveFileDialog
        {
            Filter = "文本文件|*.txt",
            Title = "导出识别结果",
            FileName = $"{imageName}_{modelSuffix}.txt"
        };
        if (dialog.ShowDialog() != true) return;

        var lines = items.Select(i => i.Text);
        File.WriteAllLines(dialog.FileName, lines);
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

    private static CrossValidateResult MergeResult(CrossValidateResult? existing, RecognitionMode mode, OcrSingleResult result)
    {
        var merged = existing ?? new CrossValidateResult();
        switch (mode)
        {
            case RecognitionMode.ServerRec: merged.ServerRec = result; break;
            case RecognitionMode.MobileRec: merged.MobileRec = result; break;
            case RecognitionMode.EnMobileRec: merged.EnMobileRec = result; break;
        }
        return merged;
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

    private void LogResultDetails(ImageFileItem item)
    {
        var result = item.Result;
        if (result is null) return;

        void LogItems(string model, List<OcrItem>? items)
        {
            if (items is null) return;
            foreach (var oi in items)
                _logger.LogTrace("[{Model}] \"{Text}\" score={Score} rect={Rect}", model, oi.Text, oi.Score, oi.BoundingRect);
        }

        LogItems("server_rec", result.ServerRec?.Items);
        LogItems("mobile_rec", result.MobileRec?.Items);
        LogItems("en_mobile_rec", result.EnMobileRec?.Items);
    }

    private static void AnnotateModel(Mat src, List<OcrItem> items, Scalar color, string outName, string outDir, bool append = false)
    {
        foreach (var item in items)
        {
            if (item.Box is null) continue;
            var pts = item.Box.Select(p => new OpenCvSharp.Point(p[0], p[1])).ToArray();
            Cv2.Polylines(src, new[] { pts }, isClosed: true, color: color, thickness: 2);
            var textPos = new OpenCvSharp.Point(pts[0].X, pts[0].Y - 6);
            var label = new string(item.Text.Where(c => c < 128).ToArray());
            Cv2.PutText(src, $"{label}({item.Score:P0})", textPos,
                HersheyFonts.HersheySimplex, 0.5, color, 1);
        }
        if (!append)
            Cv2.ImWrite(Path.Combine(outDir, outName), src);
    }

    private static string ConvertImageToBase64(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return Convert.ToBase64String(bytes);
    }
}
