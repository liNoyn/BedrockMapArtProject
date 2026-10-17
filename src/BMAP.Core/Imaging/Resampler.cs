namespace BMAP.Imaging;

/// <summary>
/// Separable resampling in premultiplied linear-ish (sRGB value) float space.
/// Premultiplying avoids dark fringes around transparent areas.
/// </summary>
public static class Resampler
{
    /// <summary>
    /// Renders the source rectangle (srcX, srcY, srcW, srcH — may extend outside the image, in source pixels)
    /// into a destination of dstW x dstH. Pixels outside the source are transparent.
    /// </summary>
    public static RgbaImage Resample(RgbaImage src, double srcX, double srcY, double srcW, double srcH,
        int dstW, int dstH, Interpolation mode, CancellationToken ct = default, bool linearLight = false)
    {
        if (mode == Interpolation.Nearest)
            return Nearest(src, srcX, srcY, srcW, srcH, dstW, dstH);

        // Gamma-correct resampling works on linear light (0..255 scale) and converts back afterwards.
        float Channel(byte v) => linearLight ? (float)(ColorEngine.ColorMath.SrgbToLinear(v) * 255) : v;
        byte Back(float v) => linearLight ? ClampByte(ColorEngine.ColorMath.LinearToSrgb(v / 255.0)) : ClampByte(v);

        // Premultiplied float planes.
        int w = src.Width, h = src.Height;
        var plane = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            float a = src.Pixels[i * 4 + 3] / 255f;
            plane[i * 4] = Channel(src.Pixels[i * 4]) * a;
            plane[i * 4 + 1] = Channel(src.Pixels[i * 4 + 1]) * a;
            plane[i * 4 + 2] = Channel(src.Pixels[i * 4 + 2]) * a;
            plane[i * 4 + 3] = a * 255f;
        }

        // Horizontal pass: h rows x dstW columns.
        var hWeights = BuildWeights(w, srcX, srcW, dstW, mode);
        var tmp = new float[dstW * h * 4];
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < dstW; x++)
            {
                var (start, weights) = hWeights[x];
                float r = 0, g = 0, b = 0, a = 0;
                for (int k = 0; k < weights.Length; k++)
                {
                    int sx = Math.Clamp(start + k, 0, w - 1);
                    int si = (y * w + sx) * 4;
                    float wt = weights[k];
                    r += plane[si] * wt; g += plane[si + 1] * wt; b += plane[si + 2] * wt; a += plane[si + 3] * wt;
                }
                int ti = (y * dstW + x) * 4;
                tmp[ti] = r; tmp[ti + 1] = g; tmp[ti + 2] = b; tmp[ti + 3] = a;
            }
        });

        var vWeights = BuildWeights(h, srcY, srcH, dstH, mode);
        var dst = new RgbaImage(dstW, dstH);
        Parallel.For(0, dstH, new ParallelOptions { CancellationToken = ct }, y =>
        {
            var (start, weights) = vWeights[y];
            double cy = srcY + (y + 0.5) * srcH / dstH;
            bool rowOutside = cy < 0 || cy >= h;
            for (int x = 0; x < dstW; x++)
            {
                double cx = srcX + (x + 0.5) * srcW / dstW;
                if (rowOutside || cx < 0 || cx >= w) continue; // outside the source: stays transparent
                float r = 0, g = 0, b = 0, a = 0;
                for (int k = 0; k < weights.Length; k++)
                {
                    int sy = Math.Clamp(start + k, 0, h - 1);
                    int si = (sy * dstW + x) * 4;
                    float wt = weights[k];
                    r += tmp[si] * wt; g += tmp[si + 1] * wt; b += tmp[si + 2] * wt; a += tmp[si + 3] * wt;
                }
                int di = (y * dstW + x) * 4;
                if (a <= 0.5f)
                {
                    dst.Pixels[di] = dst.Pixels[di + 1] = dst.Pixels[di + 2] = dst.Pixels[di + 3] = 0;
                    continue;
                }
                float inv = 255f / a;
                dst.Pixels[di] = Back(r * inv);
                dst.Pixels[di + 1] = Back(g * inv);
                dst.Pixels[di + 2] = Back(b * inv);
                dst.Pixels[di + 3] = ClampByte(a);
            }
        });
        return dst;
    }

    private static RgbaImage Nearest(RgbaImage src, double srcX, double srcY, double srcW, double srcH, int dstW, int dstH)
    {
        var dst = new RgbaImage(dstW, dstH);
        for (int y = 0; y < dstH; y++)
        {
            int sy = (int)Math.Floor(srcY + (y + 0.5) * srcH / dstH);
            if (sy < 0 || sy >= src.Height) continue;
            for (int x = 0; x < dstW; x++)
            {
                int sx = (int)Math.Floor(srcX + (x + 0.5) * srcW / dstW);
                if (sx < 0 || sx >= src.Width) continue;
                dst[x, y] = src[sx, sy];
            }
        }
        return dst;
    }

    private static (int start, float[] weights)[] BuildWeights(int srcSize, double srcOffset, double srcSpan, int dstSize, Interpolation mode)
    {
        double scale = srcSpan / dstSize; // source pixels per destination pixel
        double support = mode == Interpolation.Lanczos3 ? 3.0 : 1.0;
        double filterScale = Math.Max(1.0, scale); // widen the kernel when downscaling (anti-aliasing)
        double radius = support * filterScale;
        var result = new (int, float[])[dstSize];
        for (int i = 0; i < dstSize; i++)
        {
            double center = srcOffset + (i + 0.5) * scale - 0.5;
            int start = (int)Math.Floor(center - radius) + 1;
            int end = (int)Math.Ceiling(center + radius);
            var weights = new float[Math.Max(1, end - start)];
            double sum = 0;
            for (int k = 0; k < weights.Length; k++)
            {
                double d = (start + k - center) / filterScale;
                double wt = mode == Interpolation.Lanczos3 ? Lanczos(d) : Math.Max(0, 1 - Math.Abs(d));
                weights[k] = (float)wt;
                sum += wt;
            }
            if (sum > 0)
                for (int k = 0; k < weights.Length; k++) weights[k] = (float)(weights[k] / sum);
            result[i] = (start, weights);
        }
        return result;
    }

    private static double Lanczos(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-8) return 1;
        if (x >= 3) return 0;
        double px = Math.PI * x;
        return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
    }

    internal static byte ClampByte(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
}
