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
/// On noisy inputs, boundary smoothing parameters are adapted to SmoothIterations = 32
/// and SmoothClamp = 1.5 px (measured on JPEG bench: nodes 342 -&gt; 306, bad px 0.723% -&gt; 0.707%).
/// </summary>
internal static class NoiseEstimate
{
    public const double NoisyShare = 0.03;

    /// <summary>Specks smaller than this are absorbed on noisy inputs; Detail=Low's Blended size.</summary>
    public const int NoisyMinRegionArea = 14;

    public static double InteriorNoiseShare(RasterImage img, int[] labels, int w, int h, CancellationToken ct)
    {
        if (w <= 0 || h <= 0 || labels.Length < w * h)
            return 0.0;

        var px = img.Pixels;
        var interiorPerRow = new long[h];
        var noisyPerRow = new long[h];

        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            long interior = 0;
            long noisy = 0;

            int yMin5 = Math.Max(0, y - 2);
            int yMax5 = Math.Min(h - 1, y + 2);
            int yMin3 = Math.Max(0, y - 1);
            int yMax3 = Math.Min(h - 1, y + 1);

            for (int x = 0; x < w; x++)
            {
                int centerLabel = labels[y * w + x];
                if (centerLabel < 0) continue;

                int xMin5 = Math.Max(0, x - 2);
                int xMax5 = Math.Min(w - 1, x + 2);

                bool isInterior = true;
                for (int ny = yMin5; ny <= yMax5; ny++)
                {
                    int row = ny * w;
                    for (int nx = xMin5; nx <= xMax5; nx++)
                    {
                        if (labels[row + nx] != centerLabel)
                        {
                            isInterior = false;
                            break;
                        }
                    }
                    if (!isInterior) break;
                }

                if (!isInterior) continue;

                interior++;

                int xMin3 = Math.Max(0, x - 1);
                int xMax3 = Math.Min(w - 1, x + 1);

                byte minR = 255, maxR = 0;
                byte minG = 255, maxG = 0;
                byte minB = 255, maxB = 0;

                for (int ny = yMin3; ny <= yMax3; ny++)
                {
                    int row = ny * w;
                    for (int nx = xMin3; nx <= xMax3; nx++)
                    {
                        int pi = (row + nx) * 4;
                        byte r = px[pi];
                        byte g = px[pi + 1];
                        byte b = px[pi + 2];

                        if (r < minR) minR = r;
                        if (r > maxR) maxR = r;
                        if (g < minG) minG = g;
                        if (g > maxG) maxG = g;
                        if (b < minB) minB = b;
                        if (b > maxB) maxB = b;
                    }
                }

                int rangeR = maxR - minR;
                int rangeG = maxG - minG;
                int rangeB = maxB - minB;
                int maxRange = Math.Max(rangeR, Math.Max(rangeG, rangeB));

                if (maxRange >= 3)
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
