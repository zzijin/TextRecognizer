using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using OcrClient.Core.Models;
using OcrClient.UI.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace OcrClient.UI.ViewModels;

public record EnvCheckResult(string Name, bool IsOk, string Message);

public partial class SettingsViewModel : ViewModel
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly AppConfigService _configService;

    [ObservableProperty]
    private string _baseUrl = "http://localhost:8080";

    // ── Engine source ────────────────────────────────────────────────────────

    public record SourceOption(string Value, string Label);

    [ObservableProperty]
    private string _engineSource = "local_service";

    public List<SourceOption> SourceOptions { get; } =
    [
        new("local_service", "本地服务"),
        new("baidu_cloud", "PaddleOCR云服务"),
        new("onnx_csharp", "ONNX For CSharp（待支持）"),
    ];

    public bool IsLocalServiceSelected => EngineSource == "local_service";
    public bool IsBaiduCloudSelected => EngineSource == "baidu_cloud";

    partial void OnEngineSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsLocalServiceSelected));
        OnPropertyChanged(nameof(IsBaiduCloudSelected));
    }

    // ── Local engine ─────────────────────────────────────────────────────────

    public record EngineOption(string Value, string Label);
    public List<EngineOption> EngineOptions { get; } =
    [
        new("onnx_cpu", "ONNX CPU"),
        new("onnx_dml", "ONNX DML (GPU)"),
        new("paddle", "PaddlePaddle (GPU)"),
    ];

    [ObservableProperty]
    private EngineOption _selectedEngineOption = null!;

    // ── Baidu Cloud ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _baiduClientId = "";

    [ObservableProperty]
    private string _baiduClientSecret = "";

    // ── Thresholds ───────────────────────────────────────────────────────────

    [ObservableProperty]
    private double _singleModelAutoConfirmThreshold = 0.99;

    [ObservableProperty]
    private double _singleModelAutoFillThreshold = 0.95;

    [ObservableProperty]
    private double _crossValidateAutoConfirmThreshold = 0.85;

    [ObservableProperty]
    private double _crossValidateAutoFillThreshold = 0.6;

    [ObservableProperty]
    private int _startupMaxAttempts = 120;

    [ObservableProperty]
    private int _startupPollIntervalMs = 1000;

    [ObservableProperty]
    private int _healthTimeoutSeconds = 10;

    [ObservableProperty]
    private int _healthMonitorIntervalMs = 5000;

    [ObservableProperty]
    private int _healthMaxFailures = 3;

    [ObservableProperty]
    private int _requestTimeoutSeconds = 900;

    [ObservableProperty]
    private bool _killExistingOnStartup = true;

    [ObservableProperty]
    private string _serviceDirectory = "ocr_service";

    [ObservableProperty]
    private string _venvPath = "venv";

    [ObservableProperty]
    private bool _capturePythonOutput = true;

    // Logging
    [ObservableProperty]
    private string _logLevel = "Information";

    [ObservableProperty]
    private bool _enableConsoleLog = true;

    [ObservableProperty]
    private bool _enableFileLog = true;

    [ObservableProperty]
    private string _logDirectory = "Logs";

    [ObservableProperty]
    private string _rollingInterval = "Day";

    [ObservableProperty]
    private int _rollingSizeKB = 51200;

    public List<string> LogLevelOptions { get; } = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];
    public List<string> RollingIntervalOptions { get; } = ["Day", "Hour", "Month", "Year"];

    public ObservableCollection<EnvCheckResult> EnvCheckResults { get; } = [];

    [ObservableProperty]
    private string? _statusMessage;

    public SettingsViewModel(ILogger<SettingsViewModel> logger, AppConfigService configService)
    {
        _logger = logger;
        _configService = configService;
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var c = _configService.Config;
        BaseUrl = c.Server.BaseUrl;
        EngineSource = c.Server.EngineSource;
        SelectedEngineOption = EngineOptions.Find(o => o.Value == c.Server.Engine) ?? EngineOptions[0];
        BaiduClientId = c.Server.BaiduClientId;
        BaiduClientSecret = c.Server.BaiduClientSecret;
        SingleModelAutoConfirmThreshold = c.Server.SingleModelAutoConfirmThreshold;
        SingleModelAutoFillThreshold = c.Server.SingleModelAutoFillThreshold;
        CrossValidateAutoConfirmThreshold = c.Server.CrossValidateAutoConfirmThreshold;
        CrossValidateAutoFillThreshold = c.Server.CrossValidateAutoFillThreshold;
        StartupMaxAttempts = c.Server.StartupMaxAttempts;
        StartupPollIntervalMs = c.Server.StartupPollIntervalMs;
        HealthTimeoutSeconds = c.Server.HealthTimeoutSeconds;
        HealthMonitorIntervalMs = c.Server.HealthMonitorIntervalMs;
        HealthMaxFailures = c.Server.HealthMaxFailures;
        RequestTimeoutSeconds = c.Server.RequestTimeoutSeconds;
        KillExistingOnStartup = c.OcrService.KillExistingOnStartup;
        ServiceDirectory = c.OcrService.ServiceDirectory;
        VenvPath = c.OcrService.VenvPath;
        CapturePythonOutput = c.OcrService.CapturePythonOutput;
        LogLevel = c.Logging.LogLevel;
        EnableConsoleLog = c.Logging.EnableConsole;
        EnableFileLog = c.Logging.EnableFile;
        LogDirectory = c.Logging.LogDirectory;
        RollingInterval = c.Logging.RollingInterval;
        RollingSizeKB = c.Logging.RollingSizeKB;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var c = _configService.Config;
        c.Server.BaseUrl = BaseUrl;
        c.Server.EngineSource = EngineSource;
        c.Server.Engine = SelectedEngineOption?.Value ?? "onnx_cpu";
        c.Server.BaiduClientId = BaiduClientId;
        c.Server.BaiduClientSecret = BaiduClientSecret;
        c.Server.SingleModelAutoConfirmThreshold = SingleModelAutoConfirmThreshold;
        c.Server.SingleModelAutoFillThreshold = SingleModelAutoFillThreshold;
        c.Server.CrossValidateAutoConfirmThreshold = CrossValidateAutoConfirmThreshold;
        c.Server.CrossValidateAutoFillThreshold = CrossValidateAutoFillThreshold;
        c.Server.StartupMaxAttempts = StartupMaxAttempts;
        c.Server.StartupPollIntervalMs = StartupPollIntervalMs;
        c.Server.HealthTimeoutSeconds = HealthTimeoutSeconds;
        c.Server.HealthMonitorIntervalMs = HealthMonitorIntervalMs;
        c.Server.HealthMaxFailures = HealthMaxFailures;
        c.Server.RequestTimeoutSeconds = RequestTimeoutSeconds;
        c.OcrService.KillExistingOnStartup = KillExistingOnStartup;
        c.OcrService.ServiceDirectory = ServiceDirectory;
        c.OcrService.VenvPath = VenvPath;
        c.OcrService.CapturePythonOutput = CapturePythonOutput;
        c.Logging.LogLevel = LogLevel;
        c.Logging.EnableConsole = EnableConsoleLog;
        c.Logging.EnableFile = EnableFileLog;
        c.Logging.LogDirectory = LogDirectory;
        c.Logging.RollingInterval = RollingInterval;
        c.Logging.RollingSizeKB = RollingSizeKB;

        _configService.Save(c);
        _logger.LogInformation("Settings saved");
        StatusMessage = "设置已保存，重启客户端生效";
    }

    [RelayCommand]
    private void BrowseServiceDir()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 OCR 服务目录",
            InitialDirectory = Directory.Exists(ServiceDirectory)
                ? ServiceDirectory
                : AppContext.BaseDirectory,
        };
        if (dialog.ShowDialog() == true)
            ServiceDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void ResetSettings()
    {
        var c = new AppConfig();
        _configService.Save(c);
        LoadFromConfig();
        _logger.LogInformation("Settings reset to defaults");
        StatusMessage = "已恢复默认设置";
    }

    [RelayCommand]
    private void CheckEnvironment()
    {
        EnvCheckResults.Clear();
        var config = _configService.Config;
        var engine = SelectedEngineOption?.Value ?? "onnx_cpu";
        var isOnnx = engine is "onnx_dml" or "onnx_cpu";
        var isPaddle = engine == "paddle";
        var isBaidu = engine is "baidu_cloud";
        EnvCheckResults.Add(new("所选引擎", true, engine));

        var serverDir = Path.IsPathRooted(config.OcrService.ServiceDirectory)
            ? config.OcrService.ServiceDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config.OcrService.ServiceDirectory));

        var venvDir = Path.IsPathRooted(config.OcrService.VenvPath)
            ? config.OcrService.VenvPath
            : Path.Combine(serverDir, config.OcrService.VenvPath);
        var venvPython = Path.Combine(venvDir, "Scripts", "python.exe");

        // 1. Python
        var pythonPath = GetSystemPython();
        if (string.IsNullOrEmpty(pythonPath))
        {
            EnvCheckResults.Add(new("Python 环境", false, "未找到系统 Python，请安装 Python 3.12+"));
        }
        else
        {
            EnvCheckResults.Add(new("Python 环境", true, pythonPath));
        }

        // 2. venv + pip packages
        if (!File.Exists(venvPython))
        {
            EnvCheckResults.Add(new("venv 环境", false, $"虚拟环境不存在: {venvDir}"));
            var batPath = Path.Combine(serverDir, "setup_venv.bat");
            var pipPackages = isPaddle
                ? "paddlepaddle-gpu==3.3.0 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/ paddleocr==3.5.0 fastapi uvicorn pillow"
                : engine == "onnx_dml"
                    ? "fastapi uvicorn pillow opencv-python pyclipper numpy onnxruntime-directml"
                    : "fastapi uvicorn pillow opencv-python pyclipper numpy onnxruntime";
            File.WriteAllText(batPath,
                "@echo off\r\n" +
                $"\"{pythonPath}\" -m venv \"{venvDir}\"\r\n" +
                $"\"{venvDir}\\Scripts\\python.exe\" -m pip install --upgrade pip\r\n" +
                $"\"{venvDir}\\Scripts\\python.exe\" -m pip install {pipPackages}\r\n" +
                "echo Done.\r\npause\r\n");
            OpenFolder(serverDir);
            EnvCheckResults.Add(new(" -> setup_venv.bat", false, "已创建，双击执行安装依赖"));
        }
        else
        {
            EnvCheckResults.Add(new("venv 环境", true, venvDir));

            // verify required packages
            var requiredPackages = isPaddle
                ? new[] { "paddlepaddle-gpu", "paddleocr", "fastapi", "uvicorn" }
                : engine == "onnx_dml"
                    ? new[] { "onnxruntime-directml", "fastapi", "uvicorn", "opencv-python", "pyclipper" }
                    : new[] { "onnxruntime", "fastapi", "uvicorn", "opencv-python", "pyclipper" };

            foreach (var pkg in requiredPackages)
            {
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo(venvPython, $"-c \"import {pkg.Replace("-", "_")}\"")
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                    });
                    proc?.WaitForExit(5000);
                    var err = proc?.StandardError.ReadToEnd() ?? "";
                    EnvCheckResults.Add(new($"  pip: {pkg}", proc?.ExitCode == 0, proc?.ExitCode == 0 ? "已安装" : err.Trim()[..Math.Min(err.Trim().Length, 80)]));
                }
                catch (Exception ex)
                {
                    EnvCheckResults.Add(new($"  pip: {pkg}", false, ex.Message));
                }
            }
        }

        // 3. Server script
        if (isPaddle)
        {
            var sp = Path.Combine(serverDir, "server.py");
            EnvCheckResults.Add(new("服务脚本", File.Exists(sp), File.Exists(sp) ? sp : "未找到 server.py"));
        }
        else
        {
            var sp = Path.Combine(serverDir, "server_onnx.py");
            var ocrSp = Path.Combine(serverDir, "onnx_ocr.py");
            EnvCheckResults.Add(new("服务脚本 server_onnx.py", File.Exists(sp), File.Exists(sp) ? sp : "未找到"));
            EnvCheckResults.Add(new("引擎模块 onnx_ocr.py", File.Exists(ocrSp), File.Exists(ocrSp) ? ocrSp : "未找到"));
        }

        // 4. Models
        if (isPaddle)
        {
            var modelsDir = Path.Combine(serverDir, "models", "official_models");
            var requiredModels = new[] { "PP-OCRv5_server_det", "PP-OCRv5_server_rec", "PP-OCRv5_mobile_rec", "en_PP-OCRv5_mobile_rec" };
            var missing = requiredModels.Where(m => !Directory.Exists(Path.Combine(modelsDir, m))).ToList();
            EnvCheckResults.Add(new("PIR 模型", missing.Count == 0,
                missing.Count == 0 ? $"{requiredModels.Length} 个就绪" : $"缺少: {string.Join(", ", missing)}"));
        }
        else
        {
            var onnxDir = Path.Combine(serverDir, "models", "onnx_models");
            var requiredOnnx = new[] { "PP-OCRv5_server_det.onnx", "PP-OCRv5_server_rec.onnx", "PP-OCRv5_mobile_rec.onnx", "en_PP-OCRv5_mobile_rec.onnx" };
            var missing = requiredOnnx.Where(f => !File.Exists(Path.Combine(onnxDir, f))).ToList();
            EnvCheckResults.Add(new("ONNX 模型", missing.Count == 0,
                missing.Count == 0 ? $"{requiredOnnx.Length} 个就绪" : $"缺少: {string.Join(", ", missing)}"));

            // character dicts (needed for CTC decode)
            var dictDir = Path.Combine(serverDir, "models", "official_models");
            var dictModels = new[] { "PP-OCRv5_server_rec", "PP-OCRv5_mobile_rec", "en_PP-OCRv5_mobile_rec" };
            foreach (var dm in dictModels)
            {
                var cp = Path.Combine(dictDir, dm, "config.json");
                EnvCheckResults.Add(new($"字符字典 {dm}", File.Exists(cp), File.Exists(cp) ? cp : "未找到"));
            }
        }

        // 5. GPU / Internet check
        if (isOnnx || isPaddle)
        {
            bool hasD3d = false;
            bool hasCuda = false;
            try { hasD3d = System.Runtime.InteropServices.NativeLibrary.TryLoad("d3d11.dll", out _); } catch { }
            try { hasCuda = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_PATH")); } catch { }

            if (hasD3d && hasCuda)
                EnvCheckResults.Add(new("GPU 检测", true, "DirectX 可用 (DML) + CUDA 环境变量已设置 (Paddle)"));
            else if (hasD3d)
                EnvCheckResults.Add(new("GPU 检测", true, "DirectX 可用 (支持 DML GPU)"));
            else if (hasCuda)
                EnvCheckResults.Add(new("GPU 检测", true, "CUDA 环境变量已设置 (支持 Paddle GPU)"));
            else
                EnvCheckResults.Add(new("GPU 检测", false, "未检测到 DirectX 或 CUDA，GPU加速可能不可用"));
        }

        if (isBaidu)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp = http.Send(new HttpRequestMessage(HttpMethod.Head, "https://aip.baidubce.com"));
                EnvCheckResults.Add(new("网络检测", resp.IsSuccessStatusCode,
                    resp.IsSuccessStatusCode ? "百度云API可达" : $"HTTP {(int)resp.StatusCode}"));
            }
            catch (Exception ex)
            {
                EnvCheckResults.Add(new("网络检测", false, $"百度云API不可达: {ex.Message}"));
            }
        }

        StatusMessage = "环境检测完成";
    }

    private static string? GetSystemPython()
    {
        foreach (var name in new[] { "python", "python3", "py" })
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("where", name)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                });
                if (proc is null) continue;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    return output.Split('\n')[0].Trim();
            }
            catch { }
        }
        return null;
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start("explorer.exe", $"\"{path}\""); } catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
