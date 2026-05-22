using Microsoft.Extensions.Logging;
using OcrClient.Core.Models;
using ZLogger;
using ZLogger.Formatters;
using ZLogger.Providers;

namespace OcrClient.Core.Services;

public static class LoggingExtensions
{
    public static ILoggingBuilder AddClientLogging(this ILoggingBuilder builder, LoggingConfig config)
    {
        builder.ClearProviders();

        var level = Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var parsed)
            ? parsed : LogLevel.Information;
        builder.SetMinimumLevel(level);

        if (config.EnableConsole)
        {
            builder.AddZLoggerConsole(options =>
            {
                options.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter(
                        $"{0:yyyy-MM-dd HH:mm:ss.fff}|{1:short}|{2}|",
                        (in MessageTemplate template, in LogInfo info) =>
                            template.Format(info.Timestamp, info.LogLevel, info.Category.Name));
                    formatter.SetSuffixFormatter(
                        $" ({0})",
                        (in MessageTemplate template, in LogInfo info) =>
                            template.Format(info.Category));
                    formatter.SetExceptionFormatter(
                        (writer, ex) =>
                            Utf8StringInterpolation.Utf8String.Format(writer, $"异常: {ex.Message}"));
                });
            });
        }

        if (config.EnableFile)
        {
            builder.AddZLoggerRollingFile(options =>
            {
                options.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter(
                        $"{0:yyyy-MM-dd HH:mm:ss.fff}|{1:short}|{2}|",
                        (in MessageTemplate template, in LogInfo info) =>
                            template.Format(info.Timestamp, info.LogLevel, info.Category.Name));
                    formatter.SetSuffixFormatter(
                        $" ({0})",
                        (in MessageTemplate template, in LogInfo info) =>
                            template.Format(info.Category));
                    formatter.SetExceptionFormatter(
                        (writer, ex) =>
                            Utf8StringInterpolation.Utf8String.Format(writer, $"异常: {ex.Message}"));
                });
                options.FilePathSelector = (timestamp, sequenceNumber) =>
                {
                    var dateStr = timestamp.ToLocalTime().ToString("yyyyMMdd");
                    var seqStr = sequenceNumber.ToString("000");
                    return $"{config.LogDirectory}/OCR.Client-{dateStr}-{seqStr}.log";
                };
                options.RollingInterval = Enum.TryParse<RollingInterval>(config.RollingInterval, ignoreCase: true, out var ri)
                    ? ri : RollingInterval.Day;
                options.RollingSizeKB = (int)config.RollingSizeKB;
            });
        }

        return builder;
    }
}
