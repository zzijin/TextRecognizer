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
                    _logger.LogInformation("Config loaded from {Path}", _configPath);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load config, using defaults");
        }

        // Create default config and save it
        var defaultConfig = new AppConfig();
        Save(defaultConfig);
        _logger.LogInformation("Default config created at {Path}", _configPath);
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
            _logger.LogWarning(ex, "Failed to save config to {Path}", _configPath);
        }
    }
}
