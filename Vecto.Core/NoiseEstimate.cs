namespace Vecto.Core;

/// <summary>
/// Interior noise share detector for JPEG-like inputs.
///
/// Measures the proportion of interior pixels that exhibit high local variation:
/// - A pixel is INTERIOR when it is opaque (label &gt;= 0) and every pixel in its 5×5
///   neighbourhood (clipped at the image border) has the same label.
/// - For each interior pixel, the 3×3 range (max - min, neighbourhood clipped at the
///   border) of the raw input is computed in each of R, G, B; the largest of the three
///   is taken. Interior pixels whose range is &gt;= 3 are counted.
/// - InteriorNoiseShare = counted / interior pixels (0 when there are no interior pixels).
///   An input is considered noisy when share &gt;= 0.03.
///
/// Benchmarks on 54-image quality suite:
/// - Clean inputs: &lt;= 0.0023
/// - Small 128-px inputs: &lt;= 0.0203
/// - JPEG q55 inputs: &gt;= 0.0371
///
/// Downscaled text, blurred shadows and dithered gradients also reach that share, so a second,
/// JPEG-specific test confirms it (see <see cref="EdgeBlockRoughness"/>): JPEG ringing fills the
/// 8×8 DCT blocks that contain an edge, while softness from resampling or blur does not follow the grid.
///
/// On noisy inputs, boundary smoothing parameters are adapted to SmoothIterations = 32
/// and SmoothClamp = 1.5 px (measured on JPEG bench: nodes 342 -&gt; 306, bad px 0.723% -&gt; 0.707%).
/// </summary>
internal static class NoiseEstimate
{
    public const double NoisyShare = 0.03;

    /// <summary>Specks smaller than this are absorbed on noisy inputs; Detail=Low's Blended size.</summary>
    public const int NoisyMinRegionArea = 14;

    /// <summary>
    /// Interior noise share at or above which the input is TEXTURED (photos, textured art, old scans):
    /// there the thin in-between colour bands are real shading, not anti-aliasing fringes, and the
    /// high share is texture, not JPEG ringing — so both the fringe relabel and the noisy treatment
    /// are skipped. Measured shares: photo-mona-lisa 0.999, art-great-wave 0.958, Burroughs patent
    /// scan 0.56, JPEG-compressed flat art 0.037-0.28, clean/small inputs &lt;= 0.02.
    /// </summary>
    public const double TexturedShare = 0.4;

    /// <summary>
    /// Edge-block roughness at or above which a noise share is confirmed as JPEG ringing.
    /// Measured: JPEG q85/q55 inputs 0.50-0.95, clean/mid/small inputs (downscaled text, blurred
    /// shadows, dithered gradients) &lt;= 0.28.
    /// </summary>
    public const double JpegEdgeBlockShare = 0.4;

