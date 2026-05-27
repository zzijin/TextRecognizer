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
            _logger.LogInformation("Baidu Cloud mode, skipping local server start");
            _serverState.StatusText = "云端服务就绪";
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
        _logger.LogInformation("Manual restart requested (port {Port})", port);
        _serverCts?.Cancel();
        KillPythonProcess();
        KillPort(port);
        _ = Task.Run(() => StartPythonServerAsync());
    }

    // ── engine → (script, env vars) ──────────────────────────────────────────

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

    // ── health monitor ───────────────────────────────────────────────────────

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

    // ── server process management ────────────────────────────────────────────

    private async Task StartPythonServerAsync()
    {
        var cfg = _configService.Config;
        var engine = cfg.Server.Engine;
        var port = GetPortFromUrl(cfg.Server.BaseUrl);
        var (script, envVars) = GetEngineConfig(engine, port);

        _logger.LogInformation("Starting {Script} (engine={Engine})", script, engine);
        _serverState.StatusText = "Starting OCR service...";
        _serverState.IsStarting = true;

        var serverDir = ResolveServiceDirectory(cfg.OcrService.ServiceDirectory);
        var venvDir = Path.IsPathRooted(cfg.OcrService.VenvPath)
            ? cfg.OcrService.VenvPath
            : Path.Combine(serverDir, cfg.OcrService.VenvPath);
        var pythonExe = Path.Combine(venvDir, "Scripts", "python.exe");
        var serverScript = Path.Combine(serverDir, script);
        _logger.LogInformation("ServerDir={ServerDir}, Python={Python}, Script={Script}", serverDir, pythonExe, serverScript);

        if (cfg.OcrService.KillExistingOnStartup)
            KillPort(port);

        if (!File.Exists(pythonExe) || !File.Exists(serverScript))
        {
            _logger.LogError("Python or server script not found: {Python}, {Script}", pythonExe, serverScript);
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
            _pythonProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) _logger.LogInformation("OCR | {Data}", e.Data); };
            _pythonProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) _logger.LogWarning("OCR | {Data}", e.Data); };
        }

        _logger.LogInformation("Starting: {Python} {Args} [dir={Dir}]",
            pythonExe, _pythonProcess.StartInfo.Arguments, serverDir);
        _pythonProcess.Start();
        _pythonProcess.BeginOutputReadLine();
        _pythonProcess.BeginErrorReadLine();
        _logger.LogInformation("Python PID={Pid}, healthUrl={HealthUrl}", _pythonProcess.Id, $"{cfg.Server.BaseUrl}/health");

        // Give the process a moment to fail fast, then check
        await Task.Delay(2000);
        if (_pythonProcess.HasExited)
        {
            _logger.LogError("Python process exited immediately with code {Code}", _pythonProcess.ExitCode);
            _serverState.StatusText = "OCR service crashed on startup";
            _serverState.IsStarting = false;
            _serverState.HasError = true;
            return;
        }

        // Poll health
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
                    _logger.LogInformation("OCR service healthy after {Attempt} attempts", i);
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
                _logger.LogWarning("Health check attempt {I}/{Max}: {Error}", i, sc.StartupMaxAttempts, lastError);
            try { await Task.Delay(sc.StartupPollIntervalMs, ct); } catch { return; }
        }

        _logger.LogError("OCR service timed out. Last error: {Error}", lastError);

        _logger.LogError("OCR service timed out after {Attempts} attempts", sc.StartupMaxAttempts);
        _serverState.StatusText = "OCR service timeout";
        _serverState.IsStarting = false;
        _serverState.HasError = true;
    }

    // ── process cleanup ──────────────────────────────────────────────────────

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

    // ── UI helpers ───────────────────────────────────────────────────────────

    private static string ResolveServiceDirectory(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;

        // Try relative to app base (works for published single-file)
        var appDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(appDir, configured));
        if (Directory.Exists(candidate)) return candidate;

        // Walk up from app base to find ocr_service (development builds)
        var dir = appDir;
        for (int i = 0; i < 6; i++)
        {
            dir = Path.GetDirectoryName(dir);
            if (dir is null) break;
            candidate = Path.GetFullPath(Path.Combine(dir, configured));
            if (Directory.Exists(candidate)) return candidate;
        }

        // Fallback to the original path
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
