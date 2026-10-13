namespace Vecto.Core;

internal sealed class Segmentation
{
    /// <summary>Palette index per pixel; -1 = transparent.</summary>
    public required int[] Labels { get; init; }
    /// <summary>4-connected component id per pixel; -1 = transparent.</summary>
    public required int[] RegionId { get; init; }
    public int RegionCount { get; init; }
    public required int[] RegionPalette { get; init; }
    public required int[] RegionArea { get; init; }
}

internal static class Segmenter
{
    public const byte OpaqueThreshold = 128;

    public static int[] Label(RasterImage img, Lab[] centers, CancellationToken ct)
    {
        int w = img.Width, h = img.Height;
        var labels = new int[w * h];
        if (centers.Length == 0)
        {
            Array.Fill(labels, -1);
            return labels;
        }
        var px = img.Pixels;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            int i = y * w;
            for (int x = 0; x < w; x++, i++)
            {
                int pi = i * 4;
                if (px[pi + 3] < OpaqueThreshold)
                {
                    labels[i] = -1;
                    continue;
                }
                var lab = Oklab.FromRgb(px[pi], px[pi + 1], px[pi + 2]);
                int best = 0;
                float bd = float.MaxValue;
                for (int c = 0; c < centers.Length; c++)
                {
                    float d = Oklab.DistSq(lab, centers[c]);
                    if (d < bd) { bd = d; best = c; }
                }
                labels[i] = best;
            }
        });
        return labels;
    }

    /// <summary>Neighbourhood radius for spotting the two colours a fringe pixel blends.</summary>
    public const int FringeRadius = 1;
    /// <summary>Max Oklab distance from the A→B segment for a pixel to count as a blend.</summary>
    public const float FringeMaxResidual = 0.08f;

    /// <summary>
    /// Relabels anti-aliasing fringes: a pixel whose colour is a blend of two other palette
    /// colours A and B often sits closest to a third centre C, so soft edges trace as long
    /// thin slivers of C — too big for <see cref="AbsorbSmallRegions"/> to eat. When the
    /// pixel's Oklab colour lies close to the segment centre[A] → centre[B] (and closer to
    /// that segment than to its own centre), it is really an A/B blend pixel and is
    /// relabelled to A or B depending on which side of the midpoint it projects to. Reads
    /// from a snapshot of the labels and writes into <paramref name="labels"/>, so the pass
    /// is deterministic under Parallel.For.
    /// </summary>
    public static void RelabelFringe(RasterImage img, int[] labels, Lab[] centers, int w, int h, CancellationToken ct)
    {
        var px = img.Pixels;
        var src = (int[])labels.Clone();
        float maxR2 = FringeMaxResidual * FringeMaxResidual;
        int span = 2 * FringeRadius + 1;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            Span<int> nb = stackalloc int[span * span];
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, c = src[i];
                if (c < 0) continue;
                int n = 0;
                for (int dy = -FringeRadius; dy <= FringeRadius; dy++)
                {
                    int yy = y + dy; if (yy < 0 || yy >= h) continue;
                    for (int dx = -FringeRadius; dx <= FringeRadius; dx++)
                    {
                        int xx = x + dx; if (xx < 0 || xx >= w) continue;
                        int l = src[yy * w + xx];
                        if (l < 0 || l == c) continue;
                        bool seen = false;
                        for (int k = 0; k < n; k++) if (nb[k] == l) { seen = true; break; }
                        if (!seen) nb[n++] = l;
                    }
                }
                if (n < 2) continue;
                int pi = i * 4;
                var p = Oklab.FromRgb(px[pi], px[pi + 1], px[pi + 2]);
                float dc = Oklab.DistSq(p, centers[c]);
                float bestR = float.MaxValue; int bestL = -1;
                for (int a = 0; a < n; a++)
                    for (int b = a + 1; b < n; b++)
                    {
                        var A = centers[nb[a]]; var B = centers[nb[b]];
                        float ux = B.L - A.L, uy = B.A - A.A, uz = B.B - A.B;
                        float uu = ux * ux + uy * uy + uz * uz; if (uu < 1e-9f) continue;
                        float t = ((p.L - A.L) * ux + (p.A - A.A) * uy + (p.B - A.B) * uz) / uu;
                        if (t <= 0f || t >= 1f) continue;
                        float qx = A.L + t * ux - p.L, qy = A.A + t * uy - p.A, qz = A.B + t * uz - p.B;
                        float r = qx * qx + qy * qy + qz * qz;
                        if (r < bestR) { bestR = r; bestL = t < 0.5f ? nb[a] : nb[b]; }
                    }
                if (bestL >= 0 && bestR <= maxR2 && bestR < dc)
                    labels[i] = bestL;
            }
        });
    }

    /// <summary>
    /// Repeatedly relabels regions smaller than <paramref name="minArea"/> to the neighbor
    /// they share the longest border with — this is what eats anti-aliasing fringes and dust.
    /// A merge target can be transparency (-1), which deletes the speck — but only when the
    /// region genuinely borders transparent pixels: out-of-bounds neighbors are skipped, so
    /// the image edge is never mistaken for transparency. A region with no in-bounds border
    /// at all (e.g. it covers the whole image) falls back to -1.
    /// </summary>
    public static void AbsorbSmallRegions(int[] labels, int w, int h, int minArea, CancellationToken ct)
    {
        if (minArea <= 1) return;
        for (int pass = 0; pass < 10; pass++)
        {
            ct.ThrowIfCancellationRequested();
            var (regionId, areas, palette) = ConnectedComponents(labels, w, h);
            var small = new List<int>();
            for (int r = 0; r < areas.Count; r++)
                if (areas[r] < minArea) small.Add(r);
            if (small.Count == 0) return;

            var slot = new Dictionary<int, int>(small.Count);
            for (int s = 0; s < small.Count; s++) slot[small[s]] = s;
            var pixels = new List<int>[small.Count];
            for (int s = 0; s < small.Count; s++) pixels[s] = new List<int>();
            for (int i = 0; i < labels.Length; i++)
                if (regionId[i] >= 0 && slot.TryGetValue(regionId[i], out int s))
                    pixels[s].Add(i);

            small.Sort((a, b) => areas[a] != areas[b] ? areas[a] - areas[b] : a - b);
            foreach (var r in small)
            {
                var border = new Dictionary<int, int>();
                foreach (var i in pixels[slot[r]])
                {
                    int x = i % w, y = i / w;
                    Tally(i - 1, x > 0);
                    Tally(i + 1, x < w - 1);
                    Tally(i - w, y > 0);
                    Tally(i + w, y < h - 1);

                    void Tally(int j, bool inBounds)
                    {
                        if (!inBounds) return;
                        int neighbor = regionId[j];
                        if (neighbor == r) return;
                        border[neighbor] = border.GetValueOrDefault(neighbor) + 1;
                    }
                }
                int target = -1, best = -1;
                foreach (var (neighbor, len) in border)
                    if (len > best) { best = len; target = neighbor; }
                int targetLabel = target < 0 ? -1 : palette[target];
                foreach (var i in pixels[slot[r]]) labels[i] = targetLabel;
            }
        }
    }

    public static Segmentation Components(int[] labels, int w, int h)
    {
        var (regionId, areas, palette) = ConnectedComponents(labels, w, h);
        return new Segmentation
        {
            Labels = labels,
            RegionId = regionId,
            RegionCount = areas.Count,
            RegionPalette = palette.ToArray(),
            RegionArea = areas.ToArray(),
        };
    }

    static (int[] regionId, List<int> areas, List<int> palette) ConnectedComponents(int[] labels, int w, int h)
    {
        var regionId = new int[w * h];
        Array.Fill(regionId, -1);
        var stack = new int[w * h];
        var areas = new List<int>();
        var palette = new List<int>();
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] < 0 || regionId[i] >= 0) continue;
            int id = areas.Count, lab = labels[i], area = 0, sp = 0;
            stack[sp++] = i;
            regionId[i] = id;
            while (sp > 0)
            {
                int p = stack[--sp];
                area++;
                int x = p % w;
                if (x > 0 && regionId[p - 1] < 0 && labels[p - 1] == lab) { regionId[p - 1] = id; stack[sp++] = p - 1; }
                if (x < w - 1 && regionId[p + 1] < 0 && labels[p + 1] == lab) { regionId[p + 1] = id; stack[sp++] = p + 1; }
                if (p >= w && regionId[p - w] < 0 && labels[p - w] == lab) { regionId[p - w] = id; stack[sp++] = p - w; }
                if (p < w * (h - 1) && regionId[p + w] < 0 && labels[p + w] == lab) { regionId[p + w] = id; stack[sp++] = p + w; }
            }
            areas.Add(area);
            palette.Add(lab);
        }
        return (regionId, areas, palette);
    }
}
