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

    /// <summary>Axis-aligned bounding rect derived from the quadrilateral Box.</summary>
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

public class CrossValidateResult
{
    [JsonPropertyName("server_rec")]
    public OcrSingleResult? ServerRec { get; set; }

    [JsonPropertyName("mobile_rec")]
    public OcrSingleResult? MobileRec { get; set; }

    [JsonPropertyName("en_mobile_rec")]
    public OcrSingleResult? EnMobileRec { get; set; }
}

public enum RecognitionMode
{
    CrossValidate,
    ServerRec,
    MobileRec,
    EnMobileRec
}

public record RecognitionModeOption(RecognitionMode Value, string Label)
{
    public RecognitionMode Value { get; } = Value;
    public string Label { get; } = Label;
}

public class CrossValidateGroupItem
{
    public string Model { get; set; } = "";
    public string Text { get; set; } = "";
    public double Score { get; set; }
    public bool IsPlaceholder { get; set; }
    public int Agreement { get; set; }  // 3=all agree, 2=two agree, 1=unique
}

public class CrossValidateGroup : INotifyPropertyChanged
{
    private string _confirmedText = "";
    private bool _isConfirmed;

    public List<CrossValidateGroupItem> Items { get; set; } = [];
    public int Agreement { get; set; }
    public Rect UnionRect { get; set; }          // in server-resized coordinates
    public double ImageScale { get; set; } = 1.0; // server resize factor (1024 / max(w,h))
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
