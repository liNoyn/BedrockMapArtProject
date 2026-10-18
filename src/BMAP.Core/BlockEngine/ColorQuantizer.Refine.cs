using BMAP.ColorEngine;
using BMAP.Imaging;

namespace BMAP.BlockEngine;

/// <summary>Working space of the optimiser (how errors are added up and weighted).</summary>
public enum RefineSpace
{
    /// <summary>Linear light: physically right for mixing, but weighs errors in bright tones too much.</summary>
    Linear,
    /// <summary>sRGB values: roughly perceptual weighting.</summary>
    Srgb,
    /// <summary>Oklab: perceptual weighting.</summary>
    Oklab,
}

public static partial class ColorQuantizer
{
    /// <summary>
    /// Direct optimisation of the map colours (in the spirit of direct binary search halftoning): pixels are changed
    /// one at a time to whichever candidate lowers the total error the most, until nothing improves. The error is
    /// measured at several scales at once: per pixel (sharpness, no needless noise) and blurred by about 1 and 2
    /// pixels (how mixed areas look from a distance). Error diffusion and pattern dithering only look one way and
    /// one scale at a time; this sees the whole neighbourhood, so it keeps flat areas calm and edges sharp while
    /// still mixing colours the palette does not have.
    /// </summary>
    /// <param name="chosen">Candidate per pixel (-1 = empty); changed in place.</param>
    /// <param name="sameShade">Only swap between candidates of the current shade (keeps the staircase unchanged).</param>
    internal static void Refine(RgbaImage image, int w, int h, List<Candidate> candidates, int[] chosen, bool sameShade,
        RefineSettings settings, CancellationToken ct)
    {
        int n = w * h, nc = candidates.Count;
        if (nc < 2) return;

        // Vectors in the working space.
        float[] Vec(double r, double g, double b)
        {
            switch (settings.Space)
            {
                case RefineSpace.Linear:
                    return [(float)ColorMath.SrgbToLinear((byte)r), (float)ColorMath.SrgbToLinear((byte)g), (float)ColorMath.SrgbToLinear((byte)b)];
                case RefineSpace.Srgb:
                    return [(float)(r / 255), (float)(g / 255), (float)(b / 255)];
                default:
                    var l = ColorMath.ToOklab(r, g, b);
                    return [(float)l.L, (float)l.A, (float)l.B];
            }
        }
        var cv = new float[nc * 3];
        for (int c = 0; c < nc; c++)
        {
            var v = Vec(candidates[c].Color.R, candidates[c].Color.G, candidates[c].Color.B);
            cv[c * 3] = v[0]; cv[c * 3 + 1] = v[1]; cv[c * 3 + 2] = v[2];
        }
        var ov = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            var v = Vec(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2]);
            ov[i * 3] = v[0]; ov[i * 3 + 1] = v[1]; ov[i * 3 + 2] = v[2];
        }
        // Per-pixel weight: the eye sees the same difference in light better in dark tones (lightness ~ Y^(1/3)), so
        // errors are scaled by (Y / 0.5)^(-p) with Y the source luminance (linear space only).
        var sw = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (settings.Space != RefineSpace.Linear || settings.DarkWeight <= 0) { sw[i] = 1; continue; }
            double y = 0.2126 * ColorMath.SrgbToLinear(image.Pixels[i * 4]) + 0.7152 * ColorMath.SrgbToLinear(image.Pixels[i * 4 + 1])
                + 0.0722 * ColorMath.SrgbToLinear(image.Pixels[i * 4 + 2]);
            sw[i] = (float)Math.Pow(Math.Max(y, 0.02) / 0.5, -settings.DarkWeight);
        }

        // Combined kernel C = w0·δ + Σ ws·(Gs ⊛ Gs); Gs ⊛ Gs is a Gaussian with σ√2.
        int R = (int)Math.Ceiling(3 * settings.Sigma2 * Math.Sqrt(2));
        int K = 2 * R + 1;
        var kernel = new float[K * K];
        void AddGaussian(double sigma, double weight)
        {
            if (weight <= 0 || sigma <= 0) return;
            double s2 = 2 * sigma * sigma; // variance of the autocorrelation
            var g = new double[K * K];
            for (int dy = -R; dy <= R; dy++)
                for (int dx = -R; dx <= R; dx++)
                    g[(dy + R) * K + dx + R] = Math.Exp(-(dx * dx + dy * dy) / (2 * s2));
            // Normalise like the autocorrelation of a unit-sum Gaussian: its centre value is Σ g², i.e. ≈ 1/(4πσ²).
            double centre = 1 / (4 * Math.PI * sigma * sigma);
            for (int k = 0; k < g.Length; k++) kernel[k] += (float)(weight * g[k] * centre);
        }
        kernel[R * K + R] += (float)settings.PixelWeight;
        AddGaussian(settings.Sigma1, settings.Weight1);
        AddGaussian(settings.Sigma2, settings.Weight2);
        float c0 = kernel[R * K + R];

        // ce = C * e, with e = chosen − original.
        var ce = new float[n * 3];
        void Spread(int x, int y, float er, float eg, float eb)
        {
            int y0 = Math.Max(0, y - R), y1 = Math.Min(h - 1, y + R), x0 = Math.Max(0, x - R), x1 = Math.Min(w - 1, x + R);
            for (int yy = y0; yy <= y1; yy++)
            {
                int krow = (yy - y + R) * K - x + R;
                for (int xx = x0; xx <= x1; xx++)
                {
                    float k = kernel[krow + xx];
                    int j = (yy * w + xx) * 3;
                    ce[j] += k * er; ce[j + 1] += k * eg; ce[j + 2] += k * eb;
                }
            }
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, c = chosen[i];
                if (c < 0) continue;
                float s = sw[i];
                Spread(x, y, s * (cv[c * 3] - ov[i * 3]), s * (cv[c * 3 + 1] - ov[i * 3 + 1]), s * (cv[c * 3 + 2] - ov[i * 3 + 2]));
            }

        // Per pixel only the candidates closest to the source colour are tried (keeps mixes local and fast). The
        // lists never change: with sameShade the shade of a pixel stays what it was.
        int take = Math.Min(settings.Neighbours, nc);
        var near = new int[n * take];
        var counts = new int[n];
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, () => (new int[nc], new float[nc], new int[take]), (y, _, buf) =>
        {
            var (order, dist, tried) = buf;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, cur = chosen[i];
                if (cur < 0) continue;
                float or = ov[i * 3], og = ov[i * 3 + 1], ob = ov[i * 3 + 2];
                int m = 0;
                for (int c = 0; c < nc; c++)
                {
                    if (sameShade && candidates[c].Shade != candidates[cur].Shade) continue;
                    float dr = cv[c * 3] - or, dg = cv[c * 3 + 1] - og, db = cv[c * 3 + 2] - ob;
                    order[m] = c; dist[m] = dr * dr + dg * dg + db * db; m++;
                }
                int count = Math.Min(take, m);
                PartialSort(order, dist, m, count, tried);
                Array.Copy(tried, 0, near, i * take, count);
                counts[i] = count;
            }
            return buf;
        }, _ => { });

        for (int pass = 0; pass < settings.MaxPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();
            int changes = 0;
            for (int y = 0; y < h; y++)
            {
                bool reverse = (y & 1) == 1;
                for (int s = 0; s < w; s++)
                {
                    int x = reverse ? w - 1 - s : s, i = y * w + x, cur = chosen[i];
                    if (cur < 0) continue;
                    int count = counts[i], start = i * take;
                    float qr = cv[cur * 3], qg = cv[cur * 3 + 1], qb = cv[cur * 3 + 2], sp = sw[i], cs = c0 * sp * sp;
                    float er = ce[i * 3], eg = ce[i * 3 + 1], eb = ce[i * 3 + 2];
                    int best = cur;
                    float bestDelta = -1e-9f;
                    for (int t = 0; t < count; t++)
                    {
                        int c = near[start + t];
                        if (c == cur) continue;
                        float ar = cv[c * 3] - qr, ag = cv[c * 3 + 1] - qg, ab = cv[c * 3 + 2] - qb;
                        float delta = 2 * sp * (ar * er + ag * eg + ab * eb) + cs * (ar * ar + ag * ag + ab * ab);
                        if (delta < bestDelta) { bestDelta = delta; best = c; }
                    }
                    if (best == cur) continue;
                    Spread(x, y, sp * (cv[best * 3] - qr), sp * (cv[best * 3 + 1] - qg), sp * (cv[best * 3 + 2] - qb));
                    chosen[i] = best;
                    changes++;
                }
            }
            if (changes <= n / 1000) break;
        }
    }

    /// <summary>The <paramref name="count"/> smallest of the first <paramref name="m"/> items, into <paramref name="result"/>.</summary>
    private static void PartialSort(int[] items, float[] keys, int m, int count, int[] result)
    {
        for (int t = 0; t < count; t++)
        {
            int bi = t;
            for (int j = t + 1; j < m; j++) if (keys[j] < keys[bi]) bi = j;
            (items[t], items[bi]) = (items[bi], items[t]);
            (keys[t], keys[bi]) = (keys[bi], keys[t]);
            result[t] = items[t];
        }
    }
}

/// <summary>Weights of the optimiser's error measure.</summary>
public sealed record RefineSettings
{
    /// <summary>Linear light measured best (docs/benchmarks.md).</summary>
    public RefineSpace Space { get; init; } = RefineSpace.Linear;
    /// <summary>Weight of the per-pixel error (higher = sharper, less mixing).</summary>
    public double PixelWeight { get; init; } = 0.1;
    public double Sigma1 { get; init; } = 1.0;
    public double Weight1 { get; init; } = 1.0;
    public double Sigma2 { get; init; } = 2.0;
    public double Weight2 { get; init; } = 1.0;
    /// <summary>Candidates tried per pixel (the closest to the source colour).</summary>
    public int Neighbours { get; init; } = 16;
    public int MaxPasses { get; init; } = 12;
    /// <summary>Linear space: exponent of the dark-tone weighting (0 = none, 2/3 = lightness slope).</summary>
    public double DarkWeight { get; init; } = 0.5;
}
