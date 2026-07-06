using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace OcrClient.Core.Onnx;

/// <summary>
/// ONNX 模型的图像预处理。
/// 从 Python onnx_ocr.py 的 _letterbox_resize、_det_normalize、_rec_resize_norm 移植。
/// </summary>
public static class OnnxPreprocess
{
    // ── 检测预处理 ──────────────────────────────────────────────────────────

    /// <summary>检测模型输入：长边目标尺寸。</summary>
    public const int DetTargetLong = 960;
    /// <summary>检测模型输入：stride 对齐。</summary>
    public const int DetStride = 128;
    /// <summary>检测归一化均值（BGR 顺序，与 OpenCV 默认一致）。</summary>
    private static readonly float[] DetMean = [0.485f, 0.456f, 0.406f];
    /// <summary>检测归一化标准差。</summary>
    private static readonly float[] DetStd = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// Letterbox resize: 长边缩放到 DetTargetLong，宽高对齐到 stride 倍数。
    /// 返回 (resizedImage, shapeInfo)，shapeInfo 为 (srcH, srcW, newH, newW)。
    /// </summary>
    public static (Mat Resized, (int srcH, int srcW, int newH, int newW) ShapeInfo)
        LetterboxResize(Mat imageBgr)
    {
        int h = imageBgr.Rows, w = imageBgr.Cols;
        float ratio = (float)DetTargetLong / Math.Max(h, w);
        int newH = (int)Math.Round(h * ratio / DetStride) * DetStride;
        int newW = (int)Math.Round(w * ratio / DetStride) * DetStride;
        if (newH < DetStride) newH = DetStride;
        if (newW < DetStride) newW = DetStride;

        var resized = new Mat();
        Cv2.Resize(imageBgr, resized, new Size(newW, newH));
        return (resized, (h, w, newH, newW));
    }

