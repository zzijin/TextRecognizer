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

        // Collect all items from all 3 models into a flat list
        var all = new List<(int source, OcrItem item)>();
        foreach (var item in items1) all.Add((0, item));
        foreach (var item in items2) all.Add((1, item));
        foreach (var item in items3) all.Add((2, item));

        if (all.Count == 0) return [];

        // Sort by Y center (top to bottom), then by X (left to right)
        all.Sort((a, b) =>
        {
            var aCY = a.item.BoundingRect.Y + a.item.BoundingRect.Height / 2.0;
            var bCY = b.item.BoundingRect.Y + b.item.BoundingRect.Height / 2.0;
            int yCmp = aCY.CompareTo(bCY);
            return yCmp != 0 ? yCmp : a.item.BoundingRect.X.CompareTo(b.item.BoundingRect.X);
        });

        // Cluster into Y-rows using half the average box height as threshold
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
                // Weighted average toward new item to handle gradual Y drift
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

        // Within each Y-row, sort by X and group items at similar positions across models
        var groups = new List<CrossValidateGroup>();
        foreach (var row in yRows)
        {
            row.Sort((a, b) => a.item.BoundingRect.X.CompareTo(b.item.BoundingRect.X));
            var used = new bool[row.Count];

            for (int i = 0; i < row.Count; i++)
            {
                if (used[i]) continue;
                var (srcI, itemI) = row[i];

                var groupItems = new CrossValidateGroupItem[3];
                var boxes = new List<List<double>>?[3];
                groupItems[srcI] = MakeItem(_modelNames[srcI], itemI);
                boxes[srcI] = itemI.Box;
                used[i] = true;

                // Find items from OTHER models overlapping in X within this row
                for (int j = i + 1; j < row.Count; j++)
                {
                    if (used[j]) continue;
                    var (srcJ, itemJ) = row[j];
                    if (srcJ == srcI) continue; // same model at different X → separate group

                    if (itemI.Box is not null && itemJ.Box is not null &&
                        ComputeIoU(itemI.Box, itemJ.Box) >= IouThreshold)
                    {
                        groupItems[srcJ] = MakeItem(_modelNames[srcJ], itemJ);
                        boxes[srcJ] = itemJ.Box;
                        used[j] = true;
                    }
                }

                for (int s = 0; s < 3; s++)
                    groupItems[s] ??= Placeholder();

                var itemList = groupItems.ToList();
                ApplyPerItemAgreement(itemList);
                groups.Add(new CrossValidateGroup
                {
                    Items = itemList,
                    Agreement = itemList.Where(x => !x.IsPlaceholder).Select(x => x.Agreement).DefaultIfEmpty(1).Max(),
                    UnionRect = ComputeUnionRect(boxes)
                });
            }
        }

        AutoFillConfirmation(groups);
        return groups;
    }

    private static readonly string[] _modelNames = [
        "PP-OCRv5_server_rec", "PP-OCRv5_mobile_rec", "en_PP-OCRv5_mobile_rec"
    ];

    private static void AutoFillConfirmation(List<CrossValidateGroup> groups)
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

        foreach (var item in active)
        {
            int count = active.Count(a => a.Text.Trim() == item.Text.Trim());
            item.Agreement = count >= 3 ? 3 : count == 2 ? 2 : 1;
        }
    }
}
