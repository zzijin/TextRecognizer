using System.Text.Json.Serialization;

namespace OcrClient.Core.Models;

public class OcrItem
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("box")]
    public List<List<double>>? Box { get; set; }
}

public class OcrSingleResult
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("items")]
    public List<OcrItem> Items { get; set; } = [];
}

public class CrossValidateResult
{
    [JsonPropertyName("server_rec")]
    public OcrSingleResult? ServerRec { get; set; }

    [JsonPropertyName("en_mobile_rec")]
    public OcrSingleResult? EnMobileRec { get; set; }
}
