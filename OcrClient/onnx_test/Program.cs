using Microsoft.Extensions.Logging;
using OcrClient.Core.Onnx;
using OpenCvSharp;

// Copy CUDA DLLs
var tileMindDep = @"E:\Code\mahjong_tool\TileMind\Dependency";
var outDir = AppContext.BaseDirectory;
var nativeDir = Path.Combine(outDir, "runtimes", "win-x64", "native");
if (Directory.Exists(tileMindDep))
{
    Directory.CreateDirectory(nativeDir);
    foreach (var f in Directory.GetFiles(tileMindDep, "*.dll"))
    {
        var name = Path.GetFileName(f);
        try { File.Copy(f, Path.Combine(outDir, name), overwrite: true); } catch { }
        try { File.Copy(f, Path.Combine(nativeDir, name), overwrite: true); } catch { }
    }
}

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information));
var logger = loggerFactory.CreateLogger<OnnxOcrEngine>();

// Use new directory structure
var modelsDir = @"E:\Code\NumberRecognizer\ocr_service\models";

Console.WriteLine("=== ONNX OCR Engine (GPU) ===");
using var engine = new OnnxOcrEngine(modelsDir, gpuId: 0, logger);
Console.WriteLine($"Device: {engine.DeviceName}");
Console.WriteLine($"Det models: {string.Join(", ", engine.DetModels)}");
Console.WriteLine($"Rec models: {string.Join(", ", engine.RecModels)}");
Console.WriteLine($"Ready: {engine.IsReady}\n");

if (!engine.IsReady) return 1;

var testDir = Path.Combine(@"E:\Code\NumberRecognizer", "TestDatas");
var testImages = new[] { "本家.png", "对家.png", "上家.png", "下家.png", "信息.png" };

// Warmup
var warmBytes = File.ReadAllBytes(Path.Combine(testDir, testImages[0]));
using var warmMat = Cv2.ImDecode(warmBytes, ImreadModes.Color);
engine.CrossValidate(warmMat);

double cvTotal = 0, srvTotal = 0;

foreach (var name in testImages)
{
    var path = Path.Combine(testDir, name);
    var bytes = File.ReadAllBytes(path);
    using var mat = Cv2.ImDecode(bytes, ImreadModes.Color);

    // Cross Validate
    engine.CrossValidate(mat);
    var cvTiming = engine.LastTiming!;
    cvTotal += cvTiming.TotalMs;
    Console.WriteLine($"[CV]  {name,-8}  {cvTiming}");
    Console.Out.Flush();

    // Single Model (first rec model)
    engine.Predict(mat, recIdx: 0);
    var srvTiming = engine.LastTiming!;
    srvTotal += srvTiming.TotalMs;
    Console.WriteLine($"[S ]  {name,-8}  {srvTiming}");
    Console.Out.Flush();
}

int n = testImages.Length;
Console.WriteLine($"\n=== Average ({n} images, {engine.DeviceName}) ===");
Console.WriteLine($"  Cross-validate: {cvTotal / n,6:F0}ms");
Console.WriteLine($"  Single model:   {srvTotal / n,6:F0}ms");

return 0;