    /// <summary>
    /// 检测归一化： (x/255 - mean) / std，HWC -> CHW，返回 CHW float32 DenseTensor。
    /// </summary>
    public static DenseTensor<float> DetNormalize(Mat imageBgr)
    {
        int h = imageBgr.Rows, w = imageBgr.Cols;

        // 转换为 float32
        using var f32 = new Mat();
        imageBgr.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);

        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
        var span = tensor.Buffer.Span;
        int stride = h * w;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var pixel = f32.At<Vec3f>(y, x);
                int idx = y * w + x;
                span[idx] = (pixel.Item0 - DetMean[0]) / DetStd[0];           // B -> C0
                span[stride + idx] = (pixel.Item1 - DetMean[1]) / DetStd[1];   // G -> C1
                span[2 * stride + idx] = (pixel.Item2 - DetMean[2]) / DetStd[2]; // R -> C2
            }
        }

        return tensor;
    }

    /// <summary>
    /// 完整的检测预处理流水线：letterbox resize + 归一化 -> CHW tensor。
    /// 返回 (tensor, shapeInfo)。
    /// </summary>
    public static (DenseTensor<float> Tensor, (int srcH, int srcW, int newH, int newW) ShapeInfo)
        PreprocessDet(Mat imageBgr)
    {
        var (resized, shapeInfo) = LetterboxResize(imageBgr);
        var tensor = DetNormalize(resized);
        resized.Dispose();
        return (tensor, shapeInfo);
    }

    // ── 识别预处理 ──────────────────────────────────────────────────────────

    /// <summary>识别模型输入：高度。</summary>
    public const int RecImgH = 48;
    /// <summary>识别模型输入：宽度（固定，短序列会 padding）。</summary>
    public const int RecImgW = 320;
    /// <summary>识别模型最大宽度（长序列上限）。</summary>
    public const int RecMaxW = 3200;

    /// <summary>
    /// 识别预处理：保持宽高比缩放到高度 48，归一化到 [-1, 1]，宽度 padding 到 320。
    /// 返回 CHW float32 DenseTensor，形状为 [1, 3, 48, 320]。
    /// </summary>
    public static DenseTensor<float> PreprocessRec(Mat cropBgr)
    {
        int h = cropBgr.Rows, w = cropBgr.Cols;
        float whRatio = (float)w / h;
        float maxWhRatio = Math.Max((float)RecImgW / RecImgH, whRatio);
        int resizedW = (int)Math.Ceiling(RecImgH * maxWhRatio);
        resizedW = Math.Min(resizedW, RecMaxW);
        resizedW = Math.Min(resizedW, (int)Math.Ceiling(RecImgH * whRatio));

        // resize 到 (resizedW, RecImgH)
        using var resized = new Mat();
        Cv2.Resize(cropBgr, resized, new Size(resizedW, RecImgH));

        // 转换为 float32, HWC -> CHW, 归一化到 [0, 1] 再转 [-1, 1]
        // Python: resized.transpose(2,0,1) / 255.0  然后 (resized - 0.5) / 0.5
        var tensor = new DenseTensor<float>(new[] { 1, 3, RecImgH, RecImgW });
        var span = tensor.Buffer.Span;
        int stride = RecImgH * RecImgW;

        using var f32 = new Mat();
        resized.ConvertTo(f32, MatType.CV_32FC3);

        for (int y = 0; y < RecImgH; y++)
        {
            for (int x = 0; x < RecImgW; x++)
            {
                int baseIdx = y * RecImgW + x;
                if (x < resizedW)
                {
                    var pixel = f32.At<Vec3f>(y, x);
                    // 先归一化到 [0,1]，再转 [-1,1]
                    float b = pixel.Item0 / 255.0f;
                    float g = pixel.Item1 / 255.0f;
                    float r = pixel.Item2 / 255.0f;
                    span[baseIdx] = (b - 0.5f) / 0.5f;             // B -> C0
                    span[stride + baseIdx] = (g - 0.5f) / 0.5f;    // G -> C1
                    span[2 * stride + baseIdx] = (r - 0.5f) / 0.5f; // R -> C2
                }
                // else: 默认为 0（padding 区域），对应 Python 的 constant pad
            }
        }

        return tensor;
    }

    /// <summary>
    /// 批量识别预处理。返回形状 [batch, 3, 48, 320] 的 DenseTensor。
    /// 直接写入 batch tensor 以避免中间拷贝。
    /// </summary>
    public static DenseTensor<float> PreprocessRecBatch(List<Mat> crops)
    {
        int batch = crops.Count;
        var tensor = new DenseTensor<float>(new[] { batch, 3, RecImgH, RecImgW });
        var span = tensor.Buffer.Span;
        int singleCh = RecImgH * RecImgW;
        int singleSize = 3 * singleCh;

        for (int i = 0; i < batch; i++)
        {
            var crop = crops[i];
            int h = crop.Rows, w = crop.Cols;
            float whRatio = (float)w / h;
            float maxWhRatio = Math.Max((float)RecImgW / RecImgH, whRatio);
            int resizedW = (int)Math.Ceiling(RecImgH * maxWhRatio);
            resizedW = Math.Min(resizedW, RecMaxW);
            resizedW = Math.Min(resizedW, (int)Math.Ceiling(RecImgH * whRatio));

            using var resized = new Mat();
            Cv2.Resize(crop, resized, new Size(resizedW, RecImgH));
            using var f32 = new Mat();
            resized.ConvertTo(f32, MatType.CV_32FC3);

            int batchOff = i * singleSize;
            for (int y = 0; y < RecImgH; y++)
            {
                for (int x = 0; x < RecImgW; x++)
                {
                    int baseIdx = batchOff + y * RecImgW + x;
                    if (x < resizedW)
                    {
                        var pixel = f32.At<Vec3f>(y, x);
                        float b = pixel.Item0 / 255.0f;
                        float g = pixel.Item1 / 255.0f;
                        float r = pixel.Item2 / 255.0f;
                        span[baseIdx] = (b - 0.5f) / 0.5f;                // B -> C0
                        span[batchOff + singleCh + y * RecImgW + x] = (g - 0.5f) / 0.5f;  // G -> C1
                        span[batchOff + 2 * singleCh + y * RecImgW + x] = (r - 0.5f) / 0.5f; // R -> C2
                    }
                }
            }
        }

        return tensor;
    }
}
