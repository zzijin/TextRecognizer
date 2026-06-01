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
        double autoConfirmThreshold = 0.8,
        double autoFillThreshold = 0.5)
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
                ApplyWeightedAgreement(itemList, modelCount);
                groups.Add(new CrossValidateGroup
                {
                    Items = itemList,
                    Agreement = itemList.Where(x => !x.IsPlaceholder).Select(x => x.Agreement).DefaultIfEmpty(1).Max(),
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

                if (item.Score >= autoConfirmThreshold) real.Agreement = 3;
                else if (item.Score >= autoFillThreshold) real.Agreement = 2;
                else real.Agreement = 1;

                var groupItems = new List<CrossValidateGroupItem> { real, place, place };
                groups.Add(new CrossValidateGroup
                {
                    Items = groupItems,
                    Agreement = real.Agreement,
                    UnionRect = item.BoundingRect
                });
            }
        }

        AutoFillConfirmationLegacy(groups);
        return groups;
    }

    // ── 加权评分 ─────────────────────────────────────────────────────

    /// <summary>
    /// 通过最高总置信度选择最佳文本，然后得分 = 该文本的平均置信度。
    /// 这样，共识（多个模型一致）胜出选择，但置信度质量也被考虑。
    /// weighted_score = 获胜文本的总置信度和 / 支持该文本的模型数量
    /// </summary>
    private static (string text, double weightedScore, int count) EvaluateBestText(
        List<CrossValidateGroupItem> active)
    {
        var textData = new Dictionary<string, (double sum, int count)>();
        foreach (var a in active)
        {
            var key = a.Text.Trim();
            textData.TryGetValue(key, out var d);
            textData[key] = (d.sum + a.Score, d.count + 1);
        }

        // 按最高总置信度选择（共识有利于更多模型）
        var best = textData.MaxBy(kv => kv.Value.sum);
        double avg = best.Value.sum / best.Value.count;
        return (best.Key, avg, best.Value.count);
    }

    private static void ApplyWeightedAgreement(List<CrossValidateGroupItem> items, int modelCount)
    {
        var active = items.Where(i => !i.IsPlaceholder).ToList();
        if (active.Count == 0) return;

        var (bestText, weightedScore, _) = EvaluateBestText(active);

        foreach (var item in active)
        {
            item.Agreement = item.Text.Trim() == bestText
                ? (weightedScore >= 0.85 ? 3 : weightedScore >= 0.6 ? 2 : 1)
                : 1;
        }
    }

    private static void AutoFillByWeight(List<CrossValidateGroup> groups,
        double autoConfirmThreshold, double autoFillThreshold)
    {
        foreach (var group in groups)
        {
            var active = group.Items.Where(i => !i.IsPlaceholder).ToList();
            if (active.Count == 0) continue;

            var (bestText, weightedScore, _) = EvaluateBestText(active);

            if (weightedScore >= autoConfirmThreshold)
            {
                group.ConfirmedText = bestText;
                group.IsConfirmed = true;
            }
            else if (weightedScore >= autoFillThreshold)
            {
                group.ConfirmedText = bestText;
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

            if (active.All(i => i.Agreement == 3))
            {
                group.ConfirmedText = active[0].Text;
                group.IsConfirmed = true;
            }
            else if (active.Any(i => i.Agreement == 2))
            {
                var majority = active.First(i => i.Agreement == 2);
                group.ConfirmedText = majority.Text;
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
