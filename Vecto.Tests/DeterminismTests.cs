using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

public class DeterminismTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    public void RootMatchesPowWithinRounding(int n)
    {
        for (int i = 1; i <= 1000; i++)
        {
            double x = i / 1000.0 * (i % 7 + 1);
            double expect = Math.Pow(x, 1.0 / n);
            Assert.True(Math.Abs(DetMath.Root(x, n) - expect) <= 4e-16 * expect, $"x={x} n={n}");
        }
    }

    [Fact]
    public void CbrtFloatMatchesWithinOneUlp()
    {
        for (int i = 0; i <= 4096; i++)
        {
            float x = i / 4096f;
            float a = DetMath.Cbrt(x), b = MathF.Cbrt(x);
            Assert.True(Math.Abs(BitConverter.SingleToInt32Bits(a) - BitConverter.SingleToInt32Bits(b)) <= 1, $"x={x}");
        }
        Assert.Equal(0f, DetMath.Cbrt(0f));
        Assert.Equal(-2.0, DetMath.Cbrt(-8.0), 15);
    }

    [Fact]
    public void OklabIsBitStable()
    {
        // Golden bits: must be identical on Windows and Linux (only + - * / are used).
        var lab = Oklab.FromRgb(200, 120, 40);
        var fp = (BitConverter.SingleToInt32Bits(lab.L), BitConverter.SingleToInt32Bits(lab.A), BitConverter.SingleToInt32Bits(lab.B));
        Assert.Equal((GoldenL, GoldenA, GoldenB), fp);
    }

    const int GoldenL = 1059422536, GoldenA = 1032263076, GoldenB = 1039114480;
}
