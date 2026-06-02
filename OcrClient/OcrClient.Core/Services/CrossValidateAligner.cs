using OcrClient.Core.Models;
using OpenCvSharp;

namespace OcrClient.Core.Services;

public static class CrossValidateAligner
{
    private const double IouThreshold = 0.3;

    /// <summary>
    /// 将多个模型的对齐结果组合为CrossValidateGroup格式。
    /// 每个子列表代表一个模型的识别结果。
    /// </summary>
    public static List<CrossValidateGroup> Align(
        List<List<OcrItem>> modelResults,
        List<string> modelNames,
        double autoConfirmThreshold = 0.85,
        double autoFillThreshold = 0.6,
        double decayAlpha = 0.5)
    {
        int modelCount = modelResults.Count;
        if (modelCount == 0) return [];
        if (modelResults.All(m => m.Count == 0)) return [];

        // 带来源索引展开
        var all = new List<(int source, OcrItem item)>();
        for (int s = 0; s < modelCount; s++)
            foreach (var item in modelResults[s])
                all.Add((s, item));

        if (all.Count == 0) return [];

        // 按Y中心排序，然后按X排序
        all.Sort((a, b) =>
        {
            var aCY = a.item.BoundingRect.Y + a.item.BoundingRect.Height / 2.0;
            var bCY = b.item.BoundingRect.Y + b.item.BoundingRect.Height / 2.0;
            int yCmp = aCY.CompareTo(bCY);
            return yCmp != 0 ? yCmp : a.item.BoundingRect.X.CompareTo(b.item.BoundingRect.X);
        });

        // Y行聚类
        double avgHeight = all.Average(a => a.item.BoundingRect.Height);
        double rowThreshold = Math.Max(avgHeight * 0.5, 10);

        var yRows = new List<List<(int source, OcrItem item)>>();
        var currentRow = new List<(int source, OcrItem item)> { all[0] };
        double rowCenterY = all[0].item.BoundingRect.Y + all[0].item.BoundingRect.Height / 2.0;

        for (int i = 1; i < all.Count; i++)
        {
            double itemCY = all[i].item.BoundingRect.Y + all[i].item.BoundingRect.Height / 2.0;
            if (Math.Abs(itemCY - rowCenterY) < rowThreshold)
            {
                currentRow.Add(all[i]);
                rowCenterY = (rowCenterY * (currentRow.Count - 1) + itemCY) / currentRow.Count;
            }
            else
            {
                yRows.Add(currentRow);
                currentRow = [(all[i])];
                rowCenterY = itemCY;
            }
        }
        yRows.Add(currentRow);

        // 在每一行内，按X排序并对重叠项进行分组
        var groups = new List<CrossValidateGroup>();
        foreach (var row in yRows)
        {
            row.Sort((a, b) => a.item.BoundingRect.X.CompareTo(b.item.BoundingRect.X));
            var used = new bool[row.Count];

            for (int i = 0; i < row.Count; i++)
            {
                if (used[i]) continue;
                var (srcI, itemI) = row[i];

                var groupItems = new CrossValidateGroupItem[modelCount];
                var boxes = new List<List<double>>?[modelCount];
                groupItems[srcI] = MakeItem(modelNames[srcI], itemI);
                boxes[srcI] = itemI.Box;
                used[i] = true;

                for (int j = i + 1; j < row.Count; j++)
                {
                    if (used[j]) continue;
                    var (srcJ, itemJ) = row[j];
                    if (srcJ == srcI) continue;

                    if (itemI.Box is not null && itemJ.Box is not null &&
                        ComputeIoU(itemI.Box, itemJ.Box) >= IouThreshold)
                    {
                        groupItems[srcJ] = MakeItem(modelNames[srcJ], itemJ);
                        boxes[srcJ] = itemJ.Box;
                        used[j] = true;
                    }
                }

                for (int s = 0; s < modelCount; s++)
                    groupItems[s] ??= Placeholder();

                var itemList = groupItems.ToList();
                ApplyWeightedScoring(itemList, modelCount, decayAlpha);
                groups.Add(new CrossValidateGroup
                {
                    Items = itemList,
                    WeightedScore = itemList.Where(x => !x.IsPlaceholder).Select(x => x.WeightedScore).DefaultIfEmpty(0).Max(),
                    UnionRect = ComputeUnionRect(boxes)
                });
            }
        }

        AutoFillByWeight(groups, autoConfirmThreshold, autoFillThreshold);
        return groups;
    }

