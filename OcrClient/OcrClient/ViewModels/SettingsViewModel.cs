using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using OcrClient.Core.Models;
using OcrClient.UI.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace OcrClient.UI.ViewModels;

public record EnvCheckResult(string Name, bool IsOk, string Message);

public partial class SettingsViewModel : ViewModel
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly AppConfigService _configService;

    [ObservableProperty]
    private string _baseUrl = "http://localhost:8080";

    [ObservableProperty]
    private int _startupMaxAttempts = 120;

    [ObservableProperty]
    private int _startupPollIntervalMs = 1000;

    [ObservableProperty]
    private int _healthTimeoutSeconds = 3;

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
    private string _serverScript = "server.py";

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
        StartupMaxAttempts = c.Server.StartupMaxAttempts;
        StartupPollIntervalMs = c.Server.StartupPollIntervalMs;
        HealthTimeoutSeconds = c.Server.HealthTimeoutSeconds;
        HealthMonitorIntervalMs = c.Server.HealthMonitorIntervalMs;
        HealthMaxFailures = c.Server.HealthMaxFailures;
        RequestTimeoutSeconds = c.Server.RequestTimeoutSeconds;
        KillExistingOnStartup = c.OcrService.KillExistingOnStartup;
        ServiceDirectory = c.OcrService.ServiceDirectory;
        VenvPath = c.OcrService.VenvPath;
        ServerScript = c.OcrService.ServerScript;
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
        c.Server.StartupMaxAttempts = StartupMaxAttempts;
        c.Server.StartupPollIntervalMs = StartupPollIntervalMs;
        c.Server.HealthTimeoutSeconds = HealthTimeoutSeconds;
        c.Server.HealthMonitorIntervalMs = HealthMonitorIntervalMs;
        c.Server.HealthMaxFailures = HealthMaxFailures;
        c.Server.RequestTimeoutSeconds = RequestTimeoutSeconds;
        c.OcrService.KillExistingOnStartup = KillExistingOnStartup;
        c.OcrService.ServiceDirectory = ServiceDirectory;
        c.OcrService.VenvPath = VenvPath;
        c.OcrService.ServerScript = ServerScript;
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
        var serverDir = Path.IsPathRooted(config.OcrService.ServiceDirectory)
            ? config.OcrService.ServiceDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config.OcrService.ServiceDirectory));

        // 1. Python environment
        var pythonPath = GetSystemPython();
        if (string.IsNullOrEmpty(pythonPath))
        {
            EnvCheckResults.Add(new("Python 环境", false, "未找到系统 Python，请安装 Python 3.12+"));
            OpenUrl("https://www.python.org/downloads/");
        }
        else
        {
            EnvCheckResults.Add(new("Python 环境", true, pythonPath));
        }

        // 2. venv environment
        var venvDir = Path.IsPathRooted(config.OcrService.VenvPath)
            ? config.OcrService.VenvPath
            : Path.Combine(serverDir, config.OcrService.VenvPath);
        var venvPython = Path.Combine(venvDir, "Scripts", "python.exe");
        if (!File.Exists(venvPython))
        {
            EnvCheckResults.Add(new("venv 环境", false, $"虚拟环境不存在: {venvDir}"));
            var batPath = Path.Combine(serverDir, "setup_venv.bat");
            var gpuUrl = "https://www.paddlepaddle.org.cn/packages/stable/cu126/";
            File.WriteAllText(batPath,
                "@echo off\r\n" +
                $"\"{pythonPath}\" -m venv \"{venvDir}\"\r\n" +
                $"\"{venvDir}\\Scripts\\python.exe\" -m pip install --upgrade pip\r\n" +
                $"\"{venvDir}\\Scripts\\python.exe\" -m pip install paddleocr==3.5.0 fastapi uvicorn pillow\r\n" +
                $"\"{venvDir}\\Scripts\\python.exe\" -m pip install paddlepaddle-gpu==3.3.0 -i {gpuUrl}\r\n" +
                "echo Done. Press any key to exit.\r\npause\r\n");
            OpenFolder(serverDir);
            EnvCheckResults.Add(new(" -> 操作", false, $"已创建 setup_venv.bat，请在打开的文件夹中双击执行"));
        }
        else
        {
            EnvCheckResults.Add(new("venv 环境", true, venvDir));
        }

        // 3. Server script
        var serverScript = Path.Combine(serverDir, config.OcrService.ServerScript);
        if (!File.Exists(serverScript))
        {
            EnvCheckResults.Add(new("服务脚本", false, $"未找到: {serverScript}"));
            OpenUrl("https://github.com/zzijin/TextRecognizer/blob/main/ocr_service/server.py");
        }
        else
        {
            EnvCheckResults.Add(new("服务脚本", true, serverScript));
        }

        // 4. Offline models
        var modelsDir = Path.Combine(serverDir, "models", "official_models");
        var requiredModels = new[] { "PP-OCRv5_server_det", "PP-OCRv5_server_rec", "PP-OCRv5_mobile_rec", "en_PP-OCRv5_mobile_rec" };
        var missingModels = requiredModels.Where(m => !Directory.Exists(Path.Combine(modelsDir, m))).ToList();
        if (missingModels.Count > 0)
        {
            EnvCheckResults.Add(new("离线模型", false, $"缺少 {missingModels.Count} 个模型: {string.Join(", ", missingModels)}"));
            var batPath = Path.Combine(serverDir, "download_models.bat");
            var cacheDir = modelsDir.Replace("\\", "\\\\");
            File.WriteAllText(batPath,
                "@echo off\r\n" +
                $"set PADDLE_PDX_CACHE_HOME={Path.GetDirectoryName(modelsDir)?.Replace("\\", "\\\\")}\r\n" +
                $"\"{venvPython}\" -c \"from paddleocr import PaddleOCR; ocr=PaddleOCR(device='gpu', lang='ch')\"\r\n" +
                "echo Done. Press any key to exit.\r\npause\r\n");
            OpenFolder(serverDir);
            EnvCheckResults.Add(new(" -> 操作", false, "已创建 download_models.bat，请在打开的文件夹中双击执行"));
        }
        else
        {
            EnvCheckResults.Add(new("离线模型", true, $"{requiredModels.Length} 个模型就绪"));
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
