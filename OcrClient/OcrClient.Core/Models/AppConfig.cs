using System.Text.Json.Serialization;

namespace OcrClient.Core.Models;

public class AppConfig
{
    [JsonPropertyName("server")]
    public ServerConfig Server { get; set; } = new();

    [JsonPropertyName("ocrService")]
    public OcrServiceConfig OcrService { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; set; } = new();
}

public class ServerConfig
{
    /// <summary>Base URL of the OCR service.</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Max number of health-check poll attempts during startup.</summary>
    [JsonPropertyName("startupMaxAttempts")]
    public int StartupMaxAttempts { get; set; } = 120;

    /// <summary>Interval (ms) between health-check polls.</summary>
    [JsonPropertyName("startupPollIntervalMs")]
    public int StartupPollIntervalMs { get; set; } = 1000;

    /// <summary>Health-check request timeout in seconds.</summary>
    [JsonPropertyName("healthTimeoutSeconds")]
    public int HealthTimeoutSeconds { get; set; } = 3;

    /// <summary>Interval (ms) between health-monitor polls once running.</summary>
    [JsonPropertyName("healthMonitorIntervalMs")]
    public int HealthMonitorIntervalMs { get; set; } = 1000;

    /// <summary>Consecutive health-check failures before reporting disconnected.</summary>
    [JsonPropertyName("healthMaxFailures")]
    public int HealthMaxFailures { get; set; } = 3;

    /// <summary>OCR API request timeout in seconds.</summary>
    [JsonPropertyName("requestTimeoutSeconds")]
    public int RequestTimeoutSeconds { get; set; } = 900;
}

public class OcrServiceConfig
{
    /// <summary>Whether to kill existing process on the port before starting.</summary>
    [JsonPropertyName("killExistingOnStartup")]
    public bool KillExistingOnStartup { get; set; } = true;

    /// <summary>Path to the OCR service directory. Relative paths are resolved from the app directory.</summary>
    [JsonPropertyName("serviceDirectory")]
    public string ServiceDirectory { get; set; } = "ocr_service";

    /// <summary>Python virtualenv relative path (within ServiceDirectory).</summary>
    [JsonPropertyName("venvPath")]
    public string VenvPath { get; set; } = "venv";

    /// <summary>Python server script name (within ServiceDirectory).</summary>
    [JsonPropertyName("serverScript")]
    public string ServerScript { get; set; } = "server.py";

    /// <summary>Whether to redirect Python stdout/stderr to the client log.</summary>
    [JsonPropertyName("capturePythonOutput")]
    public bool CapturePythonOutput { get; set; } = true;
}

public class LoggingConfig
{
    /// <summary>Minimum log level. Values: Trace, Debug, Information, Warning, Error, Critical.</summary>
    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Information";

    /// <summary>Whether to output logs to console.</summary>
    [JsonPropertyName("enableConsole")]
    public bool EnableConsole { get; set; } = true;

    /// <summary>Whether to output logs to rolling files.</summary>
    [JsonPropertyName("enableFile")]
    public bool EnableFile { get; set; } = true;

    /// <summary>Log file directory. Relative paths are resolved from the app directory.</summary>
    [JsonPropertyName("logDirectory")]
    public string LogDirectory { get; set; } = "Logs";

    /// <summary>Rolling interval. Values: Day, Hour, Month, Year.</summary>
    [JsonPropertyName("rollingInterval")]
    public string RollingInterval { get; set; } = "Day";

    /// <summary>Max single log file size in KB.</summary>
    [JsonPropertyName("rollingSizeKB")]
    public int RollingSizeKB { get; set; } = 51200;
}
