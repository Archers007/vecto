using System.Runtime.CompilerServices;

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
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (y) =>
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
    /// relabelled to A or B depending on which side of the sRGB midpoint between the two
    /// labels' palette centres it projects to: anti-aliasing blends in gamma sRGB, and the
    /// Oklab midpoint sits near sRGB 100, so an Oklab-side decision sends partly-dark edge
    /// pixels (sRGB 100–128) to the light side and thin dark lines lose ink. Reads from a
    /// snapshot of the labels and writes into <paramref name="labels"/>, so the pass is
    /// deterministic under Parallel.For.
    /// </summary>
    public static void RelabelFringe(RasterImage img, int[] labels, Lab[] centers, int w, int h, CancellationToken ct)
    {
        var px = img.Pixels;
        var src = (int[])labels.Clone();
        float maxR2 = FringeMaxResidual * FringeMaxResidual;
        // Per-label colour for the A-or-B side decision: the k-means centre converted to
        // sRGB. Anti-aliasing blends in gamma sRGB and the Oklab midpoint sits near sRGB
        // 100, so the side is decided by the pixel's projection in sRGB; taking the colour
        // from the centre (not from the mean of the pixels currently holding the label,
        // fringe pixels included) keeps the split point at the palette colours themselves.
        int nc = centers.Length;
        var rgb = new float[nc * 3];
        for (int c = 0; c < nc; c++)
        {
            LabToRgb(centers[c], out float r, out float g, out float b);
            rgb[c * 3] = r;
            rgb[c * 3 + 1] = g;
            rgb[c * 3 + 2] = b;
        }
        int span = 2 * FringeRadius + 1;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (y) =>
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
                        if (r < bestR)
                        {
                            bestR = r;
                            // Side decision in gamma sRGB (where anti-aliasing blends);
                            // falls back to the Oklab projection when the two centres are
                            // indistinguishable in sRGB.
                            int ia = nb[a] * 3, ib = nb[b] * 3;
                            float sx = rgb[ib] - rgb[ia], sy = rgb[ib + 1] - rgb[ia + 1], sz = rgb[ib + 2] - rgb[ia + 2];
                            float ss = sx * sx + sy * sy + sz * sz;
                            float ts = t;
                            if (ss > 1e-6f)
                                ts = ((px[pi] - rgb[ia]) * sx + (px[pi + 1] - rgb[ia + 1]) * sy + (px[pi + 2] - rgb[ia + 2]) * sz) / ss;
                            bestL = ts < 0.5f ? nb[a] : nb[b];
                        }
                    }
                if (bestL >= 0 && bestR <= maxR2 && bestR < dc)
                    labels[i] = bestL;
            }
        });
    }

    /// <summary>Oklab back to sRGB channels in 0–255 (Björn Ottosson's public-domain inverse).</summary>
    static void LabToRgb(in Lab lab, out float r, out float g, out float b)
    {
        float l_ = lab.L + 0.3963377774f * lab.A + 0.2158037573f * lab.B;
        float m_ = lab.L - 0.1055613458f * lab.A - 0.0638541728f * lab.B;
        float s_ = lab.L - 0.0894841775f * lab.A - 1.2914855480f * lab.B;
        float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
        r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
        r = LinearToSrgb(Math.Clamp(r, 0f, 1f)) * 255f;
        g = LinearToSrgb(Math.Clamp(g, 0f, 1f)) * 255f;
        b = LinearToSrgb(Math.Clamp(b, 0f, 1f)) * 255f;
    }

    static float LinearToSrgb(float v) =>
        v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    /// <summary>
    /// Repeatedly relabels regions smaller than <paramref name="minArea"/> to the neighbor
    /// they share the longest border with — this is what eats anti-aliasing fringes and dust.
    /// A merge target can be transparency (-1), which deletes the speck — but only when the
    /// region genuinely borders transparent pixels: out-of-bounds neighbors are skipped, so
    /// the image edge is never mistaken for transparency. A region with no in-bounds border
    /// at all (e.g. it covers the whole image) falls back to -1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AbsorbSmallRegions(int[] labels, int w, int h, int minArea, CancellationToken ct)
    {
        if (minArea <= 1) return;
        const int passes = 10;
        var regionId = new int[w * h];
        var stack = new int[w * h];
        // per pass, the pixels that actually changed (pixel, label before, label after):
        // a pass is a pure function of the labels, so once a pass undoes the previous one
        // (small regions swapping labels back and forth) the rest of the passes just repeat
        // that 2-cycle and the final labels are known without running them
        var log = new List<(int I, int From, int To)>();
        var prevLog = new List<(int I, int From, int To)>();
        // border tallies in first-seen order (ties go to the first-seen neighbour);
        // at[neighbour + 1] is its index in the tally, -1 when not seen yet
        int[] at = Array.Empty<int>();
        var nbId = new List<int>();
        var nbLen = new List<int>();
        for (int pass = 0; pass < passes; pass++)
        {
            ct.ThrowIfCancellationRequested();
            var (areas, palette) = ConnectedComponents(labels, w, h, regionId, stack);
            int rc = areas.Count;
            var small = new List<int>();
            for (int r = 0; r < rc; r++)
                if (areas[r] < minArea) small.Add(r);
            if (small.Count == 0) return;

            // pixels of every small region in ascending order, packed into one array
            var slot = new int[rc];
            Array.Fill(slot, -1);
            var first = new int[small.Count + 1];
            for (int s = 0; s < small.Count; s++)
            {
                slot[small[s]] = s;
                first[s + 1] = first[s] + areas[small[s]];
            }
            var fill = new int[small.Count];
            Array.Copy(first, fill, small.Count);
            var pixels = new int[first[small.Count]];
            for (int i = 0; i < labels.Length; i++)
            {
                int id = regionId[i];
                if (id >= 0 && slot[id] >= 0) pixels[fill[slot[id]]++] = i;
            }

            at = new int[rc + 1];
            Array.Fill(at, -1);
            log.Clear();
            small.Sort((a, b) => areas[a] != areas[b] ? areas[a] - areas[b] : a - b);
            foreach (var r in small)
            {
                int s = slot[r];
                for (int k = first[s]; k < first[s + 1]; k++)
                {
                    int i = pixels[k];
                    int x = i % w, y = i / w;
                    if (x > 0) Tally(regionId[i - 1], r);
                    if (x < w - 1) Tally(regionId[i + 1], r);
                    if (y > 0) Tally(regionId[i - w], r);
                    if (y < h - 1) Tally(regionId[i + w], r);
                }
                int target = -1, best = -1;
                for (int k = 0; k < nbId.Count; k++)
                {
                    if (nbLen[k] > best) { best = nbLen[k]; target = nbId[k]; }
                    at[nbId[k] + 1] = -1;
                }
                nbId.Clear();
                nbLen.Clear();
                int targetLabel = target < 0 ? -1 : palette[target];
                for (int k = first[s]; k < first[s + 1]; k++)
                {
                    int i = pixels[k];
                    if (labels[i] == targetLabel) continue;
                    log.Add((i, labels[i], targetLabel));
                    labels[i] = targetLabel;
                }
            }
            if (log.Count == 0) return;   // a fixed point: every later pass is the same no-op
            if (Undoes(log, prevLog))
            {
                // labels are back to where they were two passes ago; an odd number of
                // remaining passes ends on the other state of the cycle
                if ((passes - 1 - pass) % 2 == 1)
                    foreach (var (i, _, to) in prevLog) labels[i] = to;
                return;
            }
            (log, prevLog) = (prevLog, log);
        }

        void Tally(int neighbor, int r)
        {
            if (neighbor == r) return;
            int k = at[neighbor + 1];
            if (k < 0)
            {
                at[neighbor + 1] = nbId.Count;
                nbId.Add(neighbor);
                nbLen.Add(1);
            }
            else nbLen[k]++;
        }
    }

    /// <summary>Does <paramref name="log"/> restore exactly the labels <paramref name="prev"/> changed?</summary>
    static bool Undoes(List<(int I, int From, int To)> log, List<(int I, int From, int To)> prev)
    {
        if (log.Count != prev.Count) return false;
        var before = new Dictionary<int, int>(prev.Count);
        foreach (var (i, from, _) in prev) before[i] = from;
        foreach (var (i, _, to) in log)
            if (!before.TryGetValue(i, out int from) || from != to) return false;
        return true;
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
        var (areas, palette) = ConnectedComponents(labels, w, h, regionId, new int[w * h]);
        return (regionId, areas, palette);
    }

    /// <summary>Fills <paramref name="regionId"/>; <paramref name="stack"/> is w·h scratch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static (List<int> areas, List<int> palette) ConnectedComponents(int[] labels, int w, int h, int[] regionId, int[] stack)
    {
        Array.Fill(regionId, -1);
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
        return (areas, palette);
    }
}
