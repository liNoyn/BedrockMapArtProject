namespace BMAP.ColorEngine;

public enum ColorMetric
{
    /// <summary>Euclidean distance in Oklab (Björn Ottosson, 2020): perceptually uniform and fast. Default.</summary>
    Oklab,
    /// <summary>Plain Euclidean RGB distance. Fast.</summary>
    Rgb,
    /// <summary>Weighted RGB ("redmean"). Cheap and closer to perception than plain RGB.</summary>
    WeightedRgb,
    /// <summary>CIE76 ΔE in CIELAB.</summary>
    Lab,
    /// <summary>CIEDE2000 ΔE. Slowest.</summary>
    Ciede2000,
}

public readonly record struct Lab(double L, double A, double B);

/// <summary>Colour-space conversions and colour differences, implemented without dependencies (design 14).</summary>
public static class ColorMath
{
    private static readonly double[] SrgbToLinearLut = BuildLinearLut();

    private static double[] BuildLinearLut()
    {
        var lut = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            lut[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return lut;
    }

    public static Lab ToLab(double r, double g, double b)
    {
        double rl = Linear(r), gl = Linear(g), bl = Linear(b);
        // sRGB D65
        double x = (0.4124564 * rl + 0.3575761 * gl + 0.1804375 * bl) / 0.95047;
        double y = 0.2126729 * rl + 0.7151522 * gl + 0.0721750 * bl;
        double z = (0.0193339 * rl + 0.1191920 * gl + 0.9503041 * bl) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double Linear(double v)
    {
        if (v >= 0 && v <= 255 && v == Math.Floor(v)) return SrgbToLinearLut[(int)v];
        double c = Math.Clamp(v, 0, 255) / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;

    /// <summary>sRGB (0..255) to Oklab. Uses the same cached sRGB→linear table as <see cref="ToLab"/>.</summary>
    public static Lab ToOklab(double r, double g, double b)
    {
        double rl = Linear(r), gl = Linear(g), bl = Linear(b);
        double l = Math.Cbrt(0.4122214708 * rl + 0.5363325363 * gl + 0.0514459929 * bl);
        double m = Math.Cbrt(0.2119034982 * rl + 0.6806995451 * gl + 0.1073969566 * bl);
        double s = Math.Cbrt(0.0883024619 * rl + 0.2817188376 * gl + 0.6299787005 * bl);
        return new Lab(
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>sRGB byte value to linear light (0..1).</summary>
    public static double SrgbToLinear(byte v) => SrgbToLinearLut[v];

    /// <summary>Linear light (0..1) to an sRGB value (0..255, not rounded).</summary>
    public static double LinearToSrgb(double v)
    {
        v = Math.Clamp(v, 0, 1);
        return 255 * (v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);
    }

    public static double DeltaE76(Lab a, Lab b)
    {
        double dl = a.L - b.L, da = a.A - b.A, db = a.B - b.B;
        return dl * dl + da * da + db * db; // squared: only used for ranking
    }

    public static double WeightedRgbSq(double r1, double g1, double b1, double r2, double g2, double b2)
    {
        double rm = (r1 + r2) / 2;
        double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
    }

    /// <summary>CIEDE2000 colour difference (Sharma et al. 2005).</summary>
    public static double DeltaE2000(Lab l1, Lab l2)
    {
        const double kL = 1, kC = 1, kH = 1;
        double c1 = Math.Sqrt(l1.A * l1.A + l1.B * l1.B);
        double c2 = Math.Sqrt(l2.A * l2.A + l2.B * l2.B);
        double cBar = (c1 + c2) / 2;
        double cBar7 = Math.Pow(cBar, 7);
        double g = 0.5 * (1 - Math.Sqrt(cBar7 / (cBar7 + 6103515625.0))); // 25^7
        double a1p = (1 + g) * l1.A, a2p = (1 + g) * l2.A;
        double c1p = Math.Sqrt(a1p * a1p + l1.B * l1.B);
        double c2p = Math.Sqrt(a2p * a2p + l2.B * l2.B);
        double h1p = HueAngle(l1.B, a1p), h2p = HueAngle(l2.B, a2p);

        double dLp = l2.L - l1.L;
        double dCp = c2p - c1p;
        double dhp;
        if (c1p * c2p == 0) dhp = 0;
        else if (Math.Abs(h2p - h1p) <= 180) dhp = h2p - h1p;
        else if (h2p - h1p > 180) dhp = h2p - h1p - 360;
        else dhp = h2p - h1p + 360;
        double dHp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Deg2Rad(dhp / 2));

        double lBarP = (l1.L + l2.L) / 2;
        double cBarP = (c1p + c2p) / 2;
        double hBarP;
        if (c1p * c2p == 0) hBarP = h1p + h2p;
        else if (Math.Abs(h1p - h2p) <= 180) hBarP = (h1p + h2p) / 2;
        else if (h1p + h2p < 360) hBarP = (h1p + h2p + 360) / 2;
        else hBarP = (h1p + h2p - 360) / 2;

        double t = 1 - 0.17 * Math.Cos(Deg2Rad(hBarP - 30)) + 0.24 * Math.Cos(Deg2Rad(2 * hBarP))
                   + 0.32 * Math.Cos(Deg2Rad(3 * hBarP + 6)) - 0.20 * Math.Cos(Deg2Rad(4 * hBarP - 63));
        double dTheta = 30 * Math.Exp(-Math.Pow((hBarP - 275) / 25, 2));
        double cBarP7 = Math.Pow(cBarP, 7);
        double rc = 2 * Math.Sqrt(cBarP7 / (cBarP7 + 6103515625.0));
        double lMinus50Sq = (lBarP - 50) * (lBarP - 50);
        double sl = 1 + 0.015 * lMinus50Sq / Math.Sqrt(20 + lMinus50Sq);
        double sc = 1 + 0.045 * cBarP;
        double sh = 1 + 0.015 * cBarP * t;
        double rt = -Math.Sin(Deg2Rad(2 * dTheta)) * rc;

        double vl = dLp / (kL * sl), vc = dCp / (kC * sc), vh = dHp / (kH * sh);
        return Math.Sqrt(vl * vl + vc * vc + vh * vh + rt * vc * vh);
    }

    private static double HueAngle(double b, double a)
    {
        if (a == 0 && b == 0) return 0;
        double h = Math.Atan2(b, a) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180;
}
