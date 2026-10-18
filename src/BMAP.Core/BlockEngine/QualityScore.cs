using BMAP.ColorEngine;
using BMAP.Imaging;

namespace BMAP.BlockEngine;

/// <summary>
/// How close a map image is to its source, at three viewing distances (all CIEDE2000, lower is better):
/// <list type="bullet">
/// <item>Pixel: every pixel on its own (sharpness, noise).</item>
/// <item>Near: blurred by σ 0.8 px in linear light — the preview or a map held in the hand, where single pixels are
/// still almost visible and coloured speckle shows.</item>
/// <item>Far: blurred by σ 2 px — a map art seen from a distance, where only the mixed colour counts.</item>
/// </list>
/// </summary>
public readonly record struct QualityScore(double Pixel, double Near, double Far)
{
    /// <summary>One number for comparisons: speckle seen up close counts most, as people judge previews that way.</summary>
    public double Overall => 0.25 * Pixel + 0.5 * Near + 0.25 * Far;

    public static QualityScore Measure(RgbaImage source, RgbaImage map)
    {
        int w = source.Width, h = source.Height;
        var a = ToLinear(source);
        var b = ToLinear(map);
        return new QualityScore(Mean(a, b, map, w, h), Mean(Blur(a, map, w, h, 0.8), Blur(b, map, w, h, 0.8), map, w, h),
            Mean(Blur(a, map, w, h, 2.0), Blur(b, map, w, h, 2.0), map, w, h));
    }

    private static double[] ToLinear(RgbaImage img)
    {
        var lin = new double[img.Width * img.Height * 3];
        for (int i = 0; i < img.Width * img.Height; i++)
            for (int c = 0; c < 3; c++) lin[i * 3 + c] = ColorMath.SrgbToLinear(img.Pixels[i * 4 + c]);
        return lin;
    }

    private static double Mean(double[] a, double[] b, RgbaImage map, int w, int h)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (map.Pixels[i * 4 + 3] == 0) continue;
            var la = ColorMath.ToLab(ColorMath.LinearToSrgb(a[i * 3]), ColorMath.LinearToSrgb(a[i * 3 + 1]), ColorMath.LinearToSrgb(a[i * 3 + 2]));
            var lb = ColorMath.ToLab(ColorMath.LinearToSrgb(b[i * 3]), ColorMath.LinearToSrgb(b[i * 3 + 1]), ColorMath.LinearToSrgb(b[i * 3 + 2]));
            sum += ColorMath.DeltaE2000(la, lb);
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Separable Gaussian blur of linear values; transparent pixels do not contribute.</summary>
    private static double[] Blur(double[] src, RgbaImage mask, int w, int h, double sigma)
    {
        int r = (int)Math.Ceiling(3 * sigma);
        var k = new double[2 * r + 1];
        for (int d = -r; d <= r; d++) k[d + r] = Math.Exp(-d * d / (2 * sigma * sigma));
        var tmp = new double[src.Length];
        var dst = new double[src.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            var from = pass == 0 ? src : tmp;
            var to = pass == 0 ? tmp : dst;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double s0 = 0, s1 = 0, s2 = 0, ws = 0;
                    for (int d = -r; d <= r; d++)
                    {
                        int sx = pass == 0 ? x + d : x, sy = pass == 0 ? y : y + d;
                        if (sx < 0 || sy < 0 || sx >= w || sy >= h || mask.Pixels[(sy * w + sx) * 4 + 3] == 0) continue;
                        int j = (sy * w + sx) * 3;
                        double kw = k[d + r];
                        s0 += from[j] * kw; s1 += from[j + 1] * kw; s2 += from[j + 2] * kw; ws += kw;
                    }
                    int i = (y * w + x) * 3;
                    if (ws > 0) { to[i] = s0 / ws; to[i + 1] = s1 / ws; to[i + 2] = s2 / ws; }
                }
        }
        return dst;
    }
}
