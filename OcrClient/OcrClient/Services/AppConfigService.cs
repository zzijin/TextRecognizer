using Microsoft.Extensions.Logging;
using OcrClient.Core.Models;
using System.IO;
using System.Text.Json;

namespace OcrClient.UI.Services;

public class AppConfigService
{
    private readonly ILogger<AppConfigService> _logger;
    private readonly string _configDir;
    private readonly string _configPath;

    public AppConfig Config { get; }

    public AppConfigService(ILogger<AppConfigService> logger)
    {
        _logger = logger;
        _configDir = Path.Combine(AppContext.BaseDirectory, "settings");
        _configPath = Path.Combine(_configDir, "appsettings.json");
        Config = Load();
    }

    private AppConfig Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config is not null)
                {
                    _logger.LogInformation("配置已从 {Path} 加载", _configPath);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载配置失败，使用默认值");
        }

        // 创建默认配置并保存
        var defaultConfig = new AppConfig();
        Save(defaultConfig);
        _logger.LogInformation("默认配置已创建于 {Path}", _configPath);
        return defaultConfig;
    }

    public void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(_configDir);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存配置到 {Path} 失败", _configPath);
        }
    }
}
