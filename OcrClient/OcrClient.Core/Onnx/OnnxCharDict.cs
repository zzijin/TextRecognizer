using System.Text.Json;

namespace OcrClient.Core.Onnx;

/// <summary>
/// 从 PaddleX 模型的 config.json 加载字符字典。
/// 字典索引0为空白符（CTC blank），后续索引对应实际字符。
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

    /// <summary>
    /// 从 PaddleX 模型的 config.json 加载字符字典。
    /// </summary>
    /// <param name="configPath">config.json 文件路径</param>
    public static OnnxCharDict Load(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"字符字典配置文件未找到: {configPath}");

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<JsonElement>(json);

        var chars = new List<string> { "blank" }; // 索引0 = CTC blank

        if (config.TryGetProperty("PostProcess", out var pp) &&
            pp.TryGetProperty("character_dict", out var dict))
        {
            foreach (var item in dict.EnumerateArray())
                chars.Add(item.GetString() ?? "");
        }

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
