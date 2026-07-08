using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OcrClient.Core.Models;
using OpenCvSharp;

namespace OcrClient.Core.Onnx;

/// <summary>
/// 基于 ONNX Runtime 的 OCR 引擎。自动从模型目录发现并加载所有检测和识别模型。
/// </summary>
public class OnnxOcrEngine : IDisposable
{
    private readonly ILogger _logger;
    private readonly List<InferenceSession> _detSessions = [];
    private readonly List<InferenceSession> _recSessions = [];
    private readonly List<OnnxCharDict> _recCharDicts = [];
    private readonly string _deviceName;
    private bool _disposed;

    // 模型标识名（目录名），与 session/dict 按索引对应
    private readonly List<string> _detNames = [];
    private readonly List<string> _recNames = [];

    /// <summary>已加载的检测模型名称列表。</summary>
    public IReadOnlyList<string> DetModels => _detNames;

    /// <summary>已加载的识别模型名称列表。</summary>
    public IReadOnlyList<string> RecModels => _recNames;

    /// <summary>按名称查找识别模型索引。找不到返回 -1。</summary>
    public int FindRecIdx(string name) => _recNames.IndexOf(name);

    /// <summary>引擎是否已就绪。</summary>
    public bool IsReady => _detSessions.Count > 0 && _recSessions.Count > 0;

    /// <summary>当前推理设备名称。</summary>
    public string DeviceName => _deviceName;

    /// <summary>最近一次推理的耗时统计。</summary>
    public OcrTiming? LastTiming { get; private set; }

