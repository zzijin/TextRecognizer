using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OcrClient.Core.Models;
using OpenCvSharp;

namespace OcrClient.Core.Onnx;

/// <summary>
/// 基于 Microsoft.ML.OnnxRuntime 的 OCR 引擎，直接在 C# 进程中运行 ONNX 推理。
/// 参考 TileMind YoloDetector 的模式，复制 Python onnx_ocr.py 的预处理/后处理逻辑。
/// </summary>
public class OnnxOcrEngine : IDisposable
{
    private readonly ILogger _logger;
    private readonly InferenceSession? _detSession;
    private readonly Dictionary<string, InferenceSession> _recSessions = [];
    private readonly Dictionary<string, OnnxCharDict> _charDicts = [];
    private readonly string _deviceName;
    private bool _disposed;

    /// <summary>识别模型键名 → ONNX 文件名映射。</summary>
    public static readonly IReadOnlyDictionary<string, string> RecModelNames = new Dictionary<string, string>
    {
        ["server"] = "PP-OCRv5_server_rec",
        ["mobile_cn"] = "PP-OCRv5_mobile_rec",
        ["en_mobile"] = "en_PP-OCRv5_mobile_rec",
    };

    private const string DetModelName = "PP-OCRv5_server_det";

    /// <summary>引擎是否已就绪（所有模型加载成功）。</summary>
    public bool IsReady => _detSession is not null && _recSessions.Count > 0;

    /// <summary>当前使用的推理设备名称。</summary>
    public string DeviceName => _deviceName;

    /// <summary>最近一次推理的各阶段耗时统计。</summary>
    public OcrTiming? LastTiming { get; private set; }

    /// <summary>
    /// 创建 ONNX OCR 引擎。
    /// </summary>
    /// <param name="onnxModelsDir">.onnx 模型文件目录</param>
    /// <param name="charDictDir">字符字典模型目录（含 PP-OCRv5_server_rec/config.json）</param>
    /// <param name="gpuId">GPU 设备 ID，-1 表示 CPU</param>
    /// <param name="logger">日志记录器</param>
    public OnnxOcrEngine(string onnxModelsDir, string charDictDir, int gpuId, ILogger logger)
    {
        _logger = logger;

        // 配置推理会话选项
        var sessionOptions = new SessionOptions();
        if (gpuId >= 0)
        {
            try
            {
                sessionOptions.AppendExecutionProvider_CUDA(gpuId);
                _deviceName = $"CUDA:{gpuId}";
                _logger.LogInformation("ONNX 引擎使用 CUDA GPU {GpuId}", gpuId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CUDA 提供程序加载失败，回退到 CPU");
                sessionOptions.AppendExecutionProvider_CPU();
                _deviceName = "CPU";
            }
        }
        else
        {
            sessionOptions.AppendExecutionProvider_CPU();
            _deviceName = "CPU";
        }

        // 加载检测模型
        var detPath = Path.Combine(onnxModelsDir, $"{DetModelName}.onnx");
        if (File.Exists(detPath))
        {
            try
            {
                _detSession = new InferenceSession(detPath, sessionOptions);
                _logger.LogInformation("已加载检测模型: {Path}", detPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载检测模型失败: {Path}", detPath);
            }
        }
        else
        {
            _logger.LogWarning("检测模型未找到: {Path}", detPath);
        }

        // 加载识别模型和字符字典
        foreach (var (key, modelName) in RecModelNames)
        {
            var recPath = Path.Combine(onnxModelsDir, $"{modelName}.onnx");
            if (File.Exists(recPath))
            {
                try
                {
                    _recSessions[key] = new InferenceSession(recPath, sessionOptions);
                    _logger.LogInformation("已加载识别模型 [{Key}]: {Path}", key, recPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "加载识别模型失败 [{Key}]: {Path}", key, recPath);
                }
            }
            else
            {
                _logger.LogWarning("识别模型未找到 [{Key}]: {Path}", key, recPath);
            }

            // 加载字符字典
            var configPath = Path.Combine(charDictDir, modelName, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    _charDicts[key] = OnnxCharDict.Load(configPath);
                    _logger.LogInformation("已加载字符字典 [{Key}]: {Count} 个字符", key, _charDicts[key].Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "加载字符字典失败 [{Key}]: {Path}", key, configPath);
                }
            }
            else
            {
                _logger.LogWarning("字符字典未找到 [{Key}]: {Path}", key, configPath);
            }
        }

        _logger.LogInformation("ONNX OCR 引擎初始化完成。就绪={IsReady}, 设备={Device}", IsReady, _deviceName);
    }

    // ── 检测 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 运行文字检测。返回文本框、分数和裁剪图像。
    /// </summary>
    public (List<Point2f[]> Boxes, float[] Scores, List<Mat> Crops) Detect(Mat imageBgr)
    {
        if (_detSession is null)
            throw new InvalidOperationException("检测模型未加载");

        var t0 = System.Diagnostics.Stopwatch.StartNew();

        // 预处理
        var (tensor, shapeInfo) = OnnxPreprocess.PreprocessDet(imageBgr);
        var prepMs = t0.ElapsedMilliseconds;

        // 推理
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("x", tensor)
        };
        using var results = _detSession.Run(inputs);
        var outputTensor = results[0].AsTensor<float>();
        var output = outputTensor as DenseTensor<float>
            ?? throw new InvalidOperationException("检测模型输出不是 DenseTensor<float>");
        var inferMs = t0.ElapsedMilliseconds - prepMs;

        // 后处理
        var (boxes, scores) = OnnxPostprocess.ExtractBoxes(
            output, (imageBgr.Rows, imageBgr.Cols), shapeInfo);
        var postMs = t0.ElapsedMilliseconds - prepMs - inferMs;

        // 裁剪文字区域
        var crops = OnnxPostprocess.CropRegions(imageBgr, boxes);

        _logger.LogDebug("检测完成: prep={PrepMs}ms infer={InferMs}ms post={PostMs}ms boxes={Count}",
            prepMs, inferMs, postMs, boxes.Count);

        return (boxes, scores.ToArray(), crops);
    }

