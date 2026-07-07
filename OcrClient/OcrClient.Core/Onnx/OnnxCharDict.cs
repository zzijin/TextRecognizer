using System.Text.Json;

namespace OcrClient.Core.Onnx;

/// <summary>
/// 字符字典。索引0为空白符（CTC blank），后续索引对应实际字符。
/// 从 char_dict.json 加载（简化格式：["blank", "0", "1", ...]）。
/// </summary>
public class OnnxCharDict
{
    /// <summary>字符列表，索引0是 blank。</summary>
    public IReadOnlyList<string> Characters { get; }

    /// <summary>字典中的字符数（含 blank）。</summary>
    public int Count => Characters.Count;

    private OnnxCharDict(List<string> chars)
    {
        Characters = chars.AsReadOnly();
    }

    /// <summary>创建一个仅含 blank 的空白字典。</summary>
    public static OnnxCharDict CreateEmpty() => new(new List<string> { "blank" });

    /// <summary>
    /// 从 char_dict.json 加载字符字典。
    /// 文件格式：JSON 字符串数组，索引0="blank"，后续为实际字符。
    /// </summary>
    public static OnnxCharDict Load(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var chars = JsonSerializer.Deserialize<List<string>>(json)
            ?? throw new InvalidOperationException($"字符字典格式无效: {jsonPath}");
        return new OnnxCharDict(chars);
    }

    /// <summary>
    /// 将 CTC 输出索引映射为字符串。
    /// 安全的版本：无效索引返回空字符串。
    /// </summary>
    public string MapIndex(int idx)
    {
        if (idx < 0 || idx >= Characters.Count)
            return "";
        return Characters[idx];
    }
}
