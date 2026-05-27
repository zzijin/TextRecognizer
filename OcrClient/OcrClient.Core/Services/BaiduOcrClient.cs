using Microsoft.Extensions.Logging;
using OcrClient.Core.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OcrClient.Core.Services;

/// <summary>Baidu Cloud OCR API client (通用文字识别高精度含位置版).</summary>
public class BaiduOcrClient
{
    private readonly HttpClient _http;
    private readonly ILogger<BaiduOcrClient> _logger;
    private const string TokenUrl = "https://aip.baidubce.com/oauth/2.0/token";
    private const string OcrAccurateUrl = "https://aip.baidubce.com/rest/2.0/ocr/v1/accurate";
    private const string OcrGeneralUrl = "https://aip.baidubce.com/rest/2.0/ocr/v1/general";

    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public BaiduOcrClient(HttpClient http, ILogger<BaiduOcrClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Get access token from client_id/client_secret, with 30-day cache.</summary>
    public async Task<string?> GetAccessTokenAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry)
            return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry)
                return _cachedToken;

            var url = $"{TokenUrl}?grant_type=client_credentials&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}";
            var response = await _http.PostAsync(url, null, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);

            if (json?.access_token is null)
            {
                _logger.LogError("Baidu token response missing access_token");
                return null;
            }

            _cachedToken = json.access_token;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(json.expires_in - 3600); // refresh 1h before expiry
            _logger.LogInformation("Baidu access token obtained, expires in {Sec}s", json.expires_in);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Call Baidu OCR API and return results.</summary>
    public async Task<OcrSingleResult> RecognizeAsync(string imageBase64, string clientId, string clientSecret, bool accurate = true, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(clientId, clientSecret, ct);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Failed to obtain Baidu access token");

        var baseUrl = accurate ? OcrAccurateUrl : OcrGeneralUrl;
        var url = $"{baseUrl}?access_token={token}";
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["image"] = imageBase64,
            ["vertexes_location"] = "true",
            ["probability"] = "true",
        });

        var response = await _http.PostAsync(url, body, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<OcrResponse>(cancellationToken: ct);

        if (json?.words_result is null)
        {
            _logger.LogWarning("Baidu OCR returned empty result");
            return new OcrSingleResult { Model = "Baidu Cloud API", Count = 0, Items = [] };
        }

        var items = new List<OcrItem>();
        foreach (var wr in json.words_result)
        {
            if (string.IsNullOrEmpty(wr.words)) continue;
            var loc = wr.location;

            var box = new List<List<double>>
            {
                new() { (double)loc.left, (double)loc.top },
                new() { (double)(loc.left + loc.width), (double)loc.top },
                new() { (double)(loc.left + loc.width), (double)(loc.top + loc.height) },
                new() { (double)loc.left, (double)(loc.top + loc.height) },
            };

            double score = wr.probability?.average ?? 1.0;
            items.Add(new OcrItem { Text = wr.words, Score = score, Box = box });
        }

        _logger.LogInformation("Baidu OCR: {Count} results", items.Count);
        return new OcrSingleResult { Model = "Baidu Cloud API", Count = items.Count, Items = items };
    }

    // ── JSON models ──────────────────────────────────────────────────────────

    private class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? access_token { get; set; }

        [JsonPropertyName("expires_in")]
        public int expires_in { get; set; }
    }

    private class OcrResponse
    {
        [JsonPropertyName("words_result")]
        public List<WordResult>? words_result { get; set; }
    }

    private class WordResult
    {
        [JsonPropertyName("words")]
        public string? words { get; set; }

        [JsonPropertyName("location")]
        public OcrLocation location { get; set; } = new();

        [JsonPropertyName("probability")]
        public OcrProbability? probability { get; set; }
    }

    private class OcrLocation
    {
        [JsonPropertyName("left")]
        public int left { get; set; }
        [JsonPropertyName("top")]
        public int top { get; set; }
        [JsonPropertyName("width")]
        public int width { get; set; }
        [JsonPropertyName("height")]
        public int height { get; set; }
    }

    private class OcrProbability
    {
        [JsonPropertyName("average")]
        public double average { get; set; }
    }
}
