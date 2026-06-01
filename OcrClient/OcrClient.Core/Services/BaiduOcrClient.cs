using Microsoft.Extensions.Logging;
using OcrClient.Core.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OcrClient.Core.Services;

/// <summary>百度云OCR API客户端（通用文字识别高精度含位置版）。</summary>
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

    /// <summary>通过client_id/client_secret获取访问令牌，带有30天缓存。</summary>
    public async Task<string?> GetAccessTokenAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry)
            return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // 获取锁后再次检查
            if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry)
                return _cachedToken;

            var url = $"{TokenUrl}?grant_type=client_credentials&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}";
            var response = await _http.PostAsync(url, null, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);

            if (json?.access_token is null)
            {
                _logger.LogError("百度令牌响应缺少access_token");
                return null;
            }

            _cachedToken = json.access_token;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(json.expires_in - 3600); // 在过期前1小时刷新
            _logger.LogInformation("获取到百度访问令牌，{Sec}秒后过期", json.expires_in);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>调用百度OCR API并返回结果。</summary>
    public async Task<OcrSingleResult> RecognizeAsync(string imageBase64, string clientId, string clientSecret, bool accurate = true, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(clientId, clientSecret, ct);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("获取百度访问令牌失败");

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
            _logger.LogWarning("百度OCR返回空结果");
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

        _logger.LogInformation("百度OCR：{Count}条结果", items.Count);
        return new OcrSingleResult { Model = "Baidu Cloud API", Count = items.Count, Items = items };
    }

    // ── JSON模型 ──────────────────────────────────────────────────────────

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