    /// <summary>
    /// 创建 ONNX OCR 引擎，自动扫描 ModelsDir 加载所有模型。
    /// 目录结构：ModelsDir/det/{name}/model.onnx, ModelsDir/rec/{name}/model.onnx + char_dict.json。
    /// </summary>
    public OnnxOcrEngine(string modelsDir, int gpuId, ILogger logger)
    {
        _logger = logger;

        // 配置推理会话
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
                _logger.LogWarning(ex, "CUDA 加载失败，回退到 CPU");
                sessionOptions.AppendExecutionProvider_CPU();
                _deviceName = "CPU";
            }
        }
        else
        {
            sessionOptions.AppendExecutionProvider_CPU();
            _deviceName = "CPU";
        }

        // 自动发现检测模型
        var detDir = Path.Combine(modelsDir, "det");
        if (Directory.Exists(detDir))
        {
            foreach (var sub in Directory.GetDirectories(detDir))
            {
                var onnxPath = Path.Combine(sub, "model.onnx");
                if (!File.Exists(onnxPath)) continue;
                var name = Path.GetFileName(sub);
                try
                {
                    _detSessions.Add(new InferenceSession(onnxPath, sessionOptions));
                    _detNames.Add(name);
                    _logger.LogInformation("检测模型 [{Name}]: {Path}", name, onnxPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "加载检测模型失败 [{Name}]", name);
                }
            }
        }
        else { _logger.LogWarning("检测模型目录不存在: {Dir}", detDir); }

        // 自动发现识别模型
        var recDir = Path.Combine(modelsDir, "rec");
        if (Directory.Exists(recDir))
        {
            foreach (var sub in Directory.GetDirectories(recDir))
            {
                var onnxPath = Path.Combine(sub, "model.onnx");
                if (!File.Exists(onnxPath)) continue;
                var name = Path.GetFileName(sub);
                try
                {
                    _recSessions.Add(new InferenceSession(onnxPath, sessionOptions));
                    _recNames.Add(name);
                    _logger.LogInformation("识别模型 [{Name}]: {Path}", name, onnxPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "加载识别模型失败 [{Name}]", name);
                    continue;
                }

                // 字符字典
                var dictPath = Path.Combine(sub, "char_dict.json");
                if (File.Exists(dictPath))
                {
                    try
                    {
                        _recCharDicts.Add(OnnxCharDict.Load(dictPath));
                        _logger.LogInformation("字符字典 [{Name}]: {Count} 字符", name, _recCharDicts[^1].Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "加载字符字典失败 [{Name}]", name);
                        _recCharDicts.Add(OnnxCharDict.CreateEmpty());
                    }
                }
                else
                {
                    _logger.LogWarning("字符字典未找到 [{Name}]: {Path}", name, dictPath);
                    _recCharDicts.Add(OnnxCharDict.CreateEmpty());
                }
            }
        }
        else { _logger.LogWarning("识别模型目录不存在: {Dir}", recDir); }

        _logger.LogInformation("ONNX 引擎就绪：{DetCount} 检测, {RecCount} 识别, {Device}",
            _detSessions.Count, _recSessions.Count, _deviceName);
    }

    // ── 检测 ──────────────────────────────────────────────────────────────────

    public (List<Point2f[]> Boxes, float[] Scores, List<Mat> Crops) Detect(Mat imageBgr, int detIdx = 0)
    {
        if (_detSessions.Count == 0)
            throw new InvalidOperationException("没有可用的检测模型");

        var t0 = System.Diagnostics.Stopwatch.StartNew();

        var (tensor, shapeInfo) = OnnxPreprocess.PreprocessDet(imageBgr);
        var prepMs = t0.ElapsedMilliseconds;

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", tensor) };
        using var results = _detSessions[detIdx].Run(inputs);
        var output = results[0].AsTensor<float>() as DenseTensor<float>
            ?? throw new InvalidOperationException("检测模型输出不是 DenseTensor<float>");
        var inferMs = t0.ElapsedMilliseconds - prepMs;

        var (boxes, scores) = OnnxPostprocess.ExtractBoxes(
            output, (imageBgr.Rows, imageBgr.Cols), shapeInfo);
        var postMs = t0.ElapsedMilliseconds - prepMs - inferMs;

        var crops = OnnxPostprocess.CropRegions(imageBgr, boxes);

        _logger.LogDebug("检测 [{Name}]: prep={PrepMs}ms infer={InferMs}ms post={PostMs}ms boxes={Count}",
            _detNames[detIdx], prepMs, inferMs, postMs, boxes.Count);

        return (boxes, scores.ToArray(), crops);
    }

    // ── 识别 ──────────────────────────────────────────────────────────────────

    public List<(string Text, float Confidence)> Recognize(List<Mat> crops, int recIdx)
    {
        if (crops.Count == 0) return [];
        var session = _recSessions[recIdx];
        var charDict = _recCharDicts[recIdx];

        var t0 = System.Diagnostics.Stopwatch.StartNew();

        var tensor = OnnxPreprocess.PreprocessRecBatch(crops);
        var prepMs = t0.ElapsedMilliseconds;

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", tensor) };
        using var results = session.Run(inputs);
        var logits = results[0].AsTensor<float>() as DenseTensor<float>
            ?? throw new InvalidOperationException($"识别模型 [{_recNames[recIdx]}] 输出不是 DenseTensor<float>");
        var inferMs = t0.ElapsedMilliseconds - prepMs;

        var decoded = OnnxPostprocess.CtcDecodeBatch(logits, charDict);
        var decodeMs = t0.ElapsedMilliseconds - prepMs - inferMs;

        _logger.LogDebug("识别 [{Name}]: prep={PrepMs}ms infer={InferMs}ms decode={DecodeMs}ms batch={Batch}",
            _recNames[recIdx], prepMs, inferMs, decodeMs, crops.Count);

        return decoded;
    }

    // ── 公开 API ──────────────────────────────────────────────────────────────

    /// <summary>单模型识别。recIdx 按 RecModels 顺序，-1 表示第一个模型。</summary>
    public List<OcrItem> Predict(Mat imageBgr, int recIdx = -1)
    {
        if (recIdx < 0) recIdx = 0;
        var name = _recNames[recIdx];
        var t0 = System.Diagnostics.Stopwatch.StartNew();

        var (boxes, scores, crops) = Detect(imageBgr);
        var detMs = t0.Elapsed.TotalMilliseconds;

        if (boxes.Count == 0)
        {
            LastTiming = new OcrTiming { DetectMs = detMs, BoxCount = 0, ModelCount = 1, DeviceName = _deviceName };
            return [];
        }

        var recResults = Recognize(crops, recIdx);
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
                Text = text, Score = conf,
                Box = boxes[i].Select(p => new List<double> { p.X, p.Y }).ToList(),
            });
        }

        _logger.LogInformation("Predict [{Name}]: {Timing}", name, LastTiming);
        return items;
    }

    /// <summary>所有识别模型的交叉验证。识别模型并行执行以最大化 GPU 利用率。</summary>
    public CrossValidateResult CrossValidate(Mat imageBgr, int detIdx = 0)
    {
        var t0 = System.Diagnostics.Stopwatch.StartNew();

        var (boxes, scores, crops) = Detect(imageBgr, detIdx);
        var detMs = t0.Elapsed.TotalMilliseconds;

        if (boxes.Count == 0)
        {
            LastTiming = new OcrTiming { DetectMs = detMs, BoxCount = 0, DeviceName = _deviceName };
            return new CrossValidateResult();
        }

        var result = new CrossValidateResult();

        // 并行运行所有识别模型（每个模型使用独立的 session，线程安全）
        Parallel.For(0, _recSessions.Count, i =>
        {
            var recResults = Recognize(crops, i);
            var items = new List<OcrItem>(recResults.Count);
            for (int j = 0; j < recResults.Count; j++)
            {
                var (text, conf) = recResults[j];
                items.Add(new OcrItem
                {
                    Text = text, Score = conf,
                    Box = j < boxes.Count
                        ? boxes[j].Select(p => new List<double> { p.X, p.Y }).ToList()
                        : null,
                });
            }
            var single = new OcrSingleResult { Model = _recNames[i], Count = items.Count, Items = items };
            lock (result) { AssignRecResult(result, _recNames[i], single); }
        });

        var totalMs = t0.Elapsed.TotalMilliseconds;
        LastTiming = new OcrTiming
        {
            DetectMs = detMs, RecMs = totalMs - detMs, TotalMs = totalMs,
            BoxCount = boxes.Count, ModelCount = _recSessions.Count, DeviceName = _deviceName,
        };

        _logger.LogInformation("CrossValidate: {Timing}", LastTiming);
        return result;
    }

    /// <summary>根据模型名将结果赋到 CrossValidateResult 对应字段。</summary>
    private static void AssignRecResult(CrossValidateResult r, string name, OcrSingleResult single)
    {
        switch (name)
        {
            case "PP-OCRv5_server_rec": r.ServerRec = single; break;
            case "PP-OCRv5_mobile_rec": r.MobileRec = single; break;
            case "en_PP-OCRv5_mobile_rec": r.EnMobileRec = single; break;
            default: break; // 未知模型跳过
        }
    }

    // ── 资源释放 ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var s in _detSessions) s.Dispose();
        foreach (var s in _recSessions) s.Dispose();
        _detSessions.Clear(); _recSessions.Clear();
        _recCharDicts.Clear();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
