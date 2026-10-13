namespace Vecto.Core;

/// <summary>
/// Platform-independent replacements for Math.Cbrt / Math.Pow on the colour paths.
/// The CRT (Windows) and glibc (Linux) implementations of those functions are not
/// correctly rounded and differ by an ulp now and then, which flipped borderline
/// segmentation decisions between platforms. These use only + - * / on doubles
/// (exact IEEE) with a bit-trick seed and a fixed number of Newton steps, so every
/// platform computes the same bits.
/// </summary>
public static class DetMath
{
    const long OneBits = 0x3FF0000000000000L;

    /// <summary>x^(1/n) for x ≥ 0, n ≥ 2.</summary>
    public static double Root(double x, int n)
    {
        if (!(x > 0)) return 0;
        // Seed: divide the exponent (and mantissa, linearly) by n in the bit pattern.
        long bits = BitConverter.DoubleToInt64Bits(x);
        double y = BitConverter.Int64BitsToDouble((bits - OneBits) / n + OneBits);
        for (int it = 0; it < 8; it++)
        {
            double p = y;
            for (int k = 2; k < n; k++) p *= y;     // y^(n-1)
            y = ((n - 1) * y + x / p) / n;
        }
        return y;
    }

    public static double Cbrt(double x) => x < 0 ? -Root(-x, 3) : Root(x, 3);

    public static float Cbrt(float x) => (float)Cbrt((double)x);
}
