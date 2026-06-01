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
    private readonly BaiduOcrClient _baiduClient;
    private readonly ServerProcessState _serverState;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly ApplicationHostService _appHost;
    private readonly AppConfigService _configService;
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

    public bool IsBaiduCloud => _configService.Config.Server.EngineSource == "baidu_cloud";
    public bool IsLocalService => _configService.Config.Server.EngineSource is "local_service" or "";

    public List<RecognitionModeOption> ModeOptions =>
        IsBaiduCloud
        ? [
            new(RecognitionMode.BaiduCrossValidate, "百度云交叉验证（双模型）"),
            new(RecognitionMode.BaiduApi, "百度云智能API(高精度含位置版)"),
            new(RecognitionMode.BaiduApiGeneral, "百度云智能API(标准含位置版)"),
        ]
        : [
            new(RecognitionMode.CrossValidate, "交叉验证（三模型）"),
            new(RecognitionMode.ServerRec, "PP-OCRv5_server_rec"),
            new(RecognitionMode.MobileRec, "PP-OCRv5_mobile_rec"),
            new(RecognitionMode.EnMobileRec, "en_PP-OCRv5_mobile_rec"),
        ];

    public bool IsCrossValidate => SelectedMode is RecognitionMode.CrossValidate or RecognitionMode.BaiduCrossValidate;

    public string CrossValidateHeader1 => SelectedMode == RecognitionMode.BaiduCrossValidate
        ? "百度云(高精度)" : "PP-OCRv5_server_rec";
    public string CrossValidateHeader2 => SelectedMode == RecognitionMode.BaiduCrossValidate
        ? "百度云(标准)" : "PP-OCRv5_mobile_rec";
    public string CrossValidateHeader3 => SelectedMode == RecognitionMode.BaiduCrossValidate
        ? "" : "en_PP-OCRv5_mobile_rec";

    public string SingleModelLabel => SelectedMode switch
    {
        RecognitionMode.ServerRec => "PP-OCRv5_server_rec",
        RecognitionMode.MobileRec => "PP-OCRv5_mobile_rec",
        RecognitionMode.EnMobileRec => "en_PP-OCRv5_mobile_rec",
        RecognitionMode.BaiduApi => "百度云智能API(高精度)",
        RecognitionMode.BaiduApiGeneral => "百度云智能API(标准)",
        RecognitionMode.BaiduCrossValidate => "百度云交叉验证",
        _ => ""
    };

    partial void OnSelectedModeChanged(RecognitionMode value)
    {
        OnPropertyChanged(nameof(IsCrossValidate));
        OnPropertyChanged(nameof(SingleModelLabel));
        OnPropertyChanged(nameof(CrossValidateHeader1));
        OnPropertyChanged(nameof(CrossValidateHeader2));
        OnPropertyChanged(nameof(CrossValidateHeader3));
        RebuildCachedGroups();
    }

    private List<CrossValidateGroup>? _cachedGroups;

    partial void OnSelectedImageChanged(ImageFileItem? value)
    {
        HasSelection = value is not null;
        RebuildCachedGroups();
    }

    private void RebuildCachedGroups()
    {
        if (SelectedImage?.Result is not { } r)
        {
            _cachedGroups = null;
        }
        else if (IsCrossValidate)
        {
            var cfg = _configService.Config.Server;
            List<List<OcrItem>> modelResults;
            List<string> modelNames;

            if (SelectedMode == RecognitionMode.BaiduCrossValidate)
            {
                // Baidu dual model cross-validate
                modelResults = [r.BaiduApiRec?.Items ?? [], r.BaiduApiGeneralRec?.Items ?? []];
                modelNames = ["百度云(高精度)", "百度云(标准)"];
            }
            else
            {
                // Local 3-model cross-validate
                modelResults = [r.ServerRec?.Items ?? [], r.MobileRec?.Items ?? [], r.EnMobileRec?.Items ?? []];
                modelNames = ["PP-OCRv5_server_rec", "PP-OCRv5_mobile_rec", "en_PP-OCRv5_mobile_rec"];
            }

            _cachedGroups = CrossValidateAligner.Align(
                modelResults, modelNames,
                cfg.CrossValidateAutoConfirmThreshold,
                cfg.CrossValidateAutoFillThreshold);
        }
        else
        {
            // Single model: convert to CrossValidateGroup with confidence-based agreement
            var items = SelectedMode switch
            {
                RecognitionMode.ServerRec => r.ServerRec?.Items,
                RecognitionMode.MobileRec => r.MobileRec?.Items,
                RecognitionMode.EnMobileRec => r.EnMobileRec?.Items,
                RecognitionMode.BaiduApi => r.BaiduApiRec?.Items,
                RecognitionMode.BaiduApiGeneral => r.BaiduApiGeneralRec?.Items,
                _ => null
            };
            if (items is not null && items.Count > 0)
            {
                var cfg = _configService.Config.Server;
                _cachedGroups = CrossValidateAligner.AlignSingleModel(
                    items, SingleModelLabel,
                    cfg.SingleModelAutoConfirmThreshold,
                    cfg.SingleModelAutoFillThreshold);
            }
            else
            {
                _cachedGroups = null;
            }
        }

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

    /// <summary>Called by code-behind (e.g. Enter key) to refresh AllConfirmed binding.</summary>
    public void NotifyAllConfirmed() => OnPropertyChanged(nameof(AllConfirmed));

    public bool CanStartRecognition => (IsBaiduCloud || _serverState.IsReady) && !IsBusy && Images.Count > 0;

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

    public HomeViewModel(OcrApiClient ocrClient, BaiduOcrClient baiduClient, ServerProcessState serverState, ILogger<HomeViewModel> logger, ApplicationHostService appHost, AppConfigService configService)
    {
        _ocrClient = ocrClient;
        _baiduClient = baiduClient;
        _serverState = serverState;
        _logger = logger;
        _appHost = appHost;
        _configService = configService;

        // Set default mode for Baidu Cloud
        if (IsBaiduCloud)
            _selectedMode = RecognitionMode.BaiduCrossValidate;

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
                        case RecognitionMode.BaiduCrossValidate:
                            var accResult = await _baiduClient.RecognizeAsync(base64,
                                _configService.Config.Server.BaiduClientId,
                                _configService.Config.Server.BaiduClientSecret,
                                accurate: true, token);
                            var genResult = await _baiduClient.RecognizeAsync(base64,
                                _configService.Config.Server.BaiduClientId,
                                _configService.Config.Server.BaiduClientSecret,
                                accurate: false, token);
                            var merged = new CrossValidateResult
                            {
                                BaiduApiRec = accResult,
                                BaiduApiGeneralRec = genResult
                            };
                            item.Result = merged;
                            item.CompletedModes.Add(RecognitionMode.BaiduCrossValidate);
                            break;
                        case RecognitionMode.BaiduApi:
                            var baiduAccResult = await _baiduClient.RecognizeAsync(base64,
                                _configService.Config.Server.BaiduClientId,
                                _configService.Config.Server.BaiduClientSecret,
                                accurate: true, token);
                            item.Result = MergeResult(null, RecognitionMode.BaiduApi, baiduAccResult);
                            item.CompletedModes.Add(RecognitionMode.BaiduApi);
                            break;
                        case RecognitionMode.BaiduApiGeneral:
                            var baiduGenResult = await _baiduClient.RecognizeAsync(base64,
                                _configService.Config.Server.BaiduClientId,
                                _configService.Config.Server.BaiduClientSecret,
                                accurate: false, token);
                            item.Result = MergeResult(null, RecognitionMode.BaiduApiGeneral, baiduGenResult);
                            item.CompletedModes.Add(RecognitionMode.BaiduApiGeneral);
                            break;
                    }
                    sw.Stop();
                    _logger.LogInformation("Done: {FileName} in {ElapsedMs}ms, {Count} items",
                        item.FileName, sw.ElapsedMilliseconds,
                        item.Result?.ServerRec?.Count ?? item.Result?.MobileRec?.Count ?? item.Result?.EnMobileRec?.Count ?? item.Result?.BaiduApiRec?.Count ?? 0);

                    if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                        LogResultDetails(item);
                    item.Status = ImageFileStatus.Completed;
                    if (item == SelectedImage)
                        RebuildCachedGroups();
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

    [RelayCommand]
    private void CopyResults()
    {
        if (_cachedGroups is null) return;

        var lines = _cachedGroups
            .Where(g => g.IsConfirmed && !string.IsNullOrEmpty(g.ConfirmedText))
            .Select(g => g.ConfirmedText);
        var text = string.Join(Environment.NewLine, lines);
        Clipboard.SetText(text);
    }

    [RelayCommand]
    private void ExportAnnotatedImage()
    {
        if (_cachedGroups is null || SelectedImage?.FilePath is not { } imagePath) return;

        var imageName = Path.GetFileNameWithoutExtension(imagePath);
        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            Title = "导出批注图片",
            FileName = $"{imageName}_ocr.png"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            using var src = Cv2.ImRead(imagePath);
            if (src is null || src.Empty()) return;

            var confirmed = _cachedGroups
                .Where(g => g.IsConfirmed && !string.IsNullOrWhiteSpace(g.ConfirmedText))
                .ToList();
            if (confirmed.Count == 0) return;

            var boxColor = new Scalar(0, 200, 0);

            // Collect all bounding rects for adjacency calculation
            var allRects = confirmed.Select(g => (OpenCvSharp.Rect)g.ScaledUnionRect).ToList();

            // Pick one global label direction based on average gaps across all boxes
            string globalDir = PickGlobalLabelDirection(allRects);

            // Pass 1: draw all bounding boxes
            foreach (var group in confirmed)
            {
                var rect = (OpenCvSharp.Rect)group.ScaledUnionRect;
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                int x = Math.Max(0, rect.X), y = Math.Max(0, rect.Y);
                int w = Math.Min(rect.Width, src.Width - x), h = Math.Min(rect.Height, src.Height - y);
                if (w <= 0 || h <= 0) continue;

                Cv2.Rectangle(src, new OpenCvSharp.Point(x, y), new OpenCvSharp.Point(x + w, y + h), boxColor, 2);
            }

            // Pass 2: draw all labels on top
            foreach (var group in confirmed)
            {
                var rect = (OpenCvSharp.Rect)group.ScaledUnionRect;
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                double gap = GetGapInDirection(allRects, rect, globalDir);
                var (lx, ly) = GetLabelPosition(rect, globalDir, src.Width, src.Height);
                DrawChineseText(src, group.ConfirmedText, lx, ly, gap);
            }

            Cv2.ImWrite(dialog.FileName, src);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to export annotated image: {Error}", ex.Message);
        }
    }

    /// <summary>Pick one global direction (left/right/top/bottom) with the largest average gap across all boxes.</summary>
    private static string PickGlobalLabelDirection(List<OpenCvSharp.Rect> rects)
    {
        if (rects.Count == 0) return "right";

        double leftSum = 0, rightSum = 0, topSum = 0, bottomSum = 0;
        int leftCount = 0, rightCount = 0, topCount = 0, bottomCount = 0;

        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];

            // Find nearest other box in each direction
            double nearestLeft = double.MaxValue, nearestRight = double.MaxValue;
            double nearestTop = double.MaxValue, nearestBottom = double.MaxValue;

            for (int j = 0; j < rects.Count; j++)
            {
                if (i == j) continue;
                var o = rects[j];

                // Left: other box is to the left, not overlapping vertically too much
                if (o.Right <= r.Left && !(o.Bottom <= r.Top || o.Top >= r.Bottom))
                {
                    double d = r.Left - o.Right;
                    if (d < nearestLeft) nearestLeft = d;
                }
                // Right
                if (o.Left >= r.Right && !(o.Bottom <= r.Top || o.Top >= r.Bottom))
                {
                    double d = o.Left - r.Right;
                    if (d < nearestRight) nearestRight = d;
                }
                // Top
                if (o.Bottom <= r.Top && !(o.Right <= r.Left || o.Left >= r.Right))
                {
                    double d = r.Top - o.Bottom;
                    if (d < nearestTop) nearestTop = d;
                }
                // Bottom
                if (o.Top >= r.Bottom && !(o.Right <= r.Left || o.Left >= r.Right))
                {
                    double d = o.Top - r.Bottom;
                    if (d < nearestBottom) nearestBottom = d;
                }
            }

            if (nearestLeft < double.MaxValue) { leftSum += nearestLeft; leftCount++; }
            if (nearestRight < double.MaxValue) { rightSum += nearestRight; rightCount++; }
            if (nearestTop < double.MaxValue) { topSum += nearestTop; topCount++; }
            if (nearestBottom < double.MaxValue) { bottomSum += nearestBottom; bottomCount++; }
        }

        double leftAvg = leftCount > 0 ? leftSum / leftCount : 0;
        double rightAvg = rightCount > 0 ? rightSum / rightCount : 0;
        double topAvg = topCount > 0 ? topSum / topCount : 0;
        double bottomAvg = bottomCount > 0 ? bottomSum / bottomCount : 0;

        // Priority: left > right > top > bottom on tie
        if (leftAvg >= rightAvg && leftAvg >= topAvg && leftAvg >= bottomAvg) return "left";
        if (rightAvg >= topAvg && rightAvg >= bottomAvg) return "right";
        if (topAvg >= bottomAvg) return "top";
        return "bottom";
    }

    /// <summary>Get label anchor position for a given direction, tight to the box edge.</summary>
    private static (int x, int y) GetLabelPosition(OpenCvSharp.Rect r, string dir, int imgW, int imgH)
    {
        int x = r.X, y = r.Y;
        switch (dir)
        {
            case "left":  x = Math.Max(0, r.Left - 2); y = r.Top; break;
            case "right": x = Math.Min(imgW - 1, r.Right + 2); y = r.Top; break;
            case "top":   x = r.Left; y = Math.Max(0, r.Top - 2); break;
            default:      x = r.Left; y = Math.Min(imgH - 1, r.Bottom + 2); break;
        }
        return (x, y);
    }

    /// <summary>Get the gap to the nearest neighbor box in the given direction.</summary>
    private static double GetGapInDirection(List<OpenCvSharp.Rect> rects, OpenCvSharp.Rect r, string dir)
    {
        double nearest = double.MaxValue;
        foreach (var o in rects)
        {
            if (o == r) continue;
            double d = dir switch
            {
                "left" => !(o.Bottom <= r.Top || o.Top >= r.Bottom) ? r.Left - o.Right : double.MaxValue,
                "right" => !(o.Bottom <= r.Top || o.Top >= r.Bottom) ? o.Left - r.Right : double.MaxValue,
                "top" => !(o.Right <= r.Left || o.Left >= r.Right) ? r.Top - o.Bottom : double.MaxValue,
                _ => !(o.Right <= r.Left || o.Left >= r.Right) ? o.Top - r.Bottom : double.MaxValue,
            };
            if (d > 0 && d < nearest) nearest = d;
        }
        return nearest < double.MaxValue ? nearest : double.MaxValue;
    }

    private static void DrawChineseText(Mat mat, string text, int x, int y, double gapPx = double.MaxValue)
    {
        const int defaultFontSize = 12;
        const int minFontSize = 7;

        int fontSize = defaultFontSize;
        if (gapPx < 100 && gapPx > 0)
            fontSize = Math.Max(minFontSize, (int)(defaultFontSize * gapPx / 100.0));

        using var font = new System.Drawing.Font("Microsoft YaHei", fontSize, System.Drawing.FontStyle.Regular);
        using var dummyBmp = new System.Drawing.Bitmap(1, 1);
        using var g = System.Drawing.Graphics.FromImage(dummyBmp);
        var size = g.MeasureString(text, font);
        int tw = (int)Math.Ceiling(size.Width) + 4;
        int th = (int)Math.Ceiling(size.Height) + 2;

        if (tw <= 4 || th <= 2) return;

        using var textBmp = new System.Drawing.Bitmap(tw, th);
        using var tg = System.Drawing.Graphics.FromImage(textBmp);
        tg.Clear(System.Drawing.Color.Transparent);
        tg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        // Semi-transparent white background
        using var bgBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(180, 255, 255, 255));
        tg.FillRectangle(bgBrush, 0, 0, tw, th);

        // Red text
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Red);
        tg.DrawString(text, font, brush, 2, 1);

        var data = textBmp.LockBits(
            new System.Drawing.Rectangle(0, 0, tw, th),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var overlay = Mat.FromPixelData(th, tw, MatType.CV_8UC4, data.Scan0, data.Stride);
            using var overlayBgr = new Mat();
            Cv2.CvtColor(overlay, overlayBgr, ColorConversionCodes.BGRA2BGR);

            int copyW = Math.Min(tw, mat.Width - x);
            int copyH = Math.Min(th, mat.Height - y);
            if (copyW <= 0 || copyH <= 0) return;

            using var roi = new Mat(mat, new OpenCvSharp.Rect(x, y, copyW, copyH));
            using var overlayRoi = new Mat(overlayBgr, new OpenCvSharp.Rect(0, 0, copyW, copyH));
            // Alpha blend: overlay where non-white
            using var mask = new Mat();
            Cv2.CvtColor(overlayRoi, mask, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(mask, mask, 250, 255, ThresholdTypes.BinaryInv);
            if (Cv2.CountNonZero(mask) > 0)
                overlayRoi.CopyTo(roi, mask);
        }
        finally
        {
            textBmp.UnlockBits(data);
        }
    }

    private static CrossValidateResult MergeResult(CrossValidateResult? existing, RecognitionMode mode, OcrSingleResult result)
    {
        var merged = existing ?? new CrossValidateResult();
        switch (mode)
        {
            case RecognitionMode.ServerRec: merged.ServerRec = result; break;
            case RecognitionMode.MobileRec: merged.MobileRec = result; break;
            case RecognitionMode.EnMobileRec: merged.EnMobileRec = result; break;
            case RecognitionMode.BaiduApi: merged.BaiduApiRec = result; break;
            case RecognitionMode.BaiduApiGeneral: merged.BaiduApiGeneralRec = result; break;
            case RecognitionMode.BaiduCrossValidate: break; // handled directly in recognition switch
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
