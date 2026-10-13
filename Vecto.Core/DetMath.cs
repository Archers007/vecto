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

    // ---- Trigonometry (curve stage). Same idea: only + - * / and Sqrt (exact IEEE),
    // fixed range reduction and fixed polynomial lengths, so the bits match everywhere.

    const double PiO2Hi = 1.57079632679489655800e+00;   // pi/2 split for Cody-Waite reduction
    const double PiO2Lo = 6.12323399573676603587e-17;

    /// <summary>Reduces x to r in [-pi/4, pi/4] with x = r + q*pi/2; returns q mod 4.</summary>
    static int Reduce(double x, out double r)
    {
        double q = Math.Round(x * (2 / Math.PI));
        r = (x - q * PiO2Hi) - q * PiO2Lo;
        return (int)((long)q & 3);
    }

    // Taylor series on |r| <= pi/4; terms to r^19 / r^20 put the truncation below 1e-20.
    static double SinPoly(double r)
    {
        double r2 = r * r, t = 0;
        for (int n = 19; n >= 3; n -= 2) t = (t + 1) * -r2 / (n * (n - 1));
        return r + r * t;
    }

    static double CosPoly(double r)
    {
        double r2 = r * r, t = 0;
        for (int n = 20; n >= 2; n -= 2) t = (t + 1) * -r2 / (n * (n - 1));
        return 1 + t;
    }

    public static double Sin(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return double.NaN;
        return Reduce(x, out double r) switch
        {
            0 => SinPoly(r), 1 => CosPoly(r), 2 => -SinPoly(r), _ => -CosPoly(r),
        };
    }

    public static double Cos(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return double.NaN;
        return Reduce(x, out double r) switch
        {
            0 => CosPoly(r), 1 => -SinPoly(r), 2 => -CosPoly(r), _ => SinPoly(r),
        };
    }

    public static double Tan(double x) => Sin(x) / Cos(x);

    public static double Atan(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        bool neg = x < 0; if (neg) x = -x;
        bool inv = x > 1; if (inv) x = 1 / x;
        // Two half-angle steps: atan(x) = 2 atan(x / (1 + sqrt(1 + x^2))) -> |x| <= tan(pi/16).
        x = x / (1 + Math.Sqrt(1 + x * x));
        x = x / (1 + Math.Sqrt(1 + x * x));
        double x2 = x * x, s = 0;
        for (int n = 31; n >= 1; n -= 2) s = s * -x2 + 1.0 / n;          // 1 - x2/3 + x4/5 - ...
        double a = 4 * (x * s);
        if (inv) a = Math.PI / 2 - a;
        return neg ? -a : a;
    }

    public static double Atan2(double y, double x)
    {
        if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
        if (x == 0) return y > 0 ? Math.PI / 2 : y < 0 ? -Math.PI / 2 : Math.Atan2(y, x);  // exact constants
        double a = Atan(y / x);
        if (x > 0) return a;
        return (y < 0 || (y == 0 && BitConverter.DoubleToInt64Bits(y) < 0)) ? a - Math.PI : a + Math.PI;
    }

    /// <summary>acos for x in [-1, 1] (clamped).</summary>
    public static double Acos(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        x = Math.Clamp(x, -1, 1);
        return Atan2(Math.Sqrt((1 - x) * (1 + x)), x);
    }
}
