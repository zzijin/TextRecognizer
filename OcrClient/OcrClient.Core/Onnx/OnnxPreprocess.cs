using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace OcrClient.Core.Onnx;

/// <summary>
/// ONNX 模型的图像预处理。unsafe 仅用于从 Mat.DataPointer (byte*) 创建 Span&lt;float&gt;，
/// 其余所有内存访问操作均为安全的 Span API（Slice/CopyTo）。
/// </summary>
public static class OnnxPreprocess
{
    // ── 检测预处理 ──────────────────────────────────────────────────────────

    public const int DetTargetLong = 960;
    public const int DetStride = 128;
    private static readonly float[] DetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] DetStd = [0.229f, 0.224f, 0.225f];

    public static (Mat Resized, (int srcH, int srcW, int newH, int newW) ShapeInfo)
        LetterboxResize(Mat imageBgr)
    {
        int h = imageBgr.Rows, w = imageBgr.Cols;
        double ratio = (double)DetTargetLong / Math.Max(h, w);
        int newH = (int)(Math.Round(h * ratio / DetStride) * DetStride);
        int newW = (int)(Math.Round(w * ratio / DetStride) * DetStride);
        if (newH < DetStride) newH = DetStride;
        if (newW < DetStride) newW = DetStride;

        var resized = new Mat();
        Cv2.Resize(imageBgr, resized, new Size(newW, newH));
        return (resized, (h, w, newH, newW));
    }

    // ⚠ 唯一必须 unsafe 的地方：OpenCV 暴露的内存指针是 byte*，不是 IntPtr。
    //    创建 Span 后所有操作均为安全 API。
    private static unsafe Span<float> AsFloatSpan(Mat m, int length)
        => new((void*)m.DataPointer, length);

    public static unsafe DenseTensor<float> DetNormalize(Mat imageBgr)
    {
        int h = imageBgr.Rows, w = imageBgr.Cols;
        int planeSize = h * w;

        using var f32 = new Mat();
        imageBgr.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);
        var channels = Cv2.Split(f32);

        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
        var dst = tensor.Buffer.Span;

        for (int c = 0; c < 3; c++)
        {
            using var meanMat = new Mat(h, w, MatType.CV_32FC1, new Scalar(DetMean[c]));
            using var stdMat = new Mat(h, w, MatType.CV_32FC1, new Scalar(DetStd[c]));
            Cv2.Subtract(channels[c], meanMat, channels[c]);
            Cv2.Divide(channels[c], stdMat, channels[c]);

            AsFloatSpan(channels[c], planeSize).CopyTo(dst.Slice(c * planeSize, planeSize));
        }
        foreach (var ch in channels) ch.Dispose();

        return tensor;
    }

    public static (DenseTensor<float> Tensor, (int srcH, int srcW, int newH, int newW) ShapeInfo)
        PreprocessDet(Mat imageBgr)
    {
        var (resized, shapeInfo) = LetterboxResize(imageBgr);
        var tensor = DetNormalize(resized);
        resized.Dispose();
        return (tensor, shapeInfo);
    }

    // ── 识别预处理 ──────────────────────────────────────────────────────────

    public const int RecImgH = 48;
    public const int RecImgW = 320;
    public const int RecMaxW = 3200;

    public static unsafe DenseTensor<float> PreprocessRec(Mat cropBgr)
    {
        int h = cropBgr.Rows, w = cropBgr.Cols;
        float whRatio = (float)w / h;
        float maxWhRatio = Math.Max((float)RecImgW / RecImgH, whRatio);
        int resizedW = (int)Math.Ceiling(RecImgH * maxWhRatio);
        resizedW = Math.Min(resizedW, RecMaxW);
        resizedW = Math.Min(resizedW, (int)Math.Ceiling(RecImgH * whRatio));

        using var resized = new Mat();
        Cv2.Resize(cropBgr, resized, new Size(resizedW, RecImgH));
        using var f32 = new Mat();
        resized.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);

        var channels = Cv2.Split(f32);
        for (int c = 0; c < 3; c++)
        {
            using var half = new Mat(RecImgH, resizedW, MatType.CV_32FC1, new Scalar(0.5));
            Cv2.Subtract(channels[c], half, channels[c]);
            Cv2.Multiply(channels[c], 2.0, channels[c]);
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, RecImgH, RecImgW });
        var dst = tensor.Buffer.Span;
        int planeSize = RecImgH * RecImgW;
        int srcRowSize = resizedW;

        for (int c = 0; c < 3; c++)
        {
            var src = AsFloatSpan(channels[c], srcRowSize * RecImgH);
            var chDst = dst.Slice(c * planeSize, planeSize);
            for (int y = 0; y < RecImgH; y++)
                src.Slice(y * srcRowSize, srcRowSize).CopyTo(chDst.Slice(y * RecImgW, srcRowSize));
        }
        foreach (var ch in channels) ch.Dispose();

        return tensor;
    }

    /// <summary>
    /// 批量识别预处理。返回形状 [batch, 3, 48, imgW] 的 DenseTensor。
    /// imgW 取所有 crop 的最大宽度。模型支持动态宽度 [B,3,48,W]。
    /// </summary>
    public static unsafe DenseTensor<float> PreprocessRecBatch(List<Mat> crops)
    {
        int batch = crops.Count;

        var widths = new int[batch];
        int maxW = RecImgW;
        for (int i = 0; i < batch; i++)
        {
            int h = crops[i].Rows, w = crops[i].Cols;
            double whRatio = (double)w / h;
            double maxWhRatio = Math.Max((double)RecImgW / RecImgH, whRatio);
            int rw = (int)Math.Ceiling(RecImgH * maxWhRatio);
            rw = Math.Min(rw, RecMaxW);
            rw = Math.Min(rw, (int)Math.Ceiling(RecImgH * whRatio));
            widths[i] = rw;
            if (rw > maxW) maxW = rw;
        }

        int imgW = maxW;
        var tensor = new DenseTensor<float>(new[] { batch, 3, RecImgH, imgW });
        var dst = tensor.Buffer.Span;
        int planeSize = RecImgH * imgW;
        int singleSize = 3 * planeSize;

        for (int i = 0; i < batch; i++)
        {
            int resizedW = widths[i];
            var crop = crops[i];

            using var resized = new Mat();
            Cv2.Resize(crop, resized, new Size(resizedW, RecImgH));
            using var f32 = new Mat();
            resized.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);

            var channels = Cv2.Split(f32);
            for (int c = 0; c < 3; c++)
            {
                using var half = new Mat(RecImgH, resizedW, MatType.CV_32FC1, new Scalar(0.5));
                Cv2.Subtract(channels[c], half, channels[c]);
                Cv2.Multiply(channels[c], 2.0, channels[c]);
            }

            int batchOff = i * singleSize;
            int srcSize = resizedW * RecImgH;
            for (int c = 0; c < 3; c++)
            {
                var src = AsFloatSpan(channels[c], srcSize);
                var chDst = dst.Slice(batchOff + c * planeSize, planeSize);
                for (int y = 0; y < RecImgH; y++)
                    src.Slice(y * resizedW, resizedW).CopyTo(chDst.Slice(y * imgW, resizedW));
            }
            foreach (var ch in channels) ch.Dispose();
        }

        return tensor;
    }
}
