using Microsoft.Extensions.Logging;
using OcrClient.Core.Models;
using System.Net.Http.Json;

namespace OcrClient.Core.Services;

public class OcrApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OcrApiClient> _logger;

    public OcrApiClient(HttpClient http, AppConfig config, ILogger<OcrApiClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.BaseAddress = new Uri(config.Server.BaseUrl);
    }

    /// <summary>将base64编码的图片发送到/ocr/cross_validate并返回组合结果。</summary>
    public async Task<CrossValidateResult> CrossValidateAsync(string imageBase64, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/ocr/cross_validate", new { image = imageBase64 }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CrossValidateResult>(cancellationToken: ct))!;
    }

    /// <summary>将base64编码的图片发送到/ocr/server_rec（仅服务端模型）。</summary>
    public async Task<OcrSingleResult> RecognizeServerAsync(string imageBase64, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/ocr/server_rec", new { image = imageBase64 }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OcrSingleResult>(cancellationToken: ct))!;
    }

    /// <summary>将base64编码的图片发送到/ocr/mobile_rec（仅移动端模型）。</summary>
    public async Task<OcrSingleResult> RecognizeMobileAsync(string imageBase64, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/ocr/mobile_rec", new { image = imageBase64 }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OcrSingleResult>(cancellationToken: ct))!;
    }

    /// <summary>将base64编码的图片发送到/ocr/en_mobile_rec（仅英文移动端模型）。</summary>
    public async Task<OcrSingleResult> RecognizeEnMobileAsync(string imageBase64, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/ocr/en_mobile_rec", new { image = imageBase64 }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OcrSingleResult>(cancellationToken: ct))!;
    }

    /// <summary>检查OCR服务是否可达。</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