    /// <summary>
    /// 单模型对齐，按YX排序并基于置信度确定一致性。
    /// </summary>
    public static List<CrossValidateGroup> AlignSingleModel(
        List<OcrItem> items, string modelName,
        double autoConfirmThreshold, double autoFillThreshold)
    {
        if (items.Count == 0) return [];

        items.Sort((a, b) =>
        {
            var aCY = a.BoundingRect.Y + a.BoundingRect.Height / 2.0;
            var bCY = b.BoundingRect.Y + b.BoundingRect.Height / 2.0;
            int yCmp = aCY.CompareTo(bCY);
            return yCmp != 0 ? yCmp : a.BoundingRect.X.CompareTo(b.BoundingRect.X);
        });

        double avgHeight = items.Average(i => i.BoundingRect.Height);
        double rowThreshold = Math.Max(avgHeight * 0.5, 10);

        var yRows = new List<List<OcrItem>>();
        var currentRow = new List<OcrItem> { items[0] };
        double rowCenterY = items[0].BoundingRect.Y + items[0].BoundingRect.Height / 2.0;

        for (int i = 1; i < items.Count; i++)
        {
            double itemCY = items[i].BoundingRect.Y + items[i].BoundingRect.Height / 2.0;
            if (Math.Abs(itemCY - rowCenterY) < rowThreshold)
            {
                currentRow.Add(items[i]);
                rowCenterY = (rowCenterY * (currentRow.Count - 1) + itemCY) / currentRow.Count;
            }
            else
            {
                yRows.Add(currentRow);
                currentRow = [items[i]];
                rowCenterY = itemCY;
            }
        }
        yRows.Add(currentRow);

        foreach (var row in yRows)
            row.Sort((a, b) => a.BoundingRect.X.CompareTo(b.BoundingRect.X));

        var groups = new List<CrossValidateGroup>();
        foreach (var row in yRows)
        {
            foreach (var item in row)
            {
                var real = new CrossValidateGroupItem { Model = modelName, Text = item.Text, Score = item.Score };
                var place = Placeholder();

                real.WeightedScore = item.Score;
                real.ColorLevel = item.Score >= autoConfirmThreshold ? 2
                    : item.Score >= autoFillThreshold ? 1 : 0;

                var groupItems = new List<CrossValidateGroupItem> { real, place, place };
                groups.Add(new CrossValidateGroup
                {
                    Items = groupItems,
                    WeightedScore = real.WeightedScore,
                    UnionRect = item.BoundingRect
                });
            }
        }

        AutoFillConfirmationLegacy(groups);
        return groups;
    }

    // ── 加权衰减评分 ─────────────────────────────────────────────────

    /// <summary>
    /// 按文本分类，计算每类的平均置信度，然后应用衰减系数。
    /// weighted_score = (sum / count) * (1 - alpha * (1 - count / modelCount))
    /// 这样少数意见即使置信度高也会被衰减。
    /// </summary>
    private static (string text, double weightedScore) EvaluateBestTextWithDecay(
        List<CrossValidateGroupItem> active, int modelCount, double decayAlpha)
    {
        var textData = new Dictionary<string, (double sum, int count)>();
        foreach (var a in active)
        {
            var key = a.Text.Trim();
            textData.TryGetValue(key, out var d);
            textData[key] = (d.sum + a.Score, d.count + 1);
        }

        // 计算每组的衰减加权分数
        var scored = textData.Select(kv =>
        {
            double rawAvg = kv.Value.sum / kv.Value.count;
            double decay = 1.0 - decayAlpha * (1.0 - (double)kv.Value.count / modelCount);
            return (text: kv.Key, score: rawAvg * decay, rawAvg, count: kv.Value.count);
        }).ToList();

        // 选择衰减加权分数最高的文本
        var best = scored.MaxBy(x => x.score);
        return (best.text, best.score);
    }

