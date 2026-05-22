using Microsoft.Extensions.Logging;
using OcrClient.Core.Models;
using System.Net.Http.Json;

namespace OcrClient.Core.Services;

public class OcrApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OcrApiClient> _logger;

    public OcrApiClient(HttpClient http, ILogger<OcrApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Send base64 image to /ocr/cross_validate and return combined result.</summary>
    public async Task<CrossValidateResult> CrossValidateAsync(string imageBase64, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/ocr/cross_validate", new { image = imageBase64 }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CrossValidateResult>(cancellationToken: ct))!;
    }

    /// <summary>Send base64 image to /ocr/server_rec only.</summary>
    public async Task<OcrSingleResult> RecognizeServerAsync(string imageBase64, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/ocr/server_rec", new { image = imageBase64 }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OcrSingleResult>(cancellationToken: ct))!;
    }

    /// <summary>Send base64 image to /ocr/mobile_rec only.</summary>
    public async Task<OcrSingleResult> RecognizeMobileAsync(string imageBase64, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/ocr/mobile_rec", new { image = imageBase64 }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OcrSingleResult>(cancellationToken: ct))!;
    }

    /// <summary>Send base64 image to /ocr/en_mobile_rec only.</summary>
    public async Task<OcrSingleResult> RecognizeEnMobileAsync(string imageBase64, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/ocr/en_mobile_rec", new { image = imageBase64 }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OcrSingleResult>(cancellationToken: ct))!;
    }

    /// <summary>Check if the OCR service is reachable.</summary>
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
