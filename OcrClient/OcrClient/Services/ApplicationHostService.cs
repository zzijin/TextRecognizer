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
        _serverState.StatusText = "Connecting...";
        _serverState.IsStarting = true;
        await HandleActivationAsync();
        _ = Task.Run(() => StartPythonServerAsync(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _serverCts?.Cancel();
        StopPythonServer();
        return Task.CompletedTask;
    }

    public void Restart()
    {
        _logger.LogInformation("Manual restart requested");
        _serverCts?.Cancel();
        StopPythonServer();
        _ = Task.Run(() => StartPythonServerAsync());
    }

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
                else
                {
                    failCount++;
                }
            }
            catch
            {
                failCount++;
            }

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

    private async Task StartPythonServerAsync()
    {
        var cfg = _configService.Config;
        _logger.LogInformation("Starting OCR Python server");
        _serverState.StatusText = "Starting OCR service...";
        _serverState.IsStarting = true;

        var serverDir = ResolveServiceDirectory(cfg.OcrService.ServiceDirectory);
        var venvDir = Path.IsPathRooted(cfg.OcrService.VenvPath)
            ? cfg.OcrService.VenvPath
            : Path.Combine(serverDir, cfg.OcrService.VenvPath);
        var pythonExe = Path.Combine(venvDir, "Scripts", "python.exe");
        var serverScript = Path.Combine(serverDir, cfg.OcrService.ServerScript);
        _logger.LogInformation("ServerDir={ServerDir}, Python={Python}, Script={Script}", serverDir, pythonExe, serverScript);

        // Kill any existing process on port 8080
        if (cfg.OcrService.KillExistingOnStartup)
            KillExistingServer();

        if (!File.Exists(pythonExe) || !File.Exists(serverScript))
        {
            _logger.LogError("Python or server script not found");
            _serverState.StatusText = "OCR service not found";
            _serverState.IsStarting = false;
            _serverState.HasError = true;
            return;
        }

        // Clean stale filelock locks from previous killed processes
        CleanStaleLocks();

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

        _pythonProcess.StartInfo.Environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";
        _pythonProcess.StartInfo.Environment["PADDLE_PDX_CACHE_HOME"] = Path.Combine(serverDir, "models");

        if (cfg.OcrService.CapturePythonOutput)
        {
            _pythonProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) _logger.LogInformation("OCR Server | {Data}", e.Data); };
            _pythonProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) _logger.LogWarning("OCR Server | {Data}", e.Data); };
        }
        _pythonProcess.Start();
        _pythonProcess.BeginOutputReadLine();
        _pythonProcess.BeginErrorReadLine();
        _logger.LogInformation("Python process started, PID={Pid}", _pythonProcess.Id);

        // Poll health endpoint
        var sc = cfg.Server;
        var healthUrl = $"{sc.BaseUrl}/health";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(sc.HealthTimeoutSeconds) };
        for (int i = 1; i <= sc.StartupMaxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
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
            }
            catch { }

            _serverState.StatusText = $"Waiting for OCR service... ({i}/{sc.StartupMaxAttempts})";
            try { await Task.Delay(sc.StartupPollIntervalMs, ct); } catch { return; }
        }

        _logger.LogError("OCR service timed out after {Attempts} attempts", sc.StartupMaxAttempts);
        _serverState.StatusText = "OCR service timeout";
        _serverState.IsStarting = false;
        _serverState.HasError = true;
    }

    private static void CleanStaleLocks()
    {
        // Clean locks from both user profile and project-local cache
        var lockDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".paddlex", "locks"),
        };

        foreach (var locksDir in lockDirs)
        {
            if (!Directory.Exists(locksDir)) continue;
            try
            {
                foreach (var lockFile in Directory.GetFiles(locksDir, "*.lock", SearchOption.AllDirectories))
                {
                    try { File.Delete(lockFile); } catch { }
                }
            }
            catch { }
        }
    }

    private static void KillExistingServer()
    {
        try
        {
            var existingPid = GetProcessOnPort(8080);
            if (existingPid > 0)
            {
                using var proc = Process.GetProcessById(existingPid);
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

    private void StopPythonServer()
    {
        if (_pythonProcess is { HasExited: false })
        {
            _pythonProcess.Kill(entireProcessTree: true);
            _pythonProcess.Dispose();
        }
    }

    private static string ResolveServiceDirectory(string configured)
    {
        if (Path.IsPathRooted(configured))
            return configured;

        // Resolve relative to app directory
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }

    private async Task HandleActivationAsync()
    {
        await Task.CompletedTask;

        if (!Application.Current.Windows.OfType<MainWindow>().Any())
        {
            _navigationWindow = (_serviceProvider.GetService(typeof(INavigationWindow)) as INavigationWindow)!;
            _navigationWindow!.ShowWindow();
            _navigationWindow.Navigate(typeof(Views.HomePage));
        }
    }
}
