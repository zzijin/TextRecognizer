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
    /// <summary>OCR服务的基础URL。所有引擎共享端口8080。</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>引擎来源："local_service"（本地服务）、"baidu_cloud"（百度云）或"onnx_csharp"（占位符）。</summary>
    [JsonPropertyName("engineSource")]
    public string EngineSource { get; set; } = "local_service";

    /// <summary>本地服务的推理引擎："onnx_cpu"、"onnx_dml"或"paddle"。重启后生效。</summary>
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "onnx_cpu";

    /// <summary>百度云API密钥（client_id）。</summary>
    [JsonPropertyName("baiduClientId")]
    public string BaiduClientId { get; set; } = "";

    /// <summary>百度云秘密密钥（client_secret）。</summary>
    [JsonPropertyName("baiduClientSecret")]
    public string BaiduClientSecret { get; set; } = "";

    /// <summary>单模型：置信度 >= 此值时自动确认（0-1）。</summary>
    [JsonPropertyName("singleModelAutoConfirmThreshold")]
    public double SingleModelAutoConfirmThreshold { get; set; } = 0.99;

    /// <summary>单模型：置信度 >= 此值时自动填写（但不确认）（0-1）。</summary>
    [JsonPropertyName("singleModelAutoFillThreshold")]
    public double SingleModelAutoFillThreshold { get; set; } = 0.95;

    /// <summary>交叉验证：加权得分 >= 此值时自动确认（0-1）。</summary>
    [JsonPropertyName("crossValidateAutoConfirmThreshold")]
    public double CrossValidateAutoConfirmThreshold { get; set; } = 0.85;

    /// <summary>交叉验证：加权得分 >= 此值时自动填写（但不确认）（0-1）。</summary>
    [JsonPropertyName("crossValidateAutoFillThreshold")]
    public double CrossValidateAutoFillThreshold { get; set; } = 0.6;

    /// <summary>交叉验证：衰减系数 α（0-1）。0=不衰减，值越大共识度要求越高。</summary>
    [JsonPropertyName("crossValidateDecayAlpha")]
    public double CrossValidateDecayAlpha { get; set; } = 0.5;

    /// <summary>ONNX C# 推理设备ID。0=第一块GPU，-1=CPU。</summary>
    [JsonPropertyName("onnxCsharpGpuId")]
    public int OnnxCsharpGpuId { get; set; } = 0;

    /// <summary>启动时健康检查轮询的最大尝试次数。</summary>
    [JsonPropertyName("startupMaxAttempts")]
    public int StartupMaxAttempts { get; set; } = 120;

    /// <summary>健康检查轮询间隔（毫秒）。</summary>
    [JsonPropertyName("startupPollIntervalMs")]
    public int StartupPollIntervalMs { get; set; } = 1000;

    /// <summary>健康检查请求超时时间（秒）。</summary>
    [JsonPropertyName("healthTimeoutSeconds")]
    public int HealthTimeoutSeconds { get; set; } = 10;

    /// <summary>运行后健康监控轮询间隔（毫秒）。</summary>
    [JsonPropertyName("healthMonitorIntervalMs")]
    public int HealthMonitorIntervalMs { get; set; } = 1000;

    /// <summary>在报告断开连接之前的连续健康检查失败次数。</summary>
    [JsonPropertyName("healthMaxFailures")]
    public int HealthMaxFailures { get; set; } = 3;

    /// <summary>OCR API请求超时时间（秒）。</summary>
    [JsonPropertyName("requestTimeoutSeconds")]
    public int RequestTimeoutSeconds { get; set; } = 900;
}

public class OcrServiceConfig
{
    /// <summary>启动前是否终止端口上的现有进程。</summary>
    [JsonPropertyName("killExistingOnStartup")]
    public bool KillExistingOnStartup { get; set; } = true;

    /// <summary>OCR服务目录的路径。相对路径从应用程序目录解析。</summary>
    [JsonPropertyName("serviceDirectory")]
    public string ServiceDirectory { get; set; } = "ocr_service";

    /// <summary>Python虚拟环境的相对路径（在ServiceDirectory内）。</summary>
    [JsonPropertyName("venvPath")]
    public string VenvPath { get; set; } = "venv";

    /// <summary>是否将Python的标准输出/错误重定向到客户端日志。</summary>
    [JsonPropertyName("capturePythonOutput")]
    public bool CapturePythonOutput { get; set; } = true;

    /// <summary>ONNX模型文件目录。相对路径从服务目录解析。</summary>
    [JsonPropertyName("onnxModelsDir")]
    public string OnnxModelsDir { get; set; } = "models/onnx_models";

    /// <summary>字符字典模型目录（包含PP-OCRv5_server_rec等子目录的config.json）。相对路径从服务目录解析。</summary>
    [JsonPropertyName("charDictDir")]
    public string CharDictDir { get; set; } = "models/official_models";
}

public class LoggingConfig
{
    /// <summary>最低日志级别。取值：Trace, Debug, Information, Warning, Error, Critical。</summary>
    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Information";

    /// <summary>是否将日志输出到控制台。</summary>
    [JsonPropertyName("enableConsole")]
    public bool EnableConsole { get; set; } = true;

    /// <summary>是否将日志输出到滚动文件。</summary>
    [JsonPropertyName("enableFile")]
    public bool EnableFile { get; set; } = true;

    /// <summary>日志文件目录。相对路径从应用程序目录解析。</summary>
    [JsonPropertyName("logDirectory")]
    public string LogDirectory { get; set; } = "Logs";

    /// <summary>滚动间隔。取值：Day, Hour, Month, Year。</summary>
    [JsonPropertyName("rollingInterval")]
    public string RollingInterval { get; set; } = "Day";

    /// <summary>单个日志文件的最大大小（KB）。</summary>
    [JsonPropertyName("rollingSizeKB")]
    public int RollingSizeKB { get; set; } = 51200;
}
