using Vecto.Core;
using Xunit;
using Xunit.Abstractions;

namespace Vecto.Tests;

/// <summary>
/// Structural corner stage (CornerPolygon): thin-bar ends keep both corners and a straight end,
/// circles and shallow staircases get none, narrow rounded tips stay round while true acute
/// wedges stay sharp. Inputs are anti-aliased (8×8 supersampled).
/// </summary>
public class CornerTests
{
    readonly ITestOutputHelper _out;
    public CornerTests(ITestOutputHelper output) => _out = output;

    static readonly Rgba32 White = new(255, 255, 255, 255);
    static readonly Rgba32 Ink = new(30, 40, 160, 255);

    /// <summary>Two inferred colours: the AA ramp is split at its midpoint like real input.</summary>
    static TraceOptions Options => new() { MaxColors = 2 };

    static RasterImage Render(int size, Func<double, double, bool> inside)
    {
        var img = new RasterImage(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int hit = 0;
                for (int sy = 0; sy < 8; sy++)
                    for (int sx = 0; sx < 8; sx++)
                        if (inside(x + (sx + 0.5) / 8, y + (sy + 0.5) / 8)) hit++;
                double f = hit / 64.0;
                byte Mix(byte a, byte b) => (byte)Math.Round(a * f + b * (1 - f));
                img.SetPixel(x, y, new Rgba32(Mix(Ink.R, White.R), Mix(Ink.G, White.G), Mix(Ink.B, White.B), 255));
            }
        return img;
    }

    /// <summary>Outer loop of the ink region (the darker palette entry).</summary>
    static List<CubicBezier> InkLoop(VectorDocument doc)
    {
        var region = doc.Regions.Single(r => doc.Palette[r.PaletteIndex].Color.R < 128);
        return region.Loops[0];
    }

    /// <summary>Joins where the tangent turns by more than 30° — the corners of the fitted path.</summary>
    static List<Vec2> SharpJoins(List<CubicBezier> loop)
    {
        var joins = new List<Vec2>();
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            var tIn = -Tangent(a.P3, a.P2, a.P1, a.P0);
            var tOut = Tangent(b.P0, b.P1, b.P2, b.P3);
            double cos = Math.Clamp(tIn.Dot(tOut), -1, 1);
            if (Math.Acos(cos) * 180 / Math.PI > 30) joins.Add(b.P0);
        }
        return joins;

