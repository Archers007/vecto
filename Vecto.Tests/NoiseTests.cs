using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>
/// Unit tests for noise-aware smoothing detection (VEC-11b).
/// Verifies:
/// a) flat colour shapes with anti-aliased edges have interior noise share &lt; 0.03
///    and trace output identical to a trace with the detector's effect absent;
/// b) the same image plus deterministic pseudo-random +/-6 level noise has share &gt;= 0.03;
/// c) tracing the same input twice yields identical noise share;
/// d) a soft shadow ramp that reaches the share is still not flagged (JPEG edge-block test, VEC-19).
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
    /// Adds deterministic JPEG-like noise: each 8x8 block (JPEG's DCT grid) gets one uniform
    /// +/-6 level offset per channel. Block-correlated noise lands in the measured JPEG band
    /// (interior noise share 0.03-0.4); per-pixel iid noise at this amplitude measures ~1.0
    /// and is indistinguishable from texture, so it correctly falls under the textured guard.
    /// </summary>
    static RasterImage AddNoise(RasterImage src, int seed = 42)
    {
        int w = src.Width, h = src.Height;
        var noisy = new RasterImage(w, h);
        var rnd = new Random(seed);
        for (int by = 0; by < h; by += 8)
        {
            for (int bx = 0; bx < w; bx += 8)
            {
                int dr = rnd.Next(-6, 7);
                int dg = rnd.Next(-6, 7);
                int db = rnd.Next(-6, 7);
                for (int y = by; y < Math.Min(by + 8, h); y++)
                {
                    for (int x = bx; x < Math.Min(bx + 8, w); x++)
                    {
                        var p = src.GetPixel(x, y);
                        noisy.SetPixel(x, y, new Rgba32(
                            (byte)Math.Clamp(p.R + dr, 0, 255),
                            (byte)Math.Clamp(p.G + dg, 0, 255),
                            (byte)Math.Clamp(p.B + db, 0, 255),
                            p.A));
                    }
                }
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

    /// <summary>
    /// Builds a continuous-tone test image: a smooth 2-D colour gradient plus mild deterministic noise.
    /// </summary>
    static RasterImage CreateGradientImage(int w = 120, int h = 120)
    {
        var img = new RasterImage(w, h);
        var rnd = new Random(7);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte r = (byte)Math.Clamp(x * 2 + rnd.Next(-2, 3), 0, 255);
                byte g = (byte)Math.Clamp(y * 2 + rnd.Next(-2, 3), 0, 255);
                byte b = (byte)Math.Clamp(x + y + rnd.Next(-2, 3), 0, 255);
                img.SetPixel(x, y, new Rgba32(r, g, b, 255));
            }
        }
        return img;
    }

    [Fact]
    public void ContinuousToneImageIsTexturedAndNotNoisy()
    {
        var img = CreateGradientImage();
        var opt = new TraceOptions { Style = ImageStyle.Blended };

        var res = Tracer.Trace(img, opt);

        // Smooth gradients are texture, not JPEG ringing: textured guard fires,
        // noisy treatment (extra smoothing, speck cleanup) stays off
        Assert.True(res.Diagnostics.Params.Textured);
        Assert.False(res.Diagnostics.Params.Noisy);
        Assert.Equal(8, res.Diagnostics.Params.SmoothIterations);
        Assert.Equal(0.6, res.Diagnostics.Params.SmoothClamp);
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

        // Flat art with noise is JPEG-like, not textured: the guard must not fire
        Assert.False(res.Diagnostics.Params.Textured);

        // Stronger smoothing parameters applied
        Assert.Equal(32, res.Diagnostics.Params.SmoothIterations);
        Assert.Equal(1.5, res.Diagnostics.Params.SmoothClamp);
    }

    /// <summary>
    /// Flat art with a wide soft drop shadow (linear falloff, like a blurred shadow): the ramp makes
    /// many interior pixels vary, but smoothly and independently of the 8x8 grid.
    /// </summary>
    static RasterImage CreateSoftShadowImage(int w = 240, int h = 240)
    {
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double bx = Math.Abs(x - 120) - 30;
                double by = Math.Abs(y - 120) - 30;
                double d = Math.Max(bx, by);
                if (d <= 0)
                {
                    img.SetPixel(x, y, Blue);
                }
                else
                {
                    // 32 px shadow ramp from grey 120 to white (noise share ~0.08)
                    byte v = (byte)Math.Round(120 + 135 * Math.Clamp(d / 32.0, 0.0, 1.0));
                    img.SetPixel(x, y, new Rgba32(v, v, v, 255));
                }
            }
        }
        return img;
    }

    [Fact]
    public void SoftShadowReachesNoiseShareButIsNotNoisy()
    {
        var res = Tracer.Trace(CreateSoftShadowImage(), new TraceOptions { Style = ImageStyle.Blended });

        // The ramp alone trips the share test; the JPEG edge-block test must reject it
        Assert.True(res.Diagnostics.Params.NoiseShare >= 0.03);
        Assert.False(res.Diagnostics.Params.Textured);
        Assert.False(res.Diagnostics.Params.Noisy);
        Assert.Equal(8, res.Diagnostics.Params.SmoothIterations);
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
