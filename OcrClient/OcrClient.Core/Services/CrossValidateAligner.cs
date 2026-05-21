using OcrClient.Core.Models;
using OpenCvSharp;

namespace OcrClient.Core.Services;

public static class CrossValidateAligner
{
    private const double IouThreshold = 0.3;

    public static List<CrossValidateGroup> Align(CrossValidateResult result)
    {
        var items1 = result.ServerRec?.Items ?? [];
        var items2 = result.MobileRec?.Items ?? [];
        var items3 = result.EnMobileRec?.Items ?? [];

        var used2 = new HashSet<int>();
        var used3 = new HashSet<int>();
        var groups = new List<CrossValidateGroup>();

        foreach (var item1 in items1)
        {
            var idx2 = BestMatch(item1.Box, items2, used2);
            var idx3 = BestMatch(item1.Box, items3, used3);

            var groupItems = new List<CrossValidateGroupItem>
            {
                MakeItem("PP-OCRv5_server_rec", item1),
                idx2.HasValue ? MakeItem("PP-OCRv5_mobile_rec", items2[idx2.Value]) : Placeholder(),
                idx3.HasValue ? MakeItem("en_PP-OCRv5_mobile_rec", items3[idx3.Value]) : Placeholder(),
            };

            if (idx2.HasValue) used2.Add(idx2.Value);
            if (idx3.HasValue) used3.Add(idx3.Value);

            ApplyPerItemAgreement(groupItems);
            groups.Add(new CrossValidateGroup
            {
                Items = groupItems,
                Agreement = groupItems.Where(i => !i.IsPlaceholder).Select(i => i.Agreement).DefaultIfEmpty(1).Max(),
                UnionRect = ComputeUnionRect(item1.Box, idx2.HasValue ? items2[idx2.Value].Box : null, idx3.HasValue ? items3[idx3.Value].Box : null)
            });
        }

        // Unmatched items from model 2
        for (int i = 0; i < items2.Count; i++)
        {
            if (used2.Contains(i)) continue;
            var item = MakeItem("PP-OCRv5_mobile_rec", items2[i]);
            item.Agreement = 1;
            groups.Add(new CrossValidateGroup
            {
                Items = new List<CrossValidateGroupItem> { Placeholder(), item, Placeholder() },
                Agreement = 1,
                UnionRect = ComputeUnionRect(null, items2[i].Box, null)
            });
        }

        // Unmatched items from model 3
        for (int i = 0; i < items3.Count; i++)
        {
            if (used3.Contains(i)) continue;
            var item = MakeItem("en_PP-OCRv5_mobile_rec", items3[i]);
            item.Agreement = 1;
            groups.Add(new CrossValidateGroup
            {
                Items = new List<CrossValidateGroupItem> { Placeholder(), Placeholder(), item },
                Agreement = 1,
                UnionRect = ComputeUnionRect(null, null, items3[i].Box)
            });
        }

        AutoFillConfirmation(groups);
        return groups;
    }

    private static void AutoFillConfirmation(List<CrossValidateGroup> groups)
    {
        foreach (var group in groups)
        {
            var active = group.Items.Where(i => !i.IsPlaceholder).ToList();
            if (active.Count == 0) continue;

            bool allGreen = active.All(i => i.Agreement == 3);
            bool anyYellow = active.Any(i => i.Agreement == 2);
            bool allRed = active.All(i => i.Agreement == 1);

            if (allGreen)
            {
                group.ConfirmedText = active[0].Text;
                group.IsConfirmed = true;
            }
            else if (anyYellow)
            {
                var majority = active.First(i => i.Agreement == 2);
                group.ConfirmedText = majority.Text;
                group.IsConfirmed = false;
            }
            // allRed: leave empty, not confirmed
        }
    }

    private static int? BestMatch(List<List<double>>? box, List<OcrItem> candidates, HashSet<int> used)
    {
        if (box is null) return null;
        int? bestIdx = null;
        double bestIou = IouThreshold;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (used.Contains(i)) continue;
            if (candidates[i].Box is null) continue;
            double iou = ComputeIoU(box, candidates[i].Box!);
            if (iou > bestIou)
            {
                bestIou = iou;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

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
        return new Rect((int)minX.Value, (int)minY!.Value, (int)(maxX!.Value - minX.Value), (int)(maxY!.Value - minY!.Value));
    }

    private static void ApplyPerItemAgreement(List<CrossValidateGroupItem> items)
    {
        var active = items.Where(i => !i.IsPlaceholder).ToList();
        if (active.Count == 0) return;

        // Count how many items share each text
        foreach (var item in active)
        {
            int count = active.Count(a => a.Text.Trim() == item.Text.Trim());
            item.Agreement = count >= 3 ? 3 : count == 2 ? 2 : 1;
        }
    }
}