        static Vec2 Tangent(Vec2 p, Vec2 q1, Vec2 q2, Vec2 q3)
        {
            foreach (var q in new[] { q1, q2, q3 })
                if (q.DistSq(p) > 1e-8) return (q - p).Normalized();
            return new Vec2(0, 0);
        }
    }

    static (RasterImage Img, Vec2[] Corners) Bar(double cx, double cy, double length, double width, double deg)
    {
        double t = deg * Math.PI / 180;
        var u = new Vec2(Math.Cos(t), Math.Sin(t));
        var v = new Vec2(-u.Y, u.X);
        var c = new Vec2(cx, cy);
        var img = Render(128, (x, y) =>
        {
            var d = new Vec2(x, y) - c;
            return Math.Abs(d.Dot(u)) <= length / 2 && Math.Abs(d.Dot(v)) <= width / 2;
        });
        var corners = new[]
        {
            c + u * (length / 2) + v * (width / 2), c + u * (length / 2) - v * (width / 2),
            c - u * (length / 2) + v * (width / 2), c - u * (length / 2) - v * (width / 2),
        };
        return (img, corners);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(90)]
    public void ThreePxBar_BothEndCornersSurvive_EndsStraight(int deg)
    {
        var (img, truth) = Bar(64.3, 63.8, 40, 3, deg);
        var loop = InkLoop(Tracer.Trace(img, Options).Document);
        var joins = SharpJoins(loop);
        _out.WriteLine($"{deg}°: {loop.Count} segs, joins " + string.Join(" ", joins.Select(j => $"({j.X:0.00},{j.Y:0.00})")));
        foreach (var corner in truth)
        {
            double best = joins.Count == 0 ? double.MaxValue : joins.Min(j => Math.Sqrt(j.DistSq(corner)));
            Assert.True(best <= 1.0, $"{deg}°: no corner within 1 px of ({corner.X:0.00},{corner.Y:0.00}) (nearest {best:0.00})");
        }
        // each short end is one straight segment between its two corners
        foreach (var (a, b) in new[] { (truth[0], truth[1]), (truth[2], truth[3]) })
        {
            bool straightEnd = loop.Any(s =>
                s.IsLine(0.05) &&
                ((s.P0.DistSq(a) <= 1 && s.P3.DistSq(b) <= 1) || (s.P0.DistSq(b) <= 1 && s.P3.DistSq(a) <= 1)));
            Assert.True(straightEnd, $"{deg}°: end ({a.X:0.0},{a.Y:0.0})-({b.X:0.0},{b.Y:0.0}) is not one straight segment");
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(20)]
    public void Circle_NoCorners(int radius)
    {
        var img = Render(128, (x, y) => (x - 63.7) * (x - 63.7) + (y - 64.2) * (y - 64.2) <= radius * radius);
        var loop = InkLoop(Tracer.Trace(img, Options).Document);
        var joins = SharpJoins(loop);
        Assert.True(joins.Count == 0, $"r={radius}: {joins.Count} corners, e.g. ({joins.FirstOrDefault().X:0.0},{joins.FirstOrDefault().Y:0.0})");
    }

    [Fact]
    public void ShallowBar_NoCornersOnLongSides()
    {
        var (img, truth) = Bar(64.2, 63.6, 90, 5, 7);
        var loop = InkLoop(Tracer.Trace(img, Options).Document);
        var joins = SharpJoins(loop);
        _out.WriteLine(string.Join(" ", joins.Select(j => $"({j.X:0.00},{j.Y:0.00})")));
        foreach (var j in joins)
            Assert.True(truth.Min(c => Math.Sqrt(j.DistSq(c))) <= 1.5,
                $"corner at ({j.X:0.0},{j.Y:0.0}) is on a long side, not at a bar corner");
        Assert.True(joins.Count <= 4, $"{joins.Count} corners");
    }

    /// <summary>
    /// A tapered stripe (convex hull of a 1-px tip disk and a 7-px base disk, ~23° taper) — the
    /// low-resolution rocket stripe: the tip is round, not one acute corner.
    /// </summary>
    [Fact]
    public void TaperedStripe_RoundedTip_NoCorner()
    {
        var tip = new Vec2(82.3, 44.6);
        var tail = new Vec2(58.0, 62.2);
        const double rTip = 1, rTail = 7;
        var axis = tail - tip;
        var img = Render(128, (x, y) =>
        {
            var p = new Vec2(x, y);
            // inside iff some disk along the axis (radius interpolated) holds p; the excess
            // |p − c(t)| − r(t) is convex in t, so a ternary search finds its minimum
            double lo = 0, hi = 1;
            for (int i = 0; i < 40; i++)
            {
                double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                if (Excess(m1) < Excess(m2)) hi = m2; else lo = m1;
            }
            return Excess((lo + hi) / 2) <= 0;

            double Excess(double t) => Math.Sqrt(p.DistSq(tip + axis * t)) - (rTip + (rTail - rTip) * t);
        });
        var loop = InkLoop(Tracer.Trace(img, Options).Document);
        var joins = SharpJoins(loop);
        var end = tip - axis.Normalized() * rTip;
        _out.WriteLine($"{loop.Count} segs, joins " + string.Join(" ", joins.Select(j => $"({j.X:0.00},{j.Y:0.00})")));
        Assert.DoesNotContain(joins, j => Math.Sqrt(j.DistSq(end)) <= 4);
    }

    /// <summary>A true acute wedge (30° tip, straight sides): the tip stays one sharp corner.</summary>
    [Fact]
    public void AcuteWedge_TipStaysSharp()
    {
        var apex = new Vec2(84.3, 40.7);
        var dir = new Vec2(-0.8, 0.6);   // into the wedge
        double half = 15 * Math.PI / 180;
        var a = apex + Rotate(dir, half) * 40;
        var b = apex + Rotate(dir, -half) * 40;
        var img = Render(128, (x, y) =>
        {
            var p = new Vec2(x, y);
            return Side(apex, a, p) >= 0 && Side(a, b, p) >= 0 && Side(b, apex, p) >= 0;
        });
        var loop = InkLoop(Tracer.Trace(img, Options).Document);
        var joins = SharpJoins(loop);
        _out.WriteLine($"{loop.Count} segs, joins " + string.Join(" ", joins.Select(j => $"({j.X:0.00},{j.Y:0.00})")));
        double best = joins.Count == 0 ? double.MaxValue : joins.Min(j => Math.Sqrt(j.DistSq(apex)));
        Assert.True(best <= 1.0, $"no corner within 1 px of the apex (nearest {best:0.00})");
        Assert.Equal(1, joins.Count(j => Math.Sqrt(j.DistSq(apex)) <= 4));

        static Vec2 Rotate(Vec2 v, double t) => new(v.X * Math.Cos(t) - v.Y * Math.Sin(t), v.X * Math.Sin(t) + v.Y * Math.Cos(t));
        // the triangle apex → a → b winds one way; same-sign cross products mean inside
        double Side(Vec2 p0, Vec2 p1, Vec2 q) => Math.Sign((p1.X - p0.X) * (q.Y - p0.Y) - (p1.Y - p0.Y) * (q.X - p0.X)) * Winding;
    }

    static readonly double Winding = 1;
}
