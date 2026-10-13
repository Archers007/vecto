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
}
