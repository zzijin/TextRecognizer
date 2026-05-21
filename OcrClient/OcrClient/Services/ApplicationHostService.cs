using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Wpf.Ui;

namespace OcrClient.UI.Services;

internal class ApplicationHostService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ServerProcessState _serverState;
    private INavigationWindow? _navigationWindow;
    private Process? _pythonProcess;
    private CancellationTokenSource? _serverCts;

    public ApplicationHostService(IServiceProvider serviceProvider, ServerProcessState serverState)
    {
        _serviceProvider = serviceProvider;
        _serverState = serverState;
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

    private async Task StartHealthMonitorAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        int failCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct); } catch { return; }

            try
            {
                var resp = await http.GetAsync("http://localhost:8080/health", ct);
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

            if (failCount >= 3)
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
        _serverState.StatusText = "Starting OCR service...";
        _serverState.IsStarting = true;

        var serverDir = FindOcrServiceDir();
        var pythonExe = Path.Combine(serverDir, "venv", "Scripts", "python.exe");
        var serverScript = Path.Combine(serverDir, "server.py");

        // Kill any existing process on port 8080 (e.g., leftover from manual start)
        KillExistingServer();

        if (!File.Exists(pythonExe) || !File.Exists(serverScript))
        {
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

        _pythonProcess.Start();

        // Poll health endpoint — longer timeout for first-run model downloads
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (int i = 1; i <= 120; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await http.GetAsync("http://localhost:8080/health", ct);
                if (resp.IsSuccessStatusCode)
                {
                    _serverState.StatusText = "OCR service ready";
                    _serverState.IsReady = true;
                    _serverState.IsStarting = false;
                    _ = Task.Run(() => StartHealthMonitorAsync(ct), CancellationToken.None);
                    return;
                }
            }
            catch { }

            _serverState.StatusText = $"Waiting for OCR service... ({i}/120)";
            try { await Task.Delay(1000, ct); } catch { return; }
        }

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

    private static string FindOcrServiceDir()
    {
        var dir = AppContext.BaseDirectory;

        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "ocr_service");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
            dir = Path.GetDirectoryName(dir)!;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ocr_service"));
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
