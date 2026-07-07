using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace OcrClient.Core.Onnx;

/// <summary>
/// ONNX 模型的后处理。
/// 从 Python onnx_ocr.py 的 DB 后处理、CTC 解码、角点排序移植。
/// </summary>
public static class OnnxPostprocess
{
    // ── DB 后处理 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 DB 分割概率图中提取文本框。
    /// 复刻 Python 的 _boxes_from_bitmap。
    /// </summary>
    /// <param name="pred">检测模型输出的概率图 [H, W]，值范围 [0, 1]</param>
    /// <param name="srcShape">原始图像尺寸 (h, w)</param>
    /// <param name="shapeInfo">letterbox 后的尺寸 (srcH, srcW, newH, newW)</param>
    /// <param name="thresh">二值化阈值（默认 0.3）</param>
    /// <param name="boxThresh">框最小平均分（默认 0.5）</param>
    /// <param name="unclipRatio">多边形扩展比例（默认 1.5）</param>
    /// <param name="minSize">最小边长（默认 3）</param>
    /// <returns>(boxes, scores) — boxes 是 Point2f[4] 的列表</returns>
    public static (List<Point2f[]> Boxes, List<float> Scores) BoxesFromBitmap(
        float[,] pred, (int h, int w) srcShape,
        (int srcH, int srcW, int newH, int newW) shapeInfo,
        float thresh = 0.3f, float boxThresh = 0.5f,
        float unclipRatio = 1.5f, int minSize = 3)
    {
        int predH = pred.GetLength(0);
        int predW = pred.GetLength(1);
        float ratioH = (float)srcShape.h / shapeInfo.newH;
        float ratioW = (float)srcShape.w / shapeInfo.newW;

        // 二值化
        using var bitmap = new Mat(predH, predW, MatType.CV_8UC1);
        for (int y = 0; y < predH; y++)
            for (int x = 0; x < predW; x++)
                bitmap.Set<byte>(y, x, pred[y, x] > thresh ? (byte)255 : (byte)0);

        // 查找轮廓
        Cv2.FindContours(bitmap, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        var boxes = new List<Point2f[]>();
        var scores = new List<float>();

        foreach (var contour in contours)
        {
            if (contour.Length < 4)
                continue;

            // 最小面积外接矩形
            var rect = Cv2.MinAreaRect(contour);
            var points = rect.Points(); // 4 个角点

            // 过滤小框
            float sideShort = Math.Min(rect.Size.Width, rect.Size.Height);
            if (sideShort < minSize)
                continue;

            // 计算框内平均分
            var mask = new Mat(predH, predW, MatType.CV_8UC1, Scalar.Black);
            var pts = points.Select(p => new OpenCvSharp.Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
            Cv2.FillPoly(mask, [pts], Scalar.White);

            float sum = 0;
            int count = 0;
            for (int my = 0; my < predH; my++)
            {
                for (int mx = 0; mx < predW; mx++)
                {
                    if (mask.Get<byte>(my, mx) > 0)
                    {
                        sum += pred[my, mx];
                        count++;
                    }
                }
            }
            mask.Dispose();

            float score = count > 0 ? sum / count : 0;
            if (score < boxThresh)
                continue;

            // Unclip: 扩展多边形
            var unclipped = UnclipPoints(points, unclipRatio);
            if (unclipped is null || unclipped.Length < 4)
                continue;

            // 角点排序: TL, TR, BR, BL
            var ordered = OrderPoints(unclipped);

            // 缩放到原始图像坐标
            for (int i = 0; i < 4; i++)
            {
                ordered[i].X = Math.Clamp(ordered[i].X * ratioW, 0, srcShape.w - 1);
                ordered[i].Y = Math.Clamp(ordered[i].Y * ratioH, 0, srcShape.h - 1);
            }

            boxes.Add(ordered);
            scores.Add(score);
        }

        // 按 Y 中心排序，再按 X
        var boxScorePairs = boxes.Zip(scores, (b, s) => (box: b, score: s)).ToList();
        boxScorePairs.Sort((a, b) =>
        {
            float aCY = (a.box[0].Y + a.box[2].Y) / 2f;
            float bCY = (b.box[0].Y + b.box[2].Y) / 2f;
            int yCmp = aCY.CompareTo(bCY);
            return yCmp != 0 ? yCmp : a.box[0].X.CompareTo(b.box[0].X);
        });

        return (boxScorePairs.Select(p => p.box).ToList(),
                boxScorePairs.Select(p => p.score).ToList());
    }

    // ── 多边形扩展 (unclip) ─────────────────────────────────────────────────

    /// <summary>
    /// 扩展 4 点凸多边形。复刻 pyclipper 的 offset 行为。
    /// 通过将每个顶点沿远离中心的方向移动来实现。
    /// </summary>
    private static Point2f[]? UnclipPoints(Point2f[] points, float ratio)
    {
        if (points.Length != 4)
            return null;

        // 计算多边形面积和周长
        float area = (float)Cv2.ContourArea(points);
        float length = (float)Cv2.ArcLength(points, true);
        if (length <= 0)
            return null;

        float distance = area * ratio / length;
        if (distance <= 0)
            return points;

        // 计算中心
        float cx = (points[0].X + points[1].X + points[2].X + points[3].X) / 4f;
        float cy = (points[0].Y + points[1].Y + points[2].Y + points[3].Y) / 4f;

        // 对每个顶点：沿远离中心的方向移动 distance
        var result = new Point2f[4];
        for (int i = 0; i < 4; i++)
        {
            float dx = points[i].X - cx;
            float dy = points[i].Y - cy;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6f)
            {
                result[i] = points[i];
            }
            else
            {
                float scale = 1f + distance / len;
                result[i] = new Point2f(cx + dx * scale, cy + dy * scale);
            }
        }

        return result;
    }

    // ── 角点排序 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 将 4 个点排序为：左上、右上、右下、左下。
    /// 复刻 Python 的 _order_points。
    /// </summary>
    public static Point2f[] OrderPoints(Point2f[] pts)
    {
        if (pts.Length != 4)
            return pts;

        var sorted = pts.OrderBy(p => p.X + p.Y).ToArray();
        var tl = sorted[0];        // sum 最小 = 左上
        var br = sorted[3];        // sum 最大 = 右下

        var byDiff = pts.OrderBy(p => p.Y - p.X).ToArray();
        var tr = byDiff[0];        // diff 最小 = 右上
        var bl = byDiff[3];        // diff 最大 = 左下

        return [tl, tr, br, bl];
    }

    // ── 从概率图提取 Boxes 的便捷方法 ─────────────────────────────────────

    /// <summary>
    /// 从 DenseTensor 输出（det 模型输出 [1, 1, H, W]）提取文本框。
    /// </summary>
    public static (List<Point2f[]> Boxes, List<float> Scores) ExtractBoxes(
        DenseTensor<float> output, (int h, int w) srcShape,
        (int srcH, int srcW, int newH, int newW) shapeInfo,
        float thresh = 0.3f, float boxThresh = 0.5f, float unclipRatio = 1.5f)
    {
        int h = output.Dimensions[2];
        int w = output.Dimensions[3];
        var pred = new float[h, w];
        // output[0, 0, y, x]
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pred[y, x] = output[0, 0, y, x];

        return BoxesFromBitmap(pred, srcShape, shapeInfo, thresh, boxThresh, unclipRatio);
    }

    // ── CTC Greedy 解码 ──────────────────────────────────────────────────────

    /// <summary>
    /// CTC 贪婪解码：argmax → 去重 → 去 blank → 映射字符。
    /// 复刻 Python 的 _ctc_decode。
    /// </summary>
    /// <param name="logits">识别模型输出，形状 [seqLen, numClasses]</param>
    /// <param name="charDict">字符字典（索引0=blank）</param>
    /// <returns>(decodedText, confidence)</returns>
    public static (string Text, float Confidence) CtcDecode(float[,] logits, OnnxCharDict charDict)
    {
        int seqLen = logits.GetLength(0);
        int numClasses = logits.GetLength(1);

        // Argmax
        var indices = new int[seqLen];
        var maxProbs = new float[seqLen];
        for (int t = 0; t < seqLen; t++)
        {
            int bestIdx = 0;
            float bestVal = logits[t, 0];
            for (int c = 1; c < numClasses; c++)
            {
                if (logits[t, c] > bestVal)
                {
                    bestVal = logits[t, c];
                    bestIdx = c;
                }
            }
            indices[t] = bestIdx;
            maxProbs[t] = bestVal;
        }

        // 去除连续重复 + 去除 blank (index 0)
        var filteredIdx = new List<int>();
        var filteredProb = new List<float>();
        for (int t = 0; t < seqLen; t++)
        {
            if (indices[t] == 0) continue; // skip blank
            if (t > 0 && indices[t] == indices[t - 1]) continue; // skip repeats
            filteredIdx.Add(indices[t]);
            filteredProb.Add(maxProbs[t]);
        }

        // 映射字符
        var chars = filteredIdx.Select(i => charDict.MapIndex(i)).ToArray();
        string text = string.Concat(chars);
        float conf = filteredProb.Count > 0 ? filteredProb.Average() : 0f;

        return (text, conf);
    }

    /// <summary>
    /// 批量 CTC 解码。
    /// </summary>
    /// <param name="logits">形状 [batch, seqLen, numClasses] 的 DenseTensor</param>
    /// <param name="charDict">字符字典</param>
    /// <returns>每个 batch item 的 (text, confidence)</returns>
    public static List<(string Text, float Confidence)> CtcDecodeBatch(
        DenseTensor<float> logits, OnnxCharDict charDict)
    {
        int batch = logits.Dimensions[0];
        int seqLen = logits.Dimensions[1];
        int numClasses = logits.Dimensions[2];

        var results = new List<(string, float)>(batch);

        for (int i = 0; i < batch; i++)
        {
            var single = new float[seqLen, numClasses];
            for (int t = 0; t < seqLen; t++)
                for (int c = 0; c < numClasses; c++)
                    single[t, c] = logits[i, t, c];

            results.Add(CtcDecode(single, charDict));
        }

        return results;
    }

    // ── 图像裁剪 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 根据检测框从原图裁剪文字区域（轴对齐，不扶正）。
    /// </summary>
    public static List<Mat> CropRegions(Mat imageBgr, List<Point2f[]> boxes)
    {
        var crops = new List<Mat>(boxes.Count);
        foreach (var box in boxes)
        {
            float xMin = box.Min(p => p.X);
            float xMax = box.Max(p => p.X);
            float yMin = box.Min(p => p.Y);
            float yMax = box.Max(p => p.Y);

            int x1 = Math.Max(0, (int)xMin);
            int y1 = Math.Max(0, (int)yMin);
            int x2 = Math.Min(imageBgr.Cols, (int)Math.Ceiling(xMax));
            int y2 = Math.Min(imageBgr.Rows, (int)Math.Ceiling(yMax));

            if (x2 > x1 && y2 > y1)
                crops.Add(imageBgr[y1..y2, x1..x2].Clone());
            else
                crops.Add(new Mat(48, 48, MatType.CV_8UC3, Scalar.Black));
        }
        return crops;
    }

    /// <summary>
    /// 根据检测框从原图裁剪并扶正文字区域。
    /// 用 4 个角点做透视变换，将旋转文字映射为水平矩形，再裁剪。
    /// 处理 90°/180° 等大角度旋转，确保送入识别模型的文字始终水平。
    /// </summary>
    public static List<Mat> CropRegionsStraightened(Mat imageBgr, List<Point2f[]> boxes)
    {
        var crops = new List<Mat>(boxes.Count);
        foreach (var box in boxes)
        {
            // 确保角点为 TL, TR, BR, BL 顺序
            var ordered = OrderPoints(box);

            // 计算目标矩形的宽高
            float wTop = MathF.Sqrt(MathF.Pow(ordered[1].X - ordered[0].X, 2) + MathF.Pow(ordered[1].Y - ordered[0].Y, 2));
            float wBot = MathF.Sqrt(MathF.Pow(ordered[2].X - ordered[3].X, 2) + MathF.Pow(ordered[2].Y - ordered[3].Y, 2));
            float dstW = MathF.Max(wTop, wBot);

            float hLef = MathF.Sqrt(MathF.Pow(ordered[3].X - ordered[0].X, 2) + MathF.Pow(ordered[3].Y - ordered[0].Y, 2));
            float hRig = MathF.Sqrt(MathF.Pow(ordered[2].X - ordered[1].X, 2) + MathF.Pow(ordered[2].Y - ordered[1].Y, 2));
            float dstH = MathF.Max(hLef, hRig);

            if (dstW < 4 || dstH < 4)
            {
                crops.Add(new Mat(48, 48, MatType.CV_8UC3, Scalar.Black));
                continue;
            }

            // 透视变换：源 4 点 → 目标水平矩形
            var srcPts = new Point2f[4];
            srcPts[0] = ordered[0];                          // TL
            srcPts[1] = ordered[1];                          // TR
            srcPts[2] = ordered[2];                          // BR
            srcPts[3] = ordered[3];                          // BL

            var dstPts = new Point2f[4];
            dstPts[0] = new Point2f(0, 0);                  // TL
            dstPts[1] = new Point2f(dstW - 1, 0);           // TR
            dstPts[2] = new Point2f(dstW - 1, dstH - 1);    // BR
            dstPts[3] = new Point2f(0, dstH - 1);           // BL

            var M = Cv2.GetPerspectiveTransform(srcPts, dstPts);
            var warped = new Mat();
            Cv2.WarpPerspective(imageBgr, warped, M, new Size((int)dstW, (int)dstH),
                InterpolationFlags.Linear, BorderTypes.Replicate);

            crops.Add(warped);
        }
        return crops;
    }
}
