using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>
/// Anti-aliasing fringe relabel (Segmenter.RelabelFringe): a pixel whose colour is a blend
/// of two neighbouring palette colours must not survive as a sliver of a third colour, a
/// genuine thin feature whose colour is NOT such a blend must survive, and the pass must
/// be deterministic.
/// </summary>
public class FringeTests
{
    static readonly Rgba32 White = new(255, 255, 255, 255);
    static readonly Rgba32 Black = new(0, 0, 0, 255);
    static readonly Rgba32 Blend = new(102, 102, 102, 255);      // 40% black: the ring colour
    static readonly Rgba32 SolidGrey = new(128, 128, 128, 255);  // a real colour, not a fringe

    /// <summary>White canvas, a black rectangle with a 1-px blend ring, and a solid grey block.</summary>
    static RasterImage RingAndBlock()
    {
        int w = 64, h = 48;
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, White);
        for (int y = 12; y <= 27; y++)
            for (int x = 12; x <= 27; x++)
                img.SetPixel(x, y, Black);
        for (int y = 11; y <= 28; y++)
            for (int x = 11; x <= 28; x++)
                if (x == 11 || x == 28 || y == 11 || y == 28)
                    img.SetPixel(x, y, Blend);
        for (int y = 12; y <= 21; y++)
            for (int x = 40; x <= 49; x++)
                img.SetPixel(x, y, SolidGrey);
        return img;
    }

    static TraceOptions Options(int colors) => new()
    {
        Style = ImageStyle.Blended,
        PaletteMode = PaletteMode.FixedCount,
        ColorCount = colors,
    };

    /// <summary>Regions whose display colour is within <paramref name="tol"/> of <paramref name="color"/>.</summary>
    static int CountRegionsNear(VectorDocument doc, Rgba32 color, int tol)
    {
        int count = 0;
        for (int i = 0; i < doc.Palette.Count; i++)
        {
            var c = doc.Palette[i].Color;
            if (Math.Abs(c.R - color.R) <= tol && Math.Abs(c.G - color.G) <= tol && Math.Abs(c.B - color.B) <= tol)
                count += doc.Regions.Count(r => r.PaletteIndex == i);
        }
        return count;
    }

    [Fact]
    public void BlendRingIsRelabelledButSolidGreyBlockSurvives()
    {
        var doc = Tracer.Trace(RingAndBlock(), Options(3)).Document;
        // the ring is a blend of black and white and must be gone; the solid block is the
        // only remaining grey region — without the fringe relabel the ring is a second one
        Assert.Equal(1, CountRegionsNear(doc, new Rgba32(115, 115, 115, 255), 30));
        Assert.Equal(3, doc.Regions.Count);
    }

    [Fact]
    public void GenuineThinBlueLineSurvives()
    {
        int w = 64, h = 48;
        var img = new RasterImage(w, h);
        var red = new Rgba32(255, 0, 0, 255);
        var blue = new Rgba32(0, 0, 255, 255);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, x < 32 ? red : White);
        for (int y = 0; y < h; y++)
            img.SetPixel(32, y, blue);      // the genuine 1-px feature, red on one side, white on the other
        for (int y = 10; y <= 25; y++)      // solid blue block: keeps blue in the palette (the line alone is too rare for k-means)
            for (int x = 44; x <= 55; x++)
                img.SetPixel(x, y, blue);
        var doc = Tracer.Trace(img, Options(3)).Document;
        // blue is far off the red→white Oklab segment, so it is not a blend: the line and
        // the block both survive as blue regions
        Assert.Equal(2, CountRegionsNear(doc, blue, 40));
    }

    [Fact]
    public void RepeatedTracesProduceIdenticalSvg()
    {
        var img = RingAndBlock();
        var a = SvgWriter.Write(Tracer.Trace(img, Options(3)).Document);
        var b = SvgWriter.Write(Tracer.Trace(img, Options(3)).Document);
        Assert.Equal(a, b);
    }

    /// <summary>
    /// VEC-13: the A-or-B side must be decided in gamma sRGB, where anti-aliasing actually
    /// blends. A 4-px black bar whose edge columns are sRGB blends at 50/55/60% black
    /// coverage must keep its full ink width: the Oklab midpoint sits near sRGB 100, so the
    /// old Oklab-side decision shaved such edge pixels off thin dark lines.
    /// </summary>
    [Theory]
    [InlineData(0.50)]
    [InlineData(0.55)]
    [InlineData(0.60)]
    public void AntiAliasedBarKeepsItsInkWidth(double coverage)
    {
        int w = 64, h = 48;
        // sRGB blend of black over white at the given coverage; 50% (127.5) truncates to
        // 127, the dark side of the sRGB split.
        byte edge = (byte)(255 * (1 - coverage));
        var edgeColor = new Rgba32(edge, edge, edge, 255);
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, White);
        for (int y = 0; y < h; y++)
        {
            img.SetPixel(20, y, edgeColor);
            img.SetPixel(21, y, Black);
            img.SetPixel(22, y, Black);
            img.SetPixel(23, y, edgeColor);
        }
        // Solid blocks away from the bar: the black square keeps black in the palette
        // (every bar pixel borders an AA column, so the sampler never sees a flat black
        // pixel), and the grey block keeps grey in it — so the AA edge columns are
        // labelled grey, the third colour, and become candidates for the fringe relabel.
        for (int y = 12; y <= 27; y++)
            for (int x = 40; x <= 55; x++)
                img.SetPixel(x, y, Black);
        for (int y = 32; y <= 41; y++)
            for (int x = 40; x <= 49; x++)
                img.SetPixel(x, y, SolidGrey);
        var doc = Tracer.Trace(img, Options(3)).Document;
        int black = -1;
        for (int i = 0; i < doc.Palette.Count; i++)
        {
            var c = doc.Palette[i].Color;
            if (c.R < 30 && c.G < 30 && c.B < 30) { black = i; break; }
        }
        Assert.True(black >= 0, "black is in the palette");
        // The bar spans the full height, so its filled width is its region's area / height.
        Assert.True(
            doc.Regions.Any(r => r.PaletteIndex == black && Math.Abs(r.Area / (double)h - 4) <= 0.25),
            $"no black region of width 4 ± 0.25 px; black areas: " +
            string.Join(", ", doc.Regions.Where(r => r.PaletteIndex == black).Select(r => r.Area)));
    }

    /// <summary>
    /// VEC-21: relabelling the fringes into a colour must not wash out its palette colour.
    /// Tiny 2x2 ink dots each carry a 12-px blend ring (60% black, so the sRGB side decision
    /// sends it to black); relabelled into black, the rings swamp the few flat black pixels,
    /// the palette colour falls back to the all-pixel mean and the ink turns grey — like
    /// small bold text in a JPEG. The ink must stay near-black.
    /// </summary>
    [Fact]
    public void FringeRelabelKeepsSmallInkDark()
    {
        int w = 80, h = 64;
        var ring = new Rgba32(102, 102, 102, 255);
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, White);
        for (int gy = 0; gy < 7; gy++)          // 70 dots on a 6-px grid
            for (int gx = 0; gx < 10; gx++)
            {
                int x0 = 2 + gx * 6, y0 = 2 + gy * 6;
                for (int y = y0 - 1; y <= y0 + 2; y++)
                    for (int x = x0 - 1; x <= x0 + 2; x++)
                        img.SetPixel(x, y, x >= x0 && x <= x0 + 1 && y >= y0 && y <= y0 + 1 ? Black : ring);
            }
        for (int y = 50; y <= 54; y++)          // a solid 5x5 black block: 9 flat black pixels
            for (int x = 66; x <= 70; x++)
                img.SetPixel(x, y, Black);
        for (int y = 44; y <= 53; y++)          // keeps grey in the palette, so the rings are labelled grey
            for (int x = 4; x <= 13; x++)
                img.SetPixel(x, y, SolidGrey);
        var doc = Tracer.Trace(img, Options(3)).Document;
        int darkest = doc.Palette.Select(e => (int)e.Color.R).Min();
        Assert.True(darkest < 30, $"darkest palette colour {darkest} (ink washed out by its fringe)");
    }
}