    /// <summary>
    /// Share of interior pixels lying in an 8×8 block (aligned to the image origin, like JPEG's DCT
    /// grid) that contains a label change, which are ROUGH: 3×3 range &gt;= 3 AND 4-neighbour
    /// Laplacian magnitude &gt;= 3 (max over R, G, B). JPEG quantisation rings across the whole block
    /// of an edge, so nearly every interior pixel there is rough; smooth gradients have a low
    /// Laplacian, dithering a low range, and anti-aliasing/blur from resampling decays with distance
    /// from the edge instead of filling the block. Pixels on the image border are skipped.
    /// </summary>
    public static double EdgeBlockRoughness(RasterImage img, int[] labels, int w, int h, CancellationToken ct)
    {
        if (w <= 0 || h <= 0 || labels.Length < w * h)
            return 0.0;

        var px = img.Pixels;
        int blockRows = (h + 7) / 8;
        var interiorPerRow = new long[blockRows];
        var roughPerRow = new long[blockRows];

        Parallel.For(0, blockRows, new ParallelOptions { CancellationToken = ct }, [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (br) =>
        {
            long interior = 0;
            long rough = 0;
            int y0 = br * 8, y1 = Math.Min(h, y0 + 8);

            for (int x0 = 0; x0 < w; x0 += 8)
            {
                int x1 = Math.Min(w, x0 + 8);
                int first = labels[y0 * w + x0];
                bool mixed = false;
                for (int y = y0; y < y1 && !mixed; y++)
                    for (int x = x0; x < x1; x++)
                        if (labels[y * w + x] != first) { mixed = true; break; }
                if (!mixed) continue;

                for (int y = Math.Max(1, y0); y < Math.Min(h - 1, y1); y++)
                {
                    for (int x = Math.Max(1, x0); x < Math.Min(w - 1, x1); x++)
                    {
                        if (labels[y * w + x] < 0 || !IsInterior(labels, w, h, x, y)) continue;
                        interior++;
                        if (Range3(px, w, h, x, y) < 3) continue;

                        int pc = (y * w + x) * 4;
                        int maxLap = 0;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            int lap = Math.Abs(4 * px[pc + ch] - px[pc - 4 + ch] - px[pc + 4 + ch] - px[pc - w * 4 + ch] - px[pc + w * 4 + ch]);
                            if (lap > maxLap) maxLap = lap;
                        }
                        if (maxLap >= 3)
                            rough++;
                    }
                }
            }

            interiorPerRow[br] = interior;
            roughPerRow[br] = rough;
        });

        long totalInterior = 0;
        long totalRough = 0;
        for (int i = 0; i < blockRows; i++)
        {
            totalInterior += interiorPerRow[i];
            totalRough += roughPerRow[i];
        }

        return totalInterior == 0 ? 0.0 : (double)totalRough / totalInterior;
    }

    /// <summary>True when every pixel of the 5×5 neighbourhood (clipped at the border) has the label at (x, y).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static bool IsInterior(int[] labels, int w, int h, int x, int y)
    {
        int c = labels[y * w + x];
        int xMax = Math.Min(w - 1, x + 2), yMax = Math.Min(h - 1, y + 2);
        for (int ny = Math.Max(0, y - 2); ny <= yMax; ny++)
        {
            int row = ny * w;
            for (int nx = Math.Max(0, x - 2); nx <= xMax; nx++)
                if (labels[row + nx] != c)
                    return false;
        }
        return true;
    }

    /// <summary>Largest per-channel (R, G, B) range of the 3×3 neighbourhood, clipped at the border.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static int Range3(byte[] px, int w, int h, int x, int y)
    {
        byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        int xMax = Math.Min(w - 1, x + 1), yMax = Math.Min(h - 1, y + 1);
        for (int ny = Math.Max(0, y - 1); ny <= yMax; ny++)
        {
            for (int nx = Math.Max(0, x - 1); nx <= xMax; nx++)
            {
                int pi = (ny * w + nx) * 4;
                byte r = px[pi], g = px[pi + 1], b = px[pi + 2];
                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                if (g < minG) minG = g;
                if (g > maxG) maxG = g;
                if (b < minB) minB = b;
                if (b > maxB) maxB = b;
            }
        }
        return Math.Max(maxR - minR, Math.Max(maxG - minG, maxB - minB));
    }

    public static double InteriorNoiseShare(RasterImage img, int[] labels, int w, int h, CancellationToken ct)
    {
        if (w <= 0 || h <= 0 || labels.Length < w * h)
            return 0.0;

        var px = img.Pixels;
        var interiorPerRow = new long[h];
        var noisyPerRow = new long[h];

        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (y) =>
        {
            long interior = 0;
            long noisy = 0;

            for (int x = 0; x < w; x++)
            {
                if (labels[y * w + x] < 0 || !IsInterior(labels, w, h, x, y)) continue;
                interior++;
                if (Range3(px, w, h, x, y) >= 3)
                    noisy++;
            }

            interiorPerRow[y] = interior;
            noisyPerRow[y] = noisy;
        });

        long totalInterior = 0;
        long totalNoisy = 0;
        for (int y = 0; y < h; y++)
        {
            totalInterior += interiorPerRow[y];
            totalNoisy += noisyPerRow[y];
        }

        return totalInterior == 0 ? 0.0 : (double)totalNoisy / totalInterior;
    }
}
