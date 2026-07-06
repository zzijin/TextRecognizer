using Microsoft.Extensions.Logging;
using OcrClient.Core.Onnx;
using OpenCvSharp;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
var logger = loggerFactory.CreateLogger<OnnxOcrEngine>();

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var onnxDir = Path.Combine(repoRoot, "ocr_service", "models", "onnx_models");
var charDir = Path.Combine(repoRoot, "ocr_service", "models", "official_models");

Console.WriteLine($"ONNX dir: {onnxDir}");
Console.WriteLine($"Char dir: {charDir}");

if (!Directory.Exists(onnxDir) || !Directory.Exists(charDir))
{
    Console.WriteLine("ERROR: Model directories not found!");
    return 1;
}

// GPU 模式
Console.WriteLine("\n=== Loading ONNX OCR engine (CUDA GPU) ===");
using var engine = new OnnxOcrEngine(onnxDir, charDir, gpuId: 0, logger);
Console.WriteLine($"Device: {engine.DeviceName} | Ready: {engine.IsReady}\n");

var testDir = Path.Combine(repoRoot, "TestDatas");
var testImages = new[] { "本家.png", "对家.png", "上家.png", "下家.png", "信息.png" };

double cvTotal = 0, srvTotal = 0, srvDetTotal = 0, srvRecTotal = 0;
int cvBoxSum = 0, srvBoxSum = 0;

foreach (var name in testImages)
{
    var path = Path.Combine(testDir, name);
    if (!File.Exists(path)) continue;

    var bytes = File.ReadAllBytes(path);
    using var mat = Cv2.ImDecode(bytes, ImreadModes.Color);
    if (mat is null || mat.Empty()) continue;

    // 预热：先跑一次（不计时）
    engine.CrossValidate(mat);
    engine.Predict(mat, "server");

    // ── Cross Validate ──
    var sw = System.Diagnostics.Stopwatch.StartNew();
    engine.CrossValidate(mat);
    sw.Stop();
    var cvTime = sw.ElapsedMilliseconds;
    var cvTiming = engine.LastTiming!;
    cvTotal += cvTime;
    cvBoxSum += cvTiming.BoxCount;
    Console.WriteLine($"  [CV]  {name,-8}  {cvTiming}");
    Console.Out.Flush();

    // ... Single Model ...
    sw.Restart();
    engine.Predict(mat, "server");
    sw.Stop();
    var srvTime2 = sw.ElapsedMilliseconds;
    var srvTiming = engine.LastTiming!;
    srvTotal += srvTime2;
    srvDetTotal += srvTiming.DetectMs;
    srvRecTotal += srvTiming.RecMs;
    srvBoxSum += srvTiming.BoxCount;
    Console.WriteLine($"  [SNG] {name,-8}  {srvTiming}");
    Console.Out.Flush();
}

// ── 总结 ──
int n = testImages.Length;
Console.WriteLine($"\n{'='*55}");
Console.WriteLine($"  平均耗时对比 ({n} 张图片, {engine.DeviceName})");
Console.WriteLine($"  ┌──────────┬─────────┬─────────┬─────────┬─────────┐");
Console.WriteLine($"  │ 模式     │ 总耗时  │ 检测    │ 识别    │ 检测框  │");
Console.WriteLine($"  ├──────────┼─────────┼─────────┼─────────┼─────────┤");
Console.WriteLine($"  │ 交叉验证 │ {cvTotal/n,6:F0}ms │ {cvBoxSum/n,5:F0}    │ 3模型   │ {cvBoxSum/n,5:F0}    │");
Console.WriteLine($"  │ 单模型   │ {srvTotal/n,6:F0}ms │ {srvDetTotal/n,6:F0}ms │ {srvRecTotal/n,6:F0}ms │ {srvBoxSum/n,5:F0}    │");
Console.WriteLine($"  └──────────┴─────────┴─────────┴─────────┴─────────┘");
Console.WriteLine($"  加速比: 交叉验证 / 单模型 = {cvTotal / srvTotal:F1}x");
Console.WriteLine($"  (交叉验证 = 检测1次 + 识别3次; 单模型 = 检测1次 + 识别1次)");

return 0;
