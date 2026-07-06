using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Wpf.Ui;

namespace OcrClient.UI.Services;

public class ApplicationHostService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ServerProcessState _serverState;
    private readonly ILogger<ApplicationHostService> _logger;
    private readonly AppConfigService _configService;
    private INavigationWindow? _navigationWindow;
    private Process? _pythonProcess;
    private CancellationTokenSource? _serverCts;

    public ApplicationHostService(IServiceProvider serviceProvider, ServerProcessState serverState, ILogger<ApplicationHostService> logger, AppConfigService configService)
    {
        _serviceProvider = serviceProvider;
        _serverState = serverState;
        _logger = logger;
        _configService = configService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_configService.Config.Server.EngineSource == "baidu_cloud")
        {
            _logger.LogInformation("百度云模式，跳过本地服务启动");
            _serverState.StatusText = "云端服务就绪";
            _serverState.IsReady = true;
            _serverState.IsStarting = false;
            await HandleActivationAsync();
            return;
        }

        if (_configService.Config.Server.EngineSource == "onnx_csharp")
        {
            _logger.LogInformation("ONNX C# 引擎模式，跳过 Python 服务启动");
            _serverState.StatusText = "OCR service ready";
            _serverState.IsReady = true;
            _serverState.IsStarting = false;
            await HandleActivationAsync();
            return;
        }

        _serverState.StatusText = "Connecting...";
        _serverState.IsStarting = true;
        await HandleActivationAsync();
        _ = Task.Run(() => StartPythonServerAsync(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _serverCts?.Cancel();
        KillPythonProcess();
        return Task.CompletedTask;
    }

    public void Restart()
    {
        var port = GetPortFromUrl(_configService.Config.Server.BaseUrl);
        _logger.LogInformation("手动请求重启（端口 {Port}）", port);
        _serverCts?.Cancel();
        KillPythonProcess();
        KillPort(port);
        _ = Task.Run(() => StartPythonServerAsync());
    }

    // ── 引擎 → (脚本, 环境变量) ──────────────────────────────────────────

    private static (string script, Dictionary<string, string> env) GetEngineConfig(string engine, int port)
    {
        var portStr = port.ToString();
        return engine switch
        {
            "paddle" => ("server.py", new Dictionary<string, string>()),
            "onnx_dml" => ("server_onnx.py", new Dictionary<string, string>
            {
                ["ONNX_DEVICE"] = "dml",
                ["ONNX_PORT"] = portStr,
            }),
            _ => ("server_onnx.py", new Dictionary<string, string>
            {
                ["ONNX_DEVICE"] = "cpu",
                ["ONNX_PORT"] = portStr,
            }),
        };
    }

    private static int GetPortFromUrl(string url)
    {
        try { return new Uri(url).Port; }
        catch { return 8081; }
    }

    // ── 健康监控 ───────────────────────────────────────────────────────

    private async Task StartHealthMonitorAsync(CancellationToken ct)
    {
        var cfg = _configService.Config.Server;
        var healthUrl = $"{cfg.BaseUrl}/health";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.HealthTimeoutSeconds) };
        int failCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(cfg.HealthMonitorIntervalMs, ct); } catch { return; }

            try
            {
                var resp = await http.GetAsync(healthUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    if (!_serverState.IsReady || failCount > 0)
                    {
                        _serverState.StatusText = "OCR service ready";
                        _serverState.IsReady = true;
                        _serverState.IsStarting = false;
                        _serverState.HasError = false;
                    }
                    failCount = 0;
                }
                else failCount++;
            }
            catch { failCount++; }

            if (failCount >= cfg.HealthMaxFailures)
            {
                if (_serverState.IsReady)
                {
                    _serverState.StatusText = "OCR service disconnected";
                    _serverState.IsReady = false;
                    _serverState.HasError = true;
                }
            }
        }
    }

    // ── 服务进程管理 ────────────────────────────────────────────

    private async Task StartPythonServerAsync()
    {
        var cfg = _configService.Config;
        var engine = cfg.Server.Engine;
        var port = GetPortFromUrl(cfg.Server.BaseUrl);
        var (script, envVars) = GetEngineConfig(engine, port);

        _logger.LogInformation("正在启动 {Script}（引擎={Engine}）", script, engine);
        _serverState.StatusText = "Starting OCR service...";
        _serverState.IsStarting = true;

        var serverDir = ResolveServiceDirectory(cfg.OcrService.ServiceDirectory);
        var venvDir = Path.IsPathRooted(cfg.OcrService.VenvPath)
            ? cfg.OcrService.VenvPath
            : Path.Combine(serverDir, cfg.OcrService.VenvPath);
        var pythonExe = Path.Combine(venvDir, "Scripts", "python.exe");
        var serverScript = Path.Combine(serverDir, script);
        _logger.LogInformation("服务目录={ServerDir}，Python={Python}，脚本={Script}", serverDir, pythonExe, serverScript);

        if (cfg.OcrService.KillExistingOnStartup)
            KillPort(port);

        if (!File.Exists(pythonExe) || !File.Exists(serverScript))
        {
            _logger.LogError("找不到 Python 或服务脚本：{Python}，{Script}", pythonExe, serverScript);
            _serverState.StatusText = "OCR service not found";
            _serverState.IsStarting = false;
            _serverState.HasError = true;
            return;
        }

        _serverCts = new CancellationTokenSource();
        var ct = _serverCts.Token;

        _pythonProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{serverScript}\"",
                WorkingDirectory = serverDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };

        foreach (var kv in envVars)
            _pythonProcess.StartInfo.Environment[kv.Key] = kv.Value;

        if (cfg.OcrService.CapturePythonOutput)
        {
            _pythonProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) _logger.LogInformation("OCR输出 | {Data}", e.Data); };
            _pythonProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) _logger.LogWarning("OCR错误 | {Data}", e.Data); };
        }

        _logger.LogInformation("启动中：{Python} {Args} [目录={Dir}]",
            pythonExe, _pythonProcess.StartInfo.Arguments, serverDir);
        _pythonProcess.Start();
        _pythonProcess.BeginOutputReadLine();
        _pythonProcess.BeginErrorReadLine();
        _logger.LogInformation("Python 进程ID={Pid}，健康检查地址={HealthUrl}", _pythonProcess.Id, $"{cfg.Server.BaseUrl}/health");

        // 给进程一点时间以快速失败，然后检查
        await Task.Delay(2000);
        if (_pythonProcess.HasExited)
        {
            _logger.LogError("Python 进程立即退出，退出码 {Code}", _pythonProcess.ExitCode);
            _serverState.StatusText = "OCR service crashed on startup";
            _serverState.IsStarting = false;
            _serverState.HasError = true;
            return;
        }

        // 轮询健康状态
        var sc = cfg.Server;
        var healthUrl = $"{sc.BaseUrl}/health";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(sc.HealthTimeoutSeconds) };
        string lastError = "";
        for (int i = 1; i <= sc.StartupMaxAttempts; i++)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var resp = await http.GetAsync(healthUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("OCR 服务在第 {Attempt} 次尝试后正常", i);
                    _serverState.StatusText = "OCR service ready";
                    _serverState.IsReady = true;
                    _serverState.IsStarting = false;
                    _ = Task.Run(() => StartHealthMonitorAsync(ct), CancellationToken.None);
                    return;
                }
                lastError = $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex)
            {
                lastError = ex.InnerException?.Message ?? ex.Message;
            }

            _serverState.StatusText = $"Waiting for OCR service... ({i}/{sc.StartupMaxAttempts})";
            if (i % 5 == 0)
                _logger.LogWarning("健康检查尝试 {I}/{Max}：{Error}", i, sc.StartupMaxAttempts, lastError);
            try { await Task.Delay(sc.StartupPollIntervalMs, ct); } catch { return; }
        }

        _logger.LogError("OCR 服务超时。最后错误：{Error}", lastError);

        _logger.LogError("OCR 服务在 {Attempts} 次尝试后超时", sc.StartupMaxAttempts);
        _serverState.StatusText = "OCR service timeout";
        _serverState.IsStarting = false;
        _serverState.HasError = true;
    }

    // ── 进程清理 ──────────────────────────────────────────────────────

    private void KillPythonProcess()
    {
        if (_pythonProcess is { HasExited: false })
        {
            try { _pythonProcess.Kill(entireProcessTree: true); } catch { }
        }
        _pythonProcess?.Dispose();
        _pythonProcess = null;
    }

    private static void KillPort(int port)
    {
        try
        {
            var pid = GetProcessOnPort(port);
            if (pid > 0)
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch { }
    }

    private static int GetProcessOnPort(int port)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd",
                    Arguments = $"/c netstat -ano | findstr :{port} | findstr LISTENING",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1000);

            var parts = output.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid))
                return pid;
        }
        catch { }
        return 0;
    }

    // ── UI 辅助 ───────────────────────────────────────────────────────────

    public static string ResolveServiceDirectory(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;

        // 尝试相对于应用程序基目录（适用于发布后的单文件模式）
        var appDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(appDir, configured));
        if (Directory.Exists(candidate)) return candidate;

        // 从应用程序基目录向上遍历以查找 ocr_service（开发构建）
        var dir = appDir;
        for (int i = 0; i < 6; i++)
        {
            dir = Path.GetDirectoryName(dir);
            if (dir is null) break;
            candidate = Path.GetFullPath(Path.Combine(dir, configured));
            if (Directory.Exists(candidate)) return candidate;
        }

        // 回退到原始路径
        return Path.GetFullPath(Path.Combine(appDir, configured));
    }

    private async Task HandleActivationAsync()
    {
        if (!Application.Current.Windows.OfType<MainWindow>().Any())
        {
            _navigationWindow = (_serviceProvider.GetService(typeof(INavigationWindow)) as INavigationWindow)!;
            _navigationWindow!.ShowWindow();
            _navigationWindow.Navigate(typeof(Views.HomePage));
        }
        await Task.CompletedTask;
    }
}