    private static void ApplyWeightedScoring(List<CrossValidateGroupItem> items, int modelCount, double decayAlpha)
    {
        var active = items.Where(i => !i.IsPlaceholder).ToList();
        if (active.Count == 0) return;

        // 为每个文本组计算衰减加权分数
        var scoresByText = new Dictionary<string, double>();
        {
            var textData = new Dictionary<string, (double sum, int count)>();
            foreach (var a in active)
            {
                var key = a.Text.Trim();
                textData.TryGetValue(key, out var d);
                textData[key] = (d.sum + a.Score, d.count + 1);
            }
            foreach (var kv in textData)
            {
                double rawAvg = kv.Value.sum / kv.Value.count;
                double decay = 1.0 - decayAlpha * (1.0 - (double)kv.Value.count / modelCount);
                scoresByText[kv.Key] = rawAvg * decay;
            }
        }

        // 为每个 item 设置 WeightedScore 和 ColorLevel（纯按阈值，同文本组同色）
        foreach (var item in active)
        {
            item.WeightedScore = scoresByText.GetValueOrDefault(item.Text.Trim(), 0);
            item.ColorLevel = item.WeightedScore >= 0.85 ? 2
                : item.WeightedScore >= 0.6 ? 1 : 0;
        }
    }

    private static void AutoFillByWeight(List<CrossValidateGroup> groups,
        double autoConfirmThreshold, double autoFillThreshold)
    {
        foreach (var group in groups)
        {
            var active = group.Items.Where(i => !i.IsPlaceholder).ToList();
            if (active.Count == 0) continue;

            // 找到 WeightedScore 最高的 item 作为胜出文本
            var best = active.MaxBy(i => i.WeightedScore)!;
            if (best.WeightedScore >= autoConfirmThreshold)
            {
                group.ConfirmedText = best.Text;
                group.IsConfirmed = true;
            }
            else if (best.WeightedScore >= autoFillThreshold)
            {
                group.ConfirmedText = best.Text;
                group.IsConfirmed = false;
            }
        }
    }

    // ── 旧版辅助方法（用于单模型）────────────────────────────────────

    private static void AutoFillConfirmationLegacy(List<CrossValidateGroup> groups)
    {
        foreach (var group in groups)
        {
            var active = group.Items.Where(i => !i.IsPlaceholder).ToList();
            if (active.Count == 0) continue;

            if (active.All(i => i.ColorLevel == 2))
            {
                group.ConfirmedText = active[0].Text;
                group.IsConfirmed = true;
            }
            else if (active.Any(i => i.ColorLevel >= 1))
            {
                var best = active.MaxBy(i => i.WeightedScore)!;
                group.ConfirmedText = best.Text;
                group.IsConfirmed = false;
            }
        }
    }

    // ── 几何计算辅助方法 ────────────────────────────────────────────────

    private static double ComputeIoU(List<List<double>> boxA, List<List<double>> boxB)
    {
        var (ax1, ay1, ax2, ay2) = BoxToRect(boxA);
        var (bx1, by1, bx2, by2) = BoxToRect(boxB);
        double ix1 = Math.Max(ax1, bx1), iy1 = Math.Max(ay1, by1);
        double ix2 = Math.Min(ax2, bx2), iy2 = Math.Min(ay2, by2);
        double iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        double inter = iw * ih;
        double areaA = Math.Max(0, (ax2 - ax1) * (ay2 - ay1));
        double areaB = Math.Max(0, (bx2 - bx1) * (by2 - by1));
        double union = areaA + areaB - inter;
        return union > 0 ? inter / union : 0;
    }

    private static (double, double, double, double) BoxToRect(List<List<double>> box)
    {
        double minX = box.Min(p => p[0]), minY = box.Min(p => p[1]);
        double maxX = box.Max(p => p[0]), maxY = box.Max(p => p[1]);
        return (minX, minY, maxX, maxY);
    }

    private static CrossValidateGroupItem MakeItem(string model, OcrItem item)
        => new() { Model = model, Text = item.Text, Score = item.Score };

    private static CrossValidateGroupItem Placeholder()
        => new() { IsPlaceholder = true };

    private static Rect ComputeUnionRect(params List<List<double>>?[] boxes)
    {
        double? minX = null, minY = null, maxX = null, maxY = null;
        foreach (var box in boxes)
        {
            if (box is null) continue;
            var (x1, y1, x2, y2) = BoxToRect(box);
            minX = minX.HasValue ? Math.Min(minX.Value, x1) : x1;
            minY = minY.HasValue ? Math.Min(minY.Value, y1) : y1;
            maxX = maxX.HasValue ? Math.Max(maxX.Value, x2) : x2;
            maxY = maxY.HasValue ? Math.Max(maxY.Value, y2) : y2;
        }
        if (!minX.HasValue) return new Rect(0, 0, 0, 0);
        return new Rect((int)minX.Value, (int)minY!.Value,
            (int)(maxX!.Value - minX.Value), (int)(maxY!.Value - minY!.Value));
    }
}
