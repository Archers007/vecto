using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>
/// Unit tests for noise-aware smoothing detection (VEC-11b).
/// Verifies:
/// a) flat colour shapes with anti-aliased edges have interior noise share &lt; 0.03
///    and trace output identical to a trace with the detector's effect absent;
/// b) the same image plus deterministic pseudo-random +/-6 level noise has share &gt;= 0.03;
/// c) tracing the same input twice yields identical noise share.
/// </summary>
public class NoiseTests
{
    static readonly Rgba32 White = new(255, 255, 255, 255);
    static readonly Rgba32 Blue = new(20, 60, 200, 255);
    static readonly Rgba32 Red = new(220, 40, 40, 255);

    /// <summary>
    /// Builds a test image with flat colour shapes (white background, blue rectangle, red circle)
    /// and anti-aliased edges.
    /// </summary>
    static RasterImage CreateCleanImage(int w = 120, int h = 120)
    {
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Box centered at (40, 40) with half-size 20 (box [20..60] x [20..60])
                double bx = Math.Abs(x - 40) - 20;
                double by = Math.Abs(y - 40) - 20;
                double boxDist = Math.Max(bx, by);
                double boxCov = Math.Clamp(0.5 - boxDist, 0.0, 1.0);

                // Circle centered at (85, 85) with radius 25
                double dx = x - 85;
                double dy = y - 85;
                double circleDist = Math.Sqrt(dx * dx + dy * dy) - 25;
                double circleCov = Math.Clamp(0.5 - circleDist, 0.0, 1.0);

                if (boxCov > 0)
                {
                    byte r = (byte)Math.Round(255 * (1 - boxCov) + Blue.R * boxCov);
                    byte g = (byte)Math.Round(255 * (1 - boxCov) + Blue.G * boxCov);
                    byte b = (byte)Math.Round(255 * (1 - boxCov) + Blue.B * boxCov);
                    img.SetPixel(x, y, new Rgba32(r, g, b, 255));
                }
                else if (circleCov > 0)
                {
                    byte r = (byte)Math.Round(255 * (1 - circleCov) + Red.R * circleCov);
                    byte g = (byte)Math.Round(255 * (1 - circleCov) + Red.G * circleCov);
                    byte b = (byte)Math.Round(255 * (1 - circleCov) + Red.B * circleCov);
                    img.SetPixel(x, y, new Rgba32(r, g, b, 255));
                }
                else
                {
                    img.SetPixel(x, y, White);
                }
            }
        }
        return img;
    }

    /// <summary>
    /// Adds deterministic pseudo-random +/-6 level noise to every pixel.
    /// </summary>
    static RasterImage AddNoise(RasterImage src, int seed = 42)
    {
        int w = src.Width, h = src.Height;
        var noisy = new RasterImage(w, h);
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var p = src.GetPixel(x, y);
                int dr = rnd.Next(-6, 7);
                int dg = rnd.Next(-6, 7);
                int db = rnd.Next(-6, 7);
                byte r = (byte)Math.Clamp(p.R + dr, 0, 255);
                byte g = (byte)Math.Clamp(p.G + dg, 0, 255);
                byte b = (byte)Math.Clamp(p.B + db, 0, 255);
                noisy.SetPixel(x, y, new Rgba32(r, g, b, p.A));
            }
        }
        return noisy;
    }

    [Fact]
    public void CleanImageHasLowNoiseShareAndDeterministicOutput()
    {
        var cleanImg = CreateCleanImage();
        var opt = new TraceOptions { Style = ImageStyle.Blended };

        var res1 = Tracer.Trace(cleanImg, opt);
        var res2 = Tracer.Trace(cleanImg, opt);

        // Interior noise share must be below 0.03 threshold and not flagged noisy
        Assert.False(res1.Diagnostics.Params.Noisy);
        Assert.True(res1.Diagnostics.Params.NoiseShare < 0.03);

        // Smoothing parameters remain at their defaults (detector's effect absent)
        Assert.Equal(8, res1.Diagnostics.Params.SmoothIterations);
        Assert.Equal(0.6, res1.Diagnostics.Params.SmoothClamp);

        // Tracing clean image produces identical SVG output
        string svg1 = SvgWriter.Write(res1.Document);
        string svg2 = SvgWriter.Write(res2.Document);
        Assert.Equal(svg1, svg2);
    }

    [Fact]
    public void NoisyImageHasHighNoiseShareAndFlagsNoisy()
    {
        var cleanImg = CreateCleanImage();
        var noisyImg = AddNoise(cleanImg);
        var opt = new TraceOptions { Style = ImageStyle.Blended };

        var res = Tracer.Trace(noisyImg, opt);

        // Interior noise share must be >= 0.03 and flagged noisy
        Assert.True(res.Diagnostics.Params.Noisy);
        Assert.True(res.Diagnostics.Params.NoiseShare >= 0.03);

        // Stronger smoothing parameters applied
        Assert.Equal(32, res.Diagnostics.Params.SmoothIterations);
        Assert.Equal(1.5, res.Diagnostics.Params.SmoothClamp);
    }

    [Fact]
    public void SameInputTwiceYieldsIdenticalShare()
    {
        var cleanImg = CreateCleanImage();
        var noisyImg = AddNoise(cleanImg);
        var opt = new TraceOptions { Style = ImageStyle.Blended };

        var resClean1 = Tracer.Trace(cleanImg, opt);
        var resClean2 = Tracer.Trace(cleanImg, opt);
        Assert.Equal(resClean1.Diagnostics.Params.NoiseShare, resClean2.Diagnostics.Params.NoiseShare);

        var resNoisy1 = Tracer.Trace(noisyImg, opt);
        var resNoisy2 = Tracer.Trace(noisyImg, opt);
        Assert.Equal(resNoisy1.Diagnostics.Params.NoiseShare, resNoisy2.Diagnostics.Params.NoiseShare);
    }

    [Fact]
    public void NoisyImageAbsorbsSmallSpecks()
    {
        var cleanImg = CreateCleanImage();
        // Add isolated 9 px (3x3) specks of a third colour (Red) inside flat white background areas
        for (int dy = 0; dy < 3; dy++)
        {
            for (int dx = 0; dx < 3; dx++)
            {
                cleanImg.SetPixel(10 + dx, 80 + dy, Red);
                cleanImg.SetPixel(80 + dx, 15 + dy, Red);
            }
        }
        var noisyImg = AddNoise(cleanImg);
        var opt = new TraceOptions { Style = ImageStyle.Blended };

        var res = Tracer.Trace(noisyImg, opt);

        Assert.True(res.Diagnostics.Params.Noisy);
        Assert.Equal(14, res.Diagnostics.Params.MinRegionArea);
        Assert.NotEmpty(res.Document.Regions);
        Assert.All(res.Document.Regions, r => Assert.True(r.Area >= 14));
    }
}
