using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>
/// JPEG-style near-black pathology: k-means can keep two clusters whose DISPLAY colours
/// are near-identical (the Korea-flag node bloat), so flat bars trace as mosaics of
/// near-identical regions. This reproduces the ingredients deterministically in code:
/// flat dark bars, near-black noise inside them, grey fringes around them.
/// </summary>
public class QualityJpegTests
{
    static readonly Rgba32 White = new(255, 255, 255, 255);
    static readonly Rgba32 Fringe = new(96, 96, 96, 255);
    static readonly Rgba32[] Noise =
    {
        new(2, 2, 2, 255), new(3, 3, 3, 255), new(4, 4, 4, 255), new(5, 5, 5, 255),
        new(6, 6, 6, 255), new(7, 7, 7, 255), new(8, 8, 8, 255),
    };

    /// <summary>Deterministic hash → one noise colour per 4×4 block, weighted toward #040404.</summary>
    static Rgba32 BlockColor(int bx, int by, int bar)
    {
        int h = (bx * 73856093 ^ by * 19349663 ^ bar * 83492791) & 0x7fffffff;
        int v = h % 100;
        return Noise[v < 50 ? 2 : v < 62 ? 3 : v < 74 ? 1 : v < 85 ? 4 : v < 92 ? 0 : v < 98 ? 5 : 6];
    }

    [Fact]
    public void NearBlackNoiseMergesToOneEntryAndOneRegionPerBar()
    {
        int w = 120, h = 72;
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, White);
        // three flat bars of 4×4 near-black blocks (the analogue of JPEG ringing:
        // one source colour smeared into several near-identical values), 1px grey fringe
        int[] barY = { 10, 30, 50 };
        for (int bar = 0; bar < barY.Length; bar++)
        {
            int y0 = barY[bar], y1 = y0 + 12;
            for (int y = y0; y < y1; y++)
                for (int x = 20; x < 100; x++)
                    img.SetPixel(x, y, BlockColor(x / 4, (y - y0) / 4, bar));
            for (int y = y0 - 1; y <= y1; y++)
                for (int x = 19; x <= 100; x++)
                    if (y == y0 - 1 || y == y1 || x == 19 || x == 100)
                        img.SetPixel(x, y, Fringe);
        }

        // FixedCount skips cluster merging, so the near-black noise survives as its own
        // palette entries whose display colours are near-identical to the bar entry —
        // the same state JPEG ringing leaves the Korea flag in.
        var options = new TraceOptions
        {
            Style = ImageStyle.Blended,
            PaletteMode = PaletteMode.FixedCount,
            ColorCount = 6,
        };
        var doc = Tracer.Trace(img, options).Document;

        var dark = doc.Palette.Where(e => e.Color.R < 32 && e.Color.G < 32 && e.Color.B < 32).ToList();
        Assert.Single(dark);
        int darkIndex = doc.Palette.IndexOf(dark[0]);
        Assert.Equal(3, doc.Regions.Count(r => r.PaletteIndex == darkIndex));
        Assert.Equal(4, doc.Regions.Count);
    }
}
