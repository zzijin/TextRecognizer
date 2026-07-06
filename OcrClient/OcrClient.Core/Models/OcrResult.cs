using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Input;
using OpenCvSharp;

namespace OcrClient.Core.Models;

public class OcrItem
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("box")]
    public List<List<double>>? Box { get; set; }

    /// <summary>从四边形Box导出的轴对齐边界矩形。</summary>
    [JsonIgnore]
    public Rect BoundingRect => Box is null || Box.Count == 0
        ? new Rect(0, 0, 0, 0)
        : new Rect(
            (int)Box.Min(p => p[0]), (int)Box.Min(p => p[1]),
            (int)(Box.Max(p => p[0]) - Box.Min(p => p[0])),
            (int)(Box.Max(p => p[1]) - Box.Min(p => p[1])));
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

/// <summary>单次 OCR 推理各阶段耗时统计（毫秒）。</summary>
public class OcrTiming
{
    /// <summary>图像加载耗时（ms）。</summary>
    public double LoadMs { get; set; }
    /// <summary>检测阶段耗时（ms）：预处理 + 推理 + 后处理。</summary>
    public double DetectMs { get; set; }
    /// <summary>识别阶段耗时（ms）：预处理 + 推理 + CTC 解码。</summary>
    public double RecMs { get; set; }
    /// <summary>总耗时（ms）。</summary>
    public double TotalMs { get; set; }
    /// <summary>检测到的文本区域数。</summary>
    public int BoxCount { get; set; }
    /// <summary>识别模型数。</summary>
    public int ModelCount { get; set; }
    /// <summary>推理设备名称。</summary>
    public string DeviceName { get; set; } = "";

    public override string ToString()
        => $"总{TotalMs:F0}ms | 检测{DetectMs:F0}ms({BoxCount}框) | 识别{RecMs:F0}ms({ModelCount}模型) | {DeviceName}";
}

public class CrossValidateResult
{
    [JsonPropertyName("server_rec")]
    public OcrSingleResult? ServerRec { get; set; }

    [JsonPropertyName("mobile_rec")]
    public OcrSingleResult? MobileRec { get; set; }

    [JsonPropertyName("en_mobile_rec")]
    public OcrSingleResult? EnMobileRec { get; set; }

    [JsonPropertyName("baidu_api_rec")]
    public OcrSingleResult? BaiduApiRec { get; set; }

    [JsonPropertyName("baidu_api_general_rec")]
    public OcrSingleResult? BaiduApiGeneralRec { get; set; }
}

public enum RecognitionMode
{
    CrossValidate,
    ServerRec,
    MobileRec,
    EnMobileRec,
    BaiduApi,
    BaiduApiGeneral,
    BaiduCrossValidate
}

public enum InferenceEngine
{
    Paddle,
    OnnxDml,
    OnnxCpu
}

public record RecognitionModeOption(RecognitionMode Value, string Label)
{
    public RecognitionMode Value { get; } = Value;
    public string Label { get; } = Label;
}

public record InferenceEngineOption(InferenceEngine Value, string Label)
{
    public InferenceEngine Value { get; } = Value;
    public string Label { get; } = Label;
}

public class CrossValidateGroupItem
{
    public string Model { get; set; } = "";
    public string Text { get; set; } = "";
    public double Score { get; set; }
    public bool IsPlaceholder { get; set; }
    /// <summary>加权衰减后的分数（0-1）。由 CrossValidateAligner 计算。</summary>
    public double WeightedScore { get; set; }
    /// <summary>颜色级别：2=绿（≥确认阈值），1=黄（≥填写阈值），0=红（低于阈值）。</summary>
    public int ColorLevel { get; set; }
}

public class CrossValidateGroup : INotifyPropertyChanged
{
    private string _confirmedText = "";
    private bool _isConfirmed;

    public List<CrossValidateGroupItem> Items { get; set; } = [];
    /// <summary>该行最高的加权衰减分数。</summary>
    public double WeightedScore { get; set; }
    public Rect UnionRect { get; set; }          // 在服务器调整大小后的坐标系中
    public double ImageScale { get; set; } = 1.0; // 服务器缩放因子（1024 / max(w,h)）
    [JsonIgnore]
    public Rect ScaledUnionRect => new(
        (int)(UnionRect.X / ImageScale),
        (int)(UnionRect.Y / ImageScale),
        (int)(UnionRect.Width / ImageScale),
        (int)(UnionRect.Height / ImageScale));
    public string SourceImagePath { get; set; } = "";

    public string ConfirmedText
    {
        get => _confirmedText;
        set { _confirmedText = value; OnPropertyChanged(); }
    }

    public bool IsConfirmed
    {
        get => _isConfirmed;
        set { _isConfirmed = value; OnPropertyChanged(); OnPropertyChanged(nameof(NeedsConfirmation)); }
    }

    public bool NeedsConfirmation => !IsConfirmed;

    private bool _isPopupVisible;
    public bool IsPopupVisible
    {
        get => _isPopupVisible;
        set { _isPopupVisible = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public ICommand? ToggleConfirmCommand { get; set; }

    [JsonIgnore]
    public ICommand? TogglePopupCommand { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
