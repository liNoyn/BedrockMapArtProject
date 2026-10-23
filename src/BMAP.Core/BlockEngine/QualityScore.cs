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
        if (source.Width != map.Width || source.Height != map.Height)
            throw new ArgumentException("Source and map dimensions must match.", nameof(map));
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
        var valid = Enumerable.Range(0, w * h).Select(i => mask.Pixels[i * 4 + 3] != 0).ToArray();
        return DitherFilter.Blur(src, valid, w, h, sigma);
    }
}
