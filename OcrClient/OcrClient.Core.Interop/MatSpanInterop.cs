using OpenCvSharp;

namespace OcrClient.Core.Interop;

/// <summary>
/// 将 OpenCV Mat 的原始内存暴露为安全的 Span&lt;float&gt;。
/// 本项目是唯一允许 unsafe 的程序集，其余项目保持纯托管内存安全。
/// </summary>
public static class MatSpanInterop
{
    /// <summary>
    /// 从 Mat 的原生 float32 数据创建 Span&lt;float&gt;（零拷贝）。
    /// </summary>
    /// <param name="mat">单通道 CV_32FC1 Mat</param>
    /// <param name="length">期望的浮点数个数</param>
    public static unsafe Span<float> AsFloatSpan(Mat mat, int length)
        => new((void*)mat.DataPointer, length);
}
