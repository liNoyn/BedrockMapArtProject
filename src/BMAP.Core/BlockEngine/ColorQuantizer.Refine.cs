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

        // Perceptual per-pixel cost: squared Oklab distance to the source colour. Mixing a colour from very different
        // ones (white with strong blue, pink and yellow dots for a pale lavender) gets the average right but looks
        // rough up close; this term and the radius below keep mixes to colours near the source.
        var ck = candidates.Select(c => ColorMath.ToOklab(c.Color.R, c.Color.G, c.Color.B)).ToArray();
        var ok = new Lab[n];
        for (int i = 0; i < n; i++) ok[i] = ColorMath.ToOklab(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2]);
        float pw = (float)settings.PerceptualWeight;
        float PixelCost(int i, int c)
        {
            double dl = ck[c].L - ok[i].L, da = ck[c].A - ok[i].A, db = ck[c].B - ok[i].B;
            return pw * (float)(dl * dl + da * da + db * db);
        }

        // Per pixel only candidates near the source colour are tried: the closest ones within Radius (Oklab) of the
        // nearest, at least two so that mixing stays possible. The lists never change: with sameShade the shade of a
        // pixel stays what it was.
        int take = Math.Min(settings.Neighbours, nc);
        var near = new int[n * take];
        var nearCost = new float[n * take];
        var counts = new int[n];
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, () => (new int[nc], new float[nc], new int[take]), (y, _, buf) =>
        {
            var (order, dist, tried) = buf;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, cur = chosen[i];
                if (cur < 0) continue;
                int m = 0;
                for (int c = 0; c < nc; c++)
                {
                    if (sameShade && candidates[c].Shade != candidates[cur].Shade) continue;
                    double dl = ck[c].L - ok[i].L, da = ck[c].A - ok[i].A, db = ck[c].B - ok[i].B;
                    order[m] = c; dist[m] = (float)Math.Sqrt(dl * dl + da * da + db * db); m++;
                }
                int count = Math.Min(take, m);
                PartialSort(order, dist, m, count, tried);
                if (count > 2 && settings.Radius > 0)
                {
                    float limit = dist[0] + (float)settings.Radius;
                    int keep = 2;
                    while (keep < count && dist[keep] <= limit) keep++;
                    count = keep;
                }
                for (int t = 0; t < count; t++) { near[i * take + t] = tried[t]; nearCost[i * take + t] = PixelCost(i, tried[t]); }
                counts[i] = count;
            }
            return buf;
        }, _ => { });

        if (settings.PairLambda > 0)
            PairLists(image, w, h, candidates, chosen, sameShade, settings, ck, ok, take, near, nearCost, counts, PixelCost, ct);

        // Initial error field for the starting colours.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, c = chosen[i];
                if (c < 0) continue;
                float s = sw[i];
                Spread(x, y, s * (cv[c * 3] - ov[i * 3]), s * (cv[c * 3 + 1] - ov[i * 3 + 1]), s * (cv[c * 3 + 2] - ov[i * 3 + 2]));
            }

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
                    float curCost = PixelCost(i, cur);
                    int best = cur;
                    float bestDelta = -1e-9f;
                    for (int t = 0; t < count; t++)
                    {
                        int c = near[start + t];
                        if (c == cur) continue;
                        float ar = cv[c * 3] - qr, ag = cv[c * 3 + 1] - qg, ab = cv[c * 3 + 2] - qb;
                        float delta = 2 * sp * (ar * er + ag * eg + ab * eb) + cs * (ar * ar + ag * ag + ab * ab) + nearCost[start + t] - curCost;
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

    /// <summary>
    /// Replaces the per-pixel candidate lists by the best two-colour mix for the source colour: the pair whose blend
    /// (in linear light) comes closest to it, with a penalty for the contrast between the two (a pale lavender from
    /// white and ice, not from white, blue, pink and yellow dots). The list holds that pair and the nearest single
    /// colour, so areas are mixed from two colours only and look calm up close while keeping their hue. Pixels whose
    /// current colour is not on their list start from the list colour closest to it.
    /// </summary>
    private static void PairLists(RgbaImage image, int w, int h, List<Candidate> candidates, int[] chosen, bool sameShade,
        RefineSettings settings, Lab[] ck, Lab[] ok, int take, int[] near, float[] nearCost, int[] counts, Func<int, int, float> pixelCost,
        CancellationToken ct)
    {
        int nc = candidates.Count;
        var lin = new double[nc * 3];
        for (int c = 0; c < nc; c++)
        {
            lin[c * 3] = ColorMath.SrgbToLinear(candidates[c].Color.R);
            lin[c * 3 + 1] = ColorMath.SrgbToLinear(candidates[c].Color.G);
            lin[c * 3 + 2] = ColorMath.SrgbToLinear(candidates[c].Color.B);
        }
        static double D(Lab a, Lab b) => Math.Sqrt((a.L - b.L) * (a.L - b.L) + (a.A - b.A) * (a.A - b.A) + (a.B - b.B) * (a.B - b.B));
        // Contrast inside a mix: differences in hue and vividness (a, b) weighted by ChromaSpread against lightness.
        double kc = settings.ChromaSpread;
        double S(Lab a, Lab b) => Math.Sqrt((a.L - b.L) * (a.L - b.L) + kc * ((a.A - b.A) * (a.A - b.A) + (a.B - b.B) * (a.B - b.B)));
        double lambda = settings.PairLambda, mu = settings.PairQuad;
        // Penalty for the contrast inside a mix: linear, plus a quadratic part so that extreme mixes (white with
        // emerald for a pale green) lose against calm ones even when their average is closer.
        double Spread(double s) => lambda * s + mu * s * s;
        int pool = Math.Min(settings.PairPool, nc);

        // Plans per colour bucket (5 bits per channel) and shade group, from the bucket centre: deterministic.
        var plans = new System.Collections.Concurrent.ConcurrentDictionary<long, int[]>();
        int[] Plan(byte r, byte g, byte b, int shadeFilter)
        {
            long key = ((long)(shadeFilter + 1) << 15) | ((long)(r >> 3) << 10) | ((long)(g >> 3) << 5) | (long)(b >> 3);
            return plans.GetOrAdd(key, _ =>
            {
                byte cr = (byte)((r & ~7) + 4), cg = (byte)((g & ~7) + 4), cb = (byte)((b & ~7) + 4);
                double tr = ColorMath.SrgbToLinear(cr), tg = ColorMath.SrgbToLinear(cg), tb = ColorMath.SrgbToLinear(cb);
                var target = ColorMath.ToOklab(cr, cg, cb);
                // Pairs: one of the colours closest to the target with any allowed colour. The partner can be far away:
                // a pale pink needs white and pink wool, which is nowhere near it.
                var allowed = Enumerable.Range(0, nc).Where(c => shadeFilter < 0 || (int)candidates[c].Shade == shadeFilter).ToArray();
                var close = allowed.OrderBy(c => D(ck[c], target)).ThenBy(c => c).Take(Math.Min(settings.PairNear, allowed.Length)).ToArray();
                int nearest = close[0];
                // Triples: among the closest in linear light (where mixing happens) and the closest perceptually.
                var ids = close.Concat(allowed.OrderBy(c => Sq(lin[c * 3] - tr) + Sq(lin[c * 3 + 1] - tg) + Sq(lin[c * 3 + 2] - tb)).ThenBy(c => c).Take(pool))
                    .Distinct().ToArray();
                // Expected per-pixel distance of a mix (PlanPixel): mixing extreme colours to reach a colour the palette
                // lacks (pure red and yellow for a salmon orange) gets the average right, but every pixel is far off.
                double beta = settings.PlanPixel, gamma = settings.PlanHue;
                // Off-hue part of a colour: its distance (in the a/b plane) from the line through the target's hue.
                // White and pink for a pale pink, or white and ice for a pale lavender, lie on that line; red and yellow
                // for a salmon orange lie on either side of it and show as loud dots.
                double tA = target.A, tB = target.B, tC = Math.Sqrt(tA * tA + tB * tB);
                double Off(int c) => tC > 0.02 ? Math.Abs(ck[c].A * tB - ck[c].B * tA) / tC : Math.Sqrt(ck[c].A * ck[c].A + ck[c].B * ck[c].B);
                double best = D(ck[nearest], target) * (1 + beta) + gamma * Off(nearest);
                int ba = nearest, bb = -1;
                var partners = new List<(double Cost, int Id)>();
                double partnerCut = double.MaxValue;
                for (int i = 0; i < close.Length; i++)
                    foreach (int c in allowed)
                    {
                        int a = close[i];
                        if (c == a) continue;
                        int ci = Array.IndexOf(close, c);
                        if (ci >= 0 && ci < i) continue; // pair already seen
                        double dx = lin[c * 3] - lin[a * 3], dy = lin[c * 3 + 1] - lin[a * 3 + 1], dz = lin[c * 3 + 2] - lin[a * 3 + 2];
                        double len = dx * dx + dy * dy + dz * dz;
                        if (len <= 0) continue;
                        double t = Math.Clamp(((tr - lin[a * 3]) * dx + (tg - lin[a * 3 + 1]) * dy + (tb - lin[a * 3 + 2]) * dz) / len, 0, 1);
                        if (t <= 0 || t >= 1) continue;
                        var mix = ColorMath.LinearToOklab(lin[a * 3] + t * dx, lin[a * 3 + 1] + t * dy, lin[a * 3 + 2] + t * dz);
                        double cost = D(mix, target) + Spread(S(ck[a], ck[c]) * 2 * Math.Sqrt(t * (1 - t)))
                            + beta * ((1 - t) * D(ck[a], target) + t * D(ck[c], target)) + gamma * ((1 - t) * Off(a) + t * Off(c));
                        if (cost < best) { best = cost; ba = a; bb = c; }
                        if (cost < partnerCut) { partners.Add((cost, c)); if (partners.Count > 24) { partners.Sort(); partners.RemoveRange(12, partners.Count - 12); partnerCut = partners[^1].Cost; } }
                    }
                // Three-colour mixes reach colours inside a triangle of map colours, which no pair can. Weights by least
                // squares in linear light (sum 1, all positive); the contrast term generalises the pair's:
                // 2·sqrt(Σ wi·wj·dij²).
                int t1 = -1, t2 = -1, t3 = -1;
                // Triples also consider the best pair partners (pink wool for a pale pink lies far from it).
                var good = partners.OrderBy(p => p.Cost).Select(p => p.Id).Distinct().Take(4).ToArray();
                ids = [.. close.Take(Math.Max(0, settings.TriplePool - good.Length)), .. good];
                ids = ids.Distinct().ToArray();
                int tri = Math.Min(settings.TriplePool, ids.Length);
                for (int i = 0; i < tri; i++)
                    for (int j = i + 1; j < tri; j++)
                        for (int k = j + 1; k < tri; k++)
                        {
                            int a = ids[i], b2 = ids[j], c = ids[k];
                            // Solve target = A + u(B−A) + v(C−A) in the least-squares sense.
                            double e1x = lin[b2 * 3] - lin[a * 3], e1y = lin[b2 * 3 + 1] - lin[a * 3 + 1], e1z = lin[b2 * 3 + 2] - lin[a * 3 + 2];
                            double e2x = lin[c * 3] - lin[a * 3], e2y = lin[c * 3 + 1] - lin[a * 3 + 1], e2z = lin[c * 3 + 2] - lin[a * 3 + 2];
                            double px = tr - lin[a * 3], py = tg - lin[a * 3 + 1], pz = tb - lin[a * 3 + 2];
                            double m11 = e1x * e1x + e1y * e1y + e1z * e1z, m12 = e1x * e2x + e1y * e2y + e1z * e2z, m22 = e2x * e2x + e2y * e2y + e2z * e2z;
                            double r1 = e1x * px + e1y * py + e1z * pz, r2 = e2x * px + e2y * py + e2z * pz;
                            double det = m11 * m22 - m12 * m12;
                            if (Math.Abs(det) < 1e-12) continue;
                            double u = (r1 * m22 - r2 * m12) / det, v = (m11 * r2 - m12 * r1) / det, w0 = 1 - u - v;
                            if (u <= 0.02 || v <= 0.02 || w0 <= 0.02) continue; // edges are covered by the pairs
                            var mix = ColorMath.LinearToOklab(lin[a * 3] + u * e1x + v * e2x, lin[a * 3 + 1] + u * e1y + v * e2y, lin[a * 3 + 2] + u * e1z + v * e2z);
                            double dab = S(ck[a], ck[b2]), dac = S(ck[a], ck[c]), dbc = S(ck[b2], ck[c]);
                            double spread = 2 * Math.Sqrt(w0 * u * dab * dab + w0 * v * dac * dac + u * v * dbc * dbc);
                            double cost = D(mix, target) + Spread(spread)
                                + beta * (w0 * D(ck[a], target) + u * D(ck[b2], target) + v * D(ck[c], target))
                                + gamma * (w0 * Off(a) + u * Off(b2) + v * Off(c));
                            if (cost < best) { best = cost; t1 = a; t2 = b2; t3 = c; }
                        }
                if (t1 >= 0)
                    return Array.IndexOf(new[] { t1, t2, t3 }, nearest) >= 0 ? [t1, t2, t3] : [t1, t2, t3, nearest];
                return bb < 0 ? (ba == nearest ? [nearest] : [ba, nearest])
                    : nearest == ba || nearest == bb ? [ba, bb] : [ba, bb, nearest];
            });
        }

        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, cur = chosen[i];
                if (cur < 0) continue;
                var plan = Plan(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2], sameShade ? (int)candidates[cur].Shade : -1);
                int count = Math.Min(plan.Length, take);
                for (int t = 0; t < count; t++) { near[i * take + t] = plan[t]; nearCost[i * take + t] = pixelCost(i, plan[t]); }
                counts[i] = count;
                if (Array.IndexOf(plan, cur) < 0)
                    chosen[i] = plan.OrderBy(c => Sq(lin[c * 3] - lin[cur * 3]) + Sq(lin[c * 3 + 1] - lin[cur * 3 + 1]) + Sq(lin[c * 3 + 2] - lin[cur * 3 + 2])).First();
            }
        });
    }

    private static double Sq(double v) => v * v;

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
    /// <summary>Weight of the per-pixel Oklab error (calm, true-hue pixels up close).</summary>
    public double PerceptualWeight { get; init; }
    /// <summary>Only candidates within this Oklab distance of the nearest one are mixed (0 = no limit).</summary>
    public double Radius { get; init; }
    /// <summary>Two-colour mixing: contrast penalty of a pair (0 = off, use the nearest-candidate lists).</summary>
    public double PairLambda { get; init; }
    /// <summary>Two-colour mixing: how many candidates (closest in linear light) are paired.</summary>
    public int PairPool { get; init; } = 40;
    /// <summary>Pairs are formed from this many colours closest to the target (with any partner).</summary>
    public int PairNear { get; init; } = 12;
    /// <summary>Three-colour mixes among the closest this many candidates (0 = pairs only).</summary>
    public int TriplePool { get; init; }
    /// <summary>Weight of hue / vividness differences against lightness differences in the mix contrast.</summary>
    public double ChromaSpread { get; init; } = 1;
    /// <summary>Weight of the expected per-pixel distance of a mix when choosing it.</summary>
    public double PlanPixel { get; init; }
    /// <summary>Weight of the off-hue part of the colours in a mix when choosing it.</summary>
    public double PlanHue { get; init; }
    /// <summary>Quadratic part of the mix contrast penalty.</summary>
    public double PairQuad { get; init; }
}