    // ── 识别 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 对裁剪图像批量运行识别。
    /// </summary>
    /// <param name="crops">文字区域裁剪图像</param>
    /// <param name="recKey">识别模型键名（"server"、"mobile_cn"、"en_mobile"）</param>
    /// <returns>(text, confidence) 列表</returns>
    public List<(string Text, float Confidence)> Recognize(List<Mat> crops, string recKey)
    {
        if (crops.Count == 0)
            return [];
        if (!_recSessions.TryGetValue(recKey, out var session))
            throw new InvalidOperationException($"识别模型 [{recKey}] 未加载");
        if (!_charDicts.TryGetValue(recKey, out var charDict))
            throw new InvalidOperationException($"字符字典 [{recKey}] 未加载");

        var t0 = System.Diagnostics.Stopwatch.StartNew();

        // 预处理
        var tensor = OnnxPreprocess.PreprocessRecBatch(crops);
        var prepMs = t0.ElapsedMilliseconds;

        // 推理
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("x", tensor)
        };
        using var results = session.Run(inputs);
        var logits = results[0].AsTensor<float>() as DenseTensor<float>
            ?? throw new InvalidOperationException($"识别模型 [{recKey}] 输出不是 DenseTensor<float>");
        var inferMs = t0.ElapsedMilliseconds - prepMs;

        // CTC 解码
        var decoded = OnnxPostprocess.CtcDecodeBatch(logits, charDict);
        var decodeMs = t0.ElapsedMilliseconds - prepMs - inferMs;

        _logger.LogDebug("识别完成 [{Key}]: prep={PrepMs}ms infer={InferMs}ms decode={DecodeMs}ms batch={Batch}",
            recKey, prepMs, inferMs, decodeMs, crops.Count);

        return decoded;
    }

    // ── 公开 API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 单模型完整 OCR 流水线：检测 + 识别 → OcrItem 列表。
    /// </summary>
    public List<OcrItem> Predict(Mat imageBgr, string recKey = "server")
    {
        var t0 = System.Diagnostics.Stopwatch.StartNew();

        var (boxes, scores, crops) = Detect(imageBgr);
        var detMs = t0.Elapsed.TotalMilliseconds;

        if (boxes.Count == 0)
        {
            LastTiming = new OcrTiming
            {
                DetectMs = detMs, RecMs = 0, TotalMs = detMs,
                BoxCount = 0, ModelCount = 1, DeviceName = _deviceName,
            };
            return [];
        }

        var recResults = Recognize(crops, recKey);
        var totalMs = t0.Elapsed.TotalMilliseconds;

        LastTiming = new OcrTiming
        {
            DetectMs = detMs, RecMs = totalMs - detMs, TotalMs = totalMs,
            BoxCount = boxes.Count, ModelCount = 1, DeviceName = _deviceName,
        };

        var items = new List<OcrItem>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
        {
            var (text, conf) = i < recResults.Count ? recResults[i] : ("", 0f);
            items.Add(new OcrItem
            {
                Text = text,
                Score = conf,
                Box = boxes[i].Select(p => new List<double> { p.X, p.Y }).ToList(),
            });
        }

        _logger.LogInformation("Predict [{Key}]: {Timing}", recKey, LastTiming);

        return items;
    }

    /// <summary>
    /// 三模型交叉验证 OCR。对同一图像运行所有 3 个识别模型。
    /// 返回 CrossValidateResult 可直接传给 CrossValidateAligner.Align()。
    /// </summary>
    public CrossValidateResult CrossValidate(Mat imageBgr)
    {
        var t0 = System.Diagnostics.Stopwatch.StartNew();

        var (boxes, scores, crops) = Detect(imageBgr);
        var detMs = t0.Elapsed.TotalMilliseconds;

        int modelCount = 0;
        if (boxes.Count == 0)
        {
            LastTiming = new OcrTiming
            {
                DetectMs = detMs, RecMs = 0, TotalMs = detMs,
                BoxCount = 0, ModelCount = 0, DeviceName = _deviceName,
            };
            return new CrossValidateResult();
        }

        var result = new CrossValidateResult();

        // 按顺序运行所有可用的识别模型
        if (_recSessions.ContainsKey("server") && _charDicts.ContainsKey("server"))
        {
            var recResults = Recognize(crops, "server");
            result.ServerRec = ToOcrSingleResult("PP-OCRv5_server_rec", recResults, boxes);
            modelCount++;
        }

        if (_recSessions.ContainsKey("mobile_cn") && _charDicts.ContainsKey("mobile_cn"))
        {
            var recResults = Recognize(crops, "mobile_cn");
            result.MobileRec = ToOcrSingleResult("PP-OCRv5_mobile_rec", recResults, boxes);
            modelCount++;
        }

        if (_recSessions.ContainsKey("en_mobile") && _charDicts.ContainsKey("en_mobile"))
        {
            var recResults = Recognize(crops, "en_mobile");
            result.EnMobileRec = ToOcrSingleResult("en_PP-OCRv5_mobile_rec", recResults, boxes);
            modelCount++;
        }

        var totalMs = t0.Elapsed.TotalMilliseconds;
        LastTiming = new OcrTiming
        {
            DetectMs = detMs,
            RecMs = totalMs - detMs,
            TotalMs = totalMs,
            BoxCount = boxes.Count,
            ModelCount = modelCount,
            DeviceName = _deviceName,
        };

        _logger.LogInformation("CrossValidate: {Timing}", LastTiming);

        return result;
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────

    private static OcrSingleResult ToOcrSingleResult(string modelName,
        List<(string Text, float Confidence)> recResults, List<Point2f[]> boxes)
    {
        var items = new List<OcrItem>(recResults.Count);
        for (int i = 0; i < recResults.Count; i++)
        {
            var (text, conf) = recResults[i];
            items.Add(new OcrItem
            {
                Text = text,
                Score = conf,
                Box = i < boxes.Count
                    ? boxes[i].Select(p => new List<double> { p.X, p.Y }).ToList()
                    : null,
            });
        }
        return new OcrSingleResult { Model = modelName, Count = items.Count, Items = items };
    }

    // ── 资源释放 ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _detSession?.Dispose();
        foreach (var s in _recSessions.Values)
            s.Dispose();
        _recSessions.Clear();
        _charDicts.Clear();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
