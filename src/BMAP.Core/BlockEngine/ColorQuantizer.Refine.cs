using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    /// <param name="frozen">Pixels that keep their colour and contribute zero residual, avoiding compensation rims.</param>
    internal static void Refine(RgbaImage image, int w, int h, List<Candidate> candidates, int[] chosen, bool sameShade,
        RefineSettings settings, CancellationToken ct, bool[]? frozen = null)
    {
        int n = w * h, nc = candidates.Count;
        if (nc < 2) return;
        if (settings.Neighbours < 1 || settings.PairNear < 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "At least one candidate is required.");

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
        // Colour (chroma) errors count ChromaWeight times as much as grey-axis errors: every vector is scaled by √k across
        // the grey axis (1,1,1), so |e|² becomes |e along grey|² + k·|e across grey|². In linear light, errors in the
        // lightness of pale colours are numerically large, so without this a light pink was mixed with too much white:
        // lightness right, a third of its colour gone.
        if (settings.ChromaWeight != 1 && settings.Space != RefineSpace.Oklab)
        {
            float s = (float)Math.Sqrt(Math.Max(0, settings.ChromaWeight));
            void Scale(float[] a, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    float m = (a[i * 3] + a[i * 3 + 1] + a[i * 3 + 2]) / 3;
                    a[i * 3] = m + s * (a[i * 3] - m); a[i * 3 + 1] = m + s * (a[i * 3 + 1] - m); a[i * 3 + 2] = m + s * (a[i * 3 + 2] - m);
                }
            }
            Scale(cv, nc);
            Scale(ov, n);
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

        var model = new DitherKernel(settings);
        int R = model.Radius, K = model.Size;
        var kernel = model.Values;
        float c0 = model.Centre;

        // ce = C * e, with e = chosen − original. Spread is the hot loop: the kernel is stored with each value three times
        // (one per channel, as ce is interleaved) so each kernel row is one contiguous run that is added with SIMD.
        // Each element still gets ce + k·e, the same float operations as a plain loop, so results do not change.
        var ce = new float[n * 3];
        int K3 = K * 3;
        var kernel3 = new float[K * K3];
        for (int k = 0; k < K * K; k++) kernel3[k * 3] = kernel3[k * 3 + 1] = kernel3[k * 3 + 2] = kernel[k];
        var pattern = new float[K3];
        void Spread(int x, int y, float er, float eg, float eb)
        {
            for (int j = 0; j < K3; j += 3) { pattern[j] = er; pattern[j + 1] = eg; pattern[j + 2] = eb; }
            int y0 = Math.Max(0, y - R), y1 = Math.Min(h - 1, y + R), x0 = Math.Max(0, x - R), x1 = Math.Min(w - 1, x + R);
            int len = (x1 - x0 + 1) * 3, kx = (x0 - x + R) * 3;
            // Bounds are checked once above (the rows stay inside ce, kernel3 and pattern); the loop uses raw references.
            ref float pr = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(pattern), kx);
            ref float k0 = ref MemoryMarshal.GetArrayDataReference(kernel3);
            ref float c0r = ref MemoryMarshal.GetArrayDataReference(ce);
            int vn = Vector.IsHardwareAccelerated ? Vector<float>.Count : int.MaxValue;
            for (int yy = y0; yy <= y1; yy++)
            {
                ref float kr = ref Unsafe.Add(ref k0, (yy - y + R) * K3 + kx);
                ref float cr = ref Unsafe.Add(ref c0r, (yy * w + x0) * 3);
                int j = 0;
                for (; j <= len - vn; j += vn)
                {
                    var c = Vector.LoadUnsafe(ref cr, (nuint)j) + Vector.LoadUnsafe(ref kr, (nuint)j) * Vector.LoadUnsafe(ref pr, (nuint)j);
                    c.StoreUnsafe(ref cr, (nuint)j);
                }
                for (; j < len; j++) Unsafe.Add(ref cr, j) += Unsafe.Add(ref kr, j) * Unsafe.Add(ref pr, j);
            }
        }

        // Perceptual per-pixel cost: squared Oklab distance to the source colour. Mixing a colour from very different
        // ones (white with strong blue, pink and yellow dots for a pale lavender) gets the average right but looks
        // rough up close; this term and the radius below keep mixes to colours near the source.
        var ck = candidates.Select(c => ColorMath.ToOklab(c.Color.R, c.Color.G, c.Color.B)).ToArray();
        var ok = new Lab[n];
        for (int i = 0; i < n; i++) ok[i] = ColorMath.ToOklab(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2]);
        float pw = (float)settings.PerceptualWeight;
        double ph = settings.PixelHue;
        // Edges (line art, contours) get a higher per-pixel weight: dithering them frays the line, while flat areas
        // can mix. Edge strength = Sobel gradient of Oklab (L, a, b) at the pixel, 0..1 (0.15 per pixel = full edge).
        var edgeBoost = new float[n];
        if (settings.EdgeWeight > 0)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double gx = 0, gy = 0;
                    foreach (var (dx, dy, kx, ky) in Sobel)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                        var o2 = chosen[yy * w + xx] < 0 ? ok[y * w + x] : ok[yy * w + xx];
                        double v = o2.L + 0.5 * Math.Sqrt(o2.A * o2.A + o2.B * o2.B);
                        gx += kx * v; gy += ky * v;
                    }
                    double g = Math.Sqrt(gx * gx + gy * gy) / 4;
                    edgeBoost[y * w + x] = (float)(1 + settings.EdgeWeight * Math.Min(1, g / 0.15));
                }
        else Array.Fill(edgeBoost, 1f);
        float PixelCost(int i, int c)
        {
            double dl = ck[c].L - ok[i].L, da = ck[c].A - ok[i].A, db = ck[c].B - ok[i].B;
            double cost = pw * edgeBoost[i] * (dl * dl + da * da + db * db);
            if (ph > 0)
            {
                // Off-hue part of the colour relative to this pixel's hue (the same idea as the plan's PlanHue), so
                // the optimiser itself prefers dots on the source's hue line: pink in a pale pink, not red in salmon.
                double tA = ok[i].A, tB = ok[i].B, tC = Math.Sqrt(tA * tA + tB * tB);
                double off = tC > 0.02 ? Math.Abs(ck[c].A * tB - ck[c].B * tA) / tC : Math.Sqrt(ck[c].A * ck[c].A + ck[c].B * ck[c].B);
                cost += ph * off * off;
            }
            return (float)cost;
        }

        // Per pixel only candidates near the source colour are tried: the closest ones within Radius (Oklab) of the
        // nearest, at least two so that mixing stays possible. The lists never change: with sameShade the shade of a
        // pixel stays what it was.
        int take = Math.Min(settings.Neighbours, nc);
        var near = new int[n * take];
        var nearCost = new float[n * take];
        var counts = new int[n];
        // With mix plans (PairLambda) the plans replace every list, so the nearest-colour lists are not built at all.
        if (!(settings.PairLambda > 0))
        Parallel.For(0, h,new ParallelOptions { CancellationToken = ct }, () => (new int[nc], new float[nc], new int[take]), (y, _, buf) =>
        {
            var (order, dist, tried) = buf;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, cur = chosen[i];
                if (cur < 0 || frozen is not null && frozen[i]) continue;
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
            PairLists(image, w, h, candidates, chosen, sameShade, settings, ck, ok, take, near, nearCost, counts, PixelCost, ct, frozen);

        // Initial error field for the starting colours.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, c = chosen[i];
                // Frozen pixels are settled: omit their residual so neighbours do not try to make up for it (that drew
                // a rim of compensating dots along the inside of every flat area).
                if (c < 0 || frozen is not null && frozen[i]) continue;
                float s = sw[i];
                Spread(x, y, s * (cv[c * 3] - ov[i * 3]), s * (cv[c * 3 + 1] - ov[i * 3 + 1]), s * (cv[c * 3 + 2] - ov[i * 3 + 2]));
            }

        // Swaps (as in direct binary search halftoning): two neighbouring pixels exchange their colours when that lowers
        // the error. The local mix stays the same, only the arrangement of the dots improves, which single-pixel
        // changes cannot do once each pixel on its own is at its best.
        bool InList(int i, int c)
        {
            int start = i * take;
            for (int t = 0; t < counts[i]; t++) if (near[start + t] == c) return true;
            return false;
        }
        (int Dx, int Dy)[] offsets = [(1, 0), (0, 1), (1, 1), (-1, 1)];
        // Rounds of single-pixel passes and swap passes (a swap can open new single-pixel improvements).
        for (int round = 0; round < Math.Max(1, settings.Rounds); round++)
        {
            for (int pass = 0; pass < settings.MaxPasses; pass++)
            {
                ct.ThrowIfCancellationRequested();
                int changes = 0;
                for (int y = 0; y < h; y++)
                {
                    if ((y & 15) == 0) ct.ThrowIfCancellationRequested();
                    bool reverse = (y & 1) == 1;
                    for (int s = 0; s < w; s++)
                    {
                        int x = reverse ? w - 1 - s : s, i = y * w + x, cur = chosen[i];
                        if (cur < 0 || frozen is not null && frozen[i]) continue;
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

            for (int pass = 0; pass < settings.SwapPasses; pass++)
            {
                ct.ThrowIfCancellationRequested();
                int swaps = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x, ci = chosen[i];
                        if (ci < 0 || frozen is not null && frozen[i]) continue;
                        foreach (var (dx, dy) in offsets)
                        {
                            int x2 = x + dx, y2 = y + dy;
                            if (x2 < 0 || x2 >= w || y2 >= h) continue;
                            int j = y2 * w + x2, cj = chosen[j];
                            if (cj < 0 || cj == ci || frozen is not null && frozen[j] || !InList(i, cj) || !InList(j, ci)) continue;
                            float si = sw[i], sj = sw[j];
                            float ar = si * (cv[cj * 3] - cv[ci * 3]), ag = si * (cv[cj * 3 + 1] - cv[ci * 3 + 1]), ab = si * (cv[cj * 3 + 2] - cv[ci * 3 + 2]);
                            float br = sj * (cv[ci * 3] - cv[cj * 3]), bg = sj * (cv[ci * 3 + 1] - cv[cj * 3 + 1]), bb = sj * (cv[ci * 3 + 2] - cv[cj * 3 + 2]);
                            float kij = kernel[(dy + R) * K + dx + R];
                            float delta = 2 * (ar * ce[i * 3] + ag * ce[i * 3 + 1] + ab * ce[i * 3 + 2]) + c0 * (ar * ar + ag * ag + ab * ab)
                                + 2 * (br * ce[j * 3] + bg * ce[j * 3 + 1] + bb * ce[j * 3 + 2]) + c0 * (br * br + bg * bg + bb * bb)
                                + 2 * kij * (ar * br + ag * bg + ab * bb)
                                + PixelCost(i, cj) - PixelCost(i, ci) + PixelCost(j, ci) - PixelCost(j, cj);
                            if (delta >= -1e-9f) continue;
                            Spread(x, y, ar, ag, ab);
                            Spread(x2, y2, br, bg, bb);
                            chosen[i] = cj; chosen[j] = ci;
                            ci = cj;
                            swaps++;
                        }
                    }
                if (swaps <= n / 2000) break;
            }
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
        CancellationToken ct, bool[]? frozen = null)
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
        double lambda = settings.PairLambda, mu = settings.PairQuad, tol = settings.HueTolerance;
        // Penalty for the contrast inside a mix: linear in the whole contrast, plus a quadratic part in the contrast
        // ACROSS the target's hue (cross = spread of the signed off-hue distances). Red with yellow for a salmon orange
        // lies on both sides of its hue and loses; white with pink for a pale pink lies along it and is not touched,
        // so near-ties between "grey alone" and "white + pink" no longer flip whole areas (patches).
        double Spread(double s, double cross) => lambda * s + mu * cross * cross;
        int pool = Math.Min(settings.PairPool, nc);

        // Plans per colour bucket (5 bits per channel) and shade group, from the bucket centre: deterministic. (Finer
        // buckets were tried: more plan boundaries inside smooth areas, more grey islands in pale pinks.)
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
                // Signed distance from the target's hue line (neutral targets: chroma), and the penalised part beyond a
                // small tolerance (a pink wool slightly off a pale pink's hue is fine).
                double OffS(int c) => tC > 0.02 ? (ck[c].A * tB - ck[c].B * tA) / tC : Math.Sqrt(ck[c].A * ck[c].A + ck[c].B * ck[c].B);
                double Off(int c) => Math.Max(0, Math.Abs(OffS(c)) - tol);
                // Chroma lost along the target's hue by the mixed colour, beyond half of the target's chroma. Distance alone
                // let plain grey win over white + pink for pale pinks (#EED0D4): same lightness, ΔE small, colour gone,
                // because the mix pays for its contrast and the grey pays nothing for turning the hair grey.
                double kappa = settings.PlanChroma;
                double Fade(Lab mix) => kappa > 0 && tC > 0.02 ? kappa * Math.Max(0, tC - (mix.A * tA + mix.B * tB) / tC - 0.5 * tC) : 0;
                // Distance of a plan's colour to the target with lightness weighted by PlanLightness. Below 1 a single colour
                // that only differs in lightness (a flat purple drawn as plain ice, slightly lighter) beats a mix of two
                // shades that is closer on average but shows as texture up close; colour (a, b) errors keep full weight.
                double wl = settings.PlanLightness, mixCost = settings.MixCost;
                // The a/b error splits into chroma (along the target's hue) and hue (across it); for light pastels (Oklab
                // L ≥ 0.75) more chroma than the target counts PlanChromaExcess times: the palette has no light pinks, and
                // a stronger pink of the same hue reads as "pink" where pink + white reads as dots. Darker colours keep the
                // plain distance (otherwise photos turn garish).
                double we = target.L >= 0.75 ? settings.PlanChromaExcess : 1;
                double Dw(Lab a)
                {
                    if (wl == 1 && we == 1) return D(a, target);
                    double dl = a.L - target.L, da = a.A - target.A, db = a.B - target.B, ab2 = da * da + db * db;
                    if (we != 1 && tC > 0.02)
                    {
                        double ca = Math.Sqrt(a.A * a.A + a.B * a.B), dc = ca - tC;
                        double dh2 = Math.Max(0, ab2 - dc * dc);
                        // Only a moderately stronger colour is tolerated (up to 60 % more chroma: pink wool for a light pink
                        // cushion), not a much stronger one (orange for a peach skin, hot pink for a pale pink hair).
                        if (dc > 0) dc = Math.Min(dc, 0.6 * tC) * we + Math.Max(0, dc - 0.6 * tC);
                        ab2 = dh2 + dc * dc;
                    }
                    return Math.Sqrt(wl * wl * dl * dl + ab2);
                }
                double SingleCost(int c) => Dw(ck[c]) + beta * D(ck[c], target) + gamma * Off(c) + Fade(ck[c]);
                if (wl != 1)
                    foreach (int c in close)
                        if (SingleCost(c) < SingleCost(nearest)) nearest = c;
                double best = SingleCost(nearest);
                int ba = nearest, bb = -1;
                // Near-best plans are kept too (within PlanMargin): one hard choice per colour made neighbouring colours
                // flip between plans (grey vs white + pink in a pale gradient), which showed as square patches.
                double margin = settings.PlanMargin, relative = settings.PlanRelative;
                double Cut() => best + margin + best * relative;
                var top = new List<(double Cost, int A, int B, int C)> { (best, nearest, -1, -1) };
                void Keep(double cost, int a, int b, int c3)
                {
                    if (cost > Cut()) return;
                    top.Add((cost, a, b, c3));
                    if (top.Count > 64) top.RemoveAll(p => p.Cost > Cut());
                }
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
                        double cost = Dw(mix) + mixCost + Spread(S(ck[a], ck[c]) * 2 * Math.Sqrt(t * (1 - t)), Math.Abs(OffS(a) - OffS(c)) * 2 * Math.Sqrt(t * (1 - t)))
                            + beta * ((1 - t) * D(ck[a], target) + t * D(ck[c], target)) + gamma * ((1 - t) * Off(a) + t * Off(c)) + Fade(mix);
                        if (cost < best) { best = cost; ba = a; bb = c; }
                        Keep(cost, a, c, -1);
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
                            double oa = OffS(a), ob = OffS(b2), oc = OffS(c);
                            double cross = 2 * Math.Sqrt(w0 * u * (oa - ob) * (oa - ob) + w0 * v * (oa - oc) * (oa - oc) + u * v * (ob - oc) * (ob - oc));
                            double cost = Dw(mix) + mixCost + Spread(spread, cross)
                                + beta * (w0 * D(ck[a], target) + u * D(ck[b2], target) + v * D(ck[c], target))
                                + gamma * (w0 * Off(a) + u * Off(b2) + v * Off(c)) + Fade(mix);
                            if (cost < best) { best = cost; t1 = a; t2 = b2; t3 = c; }
                            Keep(cost, a, b2, c);
                        }
                // Colours of the best plan first, then of the near-best ones, then the nearest colour (a fallback that keeps
                // transitions between plans soft: without it pale pinks broke into grey islands).
                var list = new List<int>();
                void Add(int id) { if (id >= 0 && !list.Contains(id)) list.Add(id); }
                if (t1 >= 0) { Add(t1); Add(t2); Add(t3); } else { Add(ba); Add(bb); }
                foreach (var p in top.Where(p => p.Cost <= Cut()).OrderBy(p => p.Cost).ThenBy(p => p.A).ThenBy(p => p.B).ThenBy(p => p.C))
                {
                    if (list.Count >= settings.PlanColours) break;
                    Add(p.A); Add(p.B); Add(p.C);
                }
                Add(nearest);
                return [.. list];
            });
        }

        // Plans are also taken for a smoothed copy of the image: flat areas of compressed pictures carry blocky noise
        // (8x8 JPEG blocks) that flipped neighbouring blocks between plans and showed as square patches. Each pixel
        // gets its own plan's colours (sharp edges) and the smoothed one's (smooth transitions).
        var smooth = settings.PlanBlur > 0 ? BlurLinear(image, settings.PlanBlur) : null;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, cur = chosen[i];
                if (cur < 0 || frozen is not null && frozen[i]) continue;
                int shade = sameShade ? (int)candidates[cur].Shade : -1;
                int[] plan;
                if (smooth is null) plan = Plan(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2], shade);
                else
                {
                    // Flat areas use only the smoothed colour's plan, so noise cannot flip blocks between plans; near
                    // edges (where the smoothed colour differs) the pixel's own plan is added for its exact colour.
                    plan = Plan(smooth[i * 3], smooth[i * 3 + 1], smooth[i * 3 + 2], shade);
                    if (settings.PlanEdge <= 0)
                    {
                        // Union mode: the pixel's own plan and the smoothed one's.
                        var po = Plan(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2], shade);
                        plan = [.. po, .. plan.Where(c => Array.IndexOf(po, c) < 0)];
                    }
                    else
                    {
                    var own = ColorMath.ToOklab(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2]);
                    var sm = ColorMath.ToOklab(smooth[i * 3], smooth[i * 3 + 1], smooth[i * 3 + 2]);
                    double dl = own.L - sm.L, da = own.A - sm.A, db = own.B - sm.B;
                    if (dl * dl + da * da + db * db > settings.PlanEdge * settings.PlanEdge)
                    {
                        var p1 = Plan(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2], shade);
                        plan = [.. p1, .. plan.Where(c => Array.IndexOf(p1, c) < 0)];
                    }
                    }
                }
                int count = Math.Min(plan.Length, take);
                for (int t = 0; t < count; t++) { near[i * take + t] = plan[t]; nearCost[i * take + t] = pixelCost(i, plan[t]); }
                counts[i] = count;
                if (Array.IndexOf(plan, cur) < 0)
                    chosen[i] = plan.OrderBy(c => Sq(lin[c * 3] - lin[cur * 3]) + Sq(lin[c * 3 + 1] - lin[cur * 3 + 1]) + Sq(lin[c * 3 + 2] - lin[cur * 3 + 2])).First();
            }
        });
    }

    private static double Sq(double v) => v * v;

    private static readonly (int Dx, int Dy, int Kx, int Ky)[] Sobel =
        [(-1, -1, -1, -1), (0, -1, 0, -2), (1, -1, 1, -1), (-1, 0, -2, 0), (1, 0, 2, 0), (-1, 1, -1, 1), (0, 1, 0, 2), (1, 1, 1, 1)];

    /// <summary>
    /// Bilateral filter (13x13, spatial σ 3, range σ <paramref name="range"/> in sRGB steps): removes the small block
    /// noise of compressed pictures in flat areas, which made the optimiser decide block by block (square patches of
    /// pink and grey in a pale pink), while keeping lines and edges, which differ by far more. Transparent pixels are
    /// left alone and do not contribute.
    /// </summary>
    internal static RgbaImage Denoise(RgbaImage img, double range, CancellationToken ct = default)
    {
        // Wider than an 8x8 compression block, or the inside of each block would keep its offset.
        const int R = 6, K = 2 * R + 1;
        const double Sigma = 3;
        int w = img.Width, h = img.Height;
        var dst = img.Clone();
        var spatial = new double[K * K];
        for (int dy = -R; dy <= R; dy++) for (int dx = -R; dx <= R; dx++) spatial[(dy + R) * K + dx + R] = Math.Exp(-(dx * dx + dy * dy) / (2 * Sigma * Sigma));
        double inv = 1 / (2 * range * range);
        // Range weights for every possible squared distance (integers), computed once instead of per neighbour; beyond
        // the table Math.Exp is exactly 0.
        int maxD2 = 3 * 255 * 255;
        var rangeTable = new List<double>();
        for (int d2 = 0; d2 <= maxD2; d2++)
        {
            double v = Math.Exp(-d2 * inv);
            if (v == 0) break;
            rangeTable.Add(v);
        }
        var rangeW = rangeTable.ToArray();
        var p = img.Pixels;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (p[i + 3] == 0) continue;
                double sr = 0, sg = 0, sb = 0, ws = 0;
                for (int dy = -R; dy <= R; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= h) continue;
                    for (int dx = -R; dx <= R; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= w) continue;
                        int j = (yy * w + xx) * 4;
                        if (p[j + 3] == 0) continue;
                        int dr = p[j] - p[i], dg = p[j + 1] - p[i + 1], db = p[j + 2] - p[i + 2];
                        int d2 = dr * dr + dg * dg + db * db;
                        if (d2 >= rangeW.Length) continue; // weight exactly 0
                        double wt = spatial[(dy + R) * K + dx + R] * rangeW[d2];
                        sr += p[j] * wt; sg += p[j + 1] * wt; sb += p[j + 2] * wt; ws += wt;
                    }
                }
                dst.Pixels[i] = (byte)Math.Round(sr / ws); dst.Pixels[i + 1] = (byte)Math.Round(sg / ws); dst.Pixels[i + 2] = (byte)Math.Round(sb / ws);
            }
        });
        return dst;
    }

    /// <summary>
    /// Pixels inside flat colour areas (Hybrid). Local slope uses Oklab differences two/four steps away, with an
    /// additional directional trend check at eight/sixteen steps, on a denoised copy, counting only
    /// neighbours of the same area (difference below 0.06: an outline next to a flat area does not make it uneven).
    /// Flat = slope below <paramref name="slope"/> per pixel and all 8 neighbours agreeing (neighbours in
    /// another area count as agreeing, so pixels along an outline stay flat).
    /// A smooth gradient across a map (Oklab 0.2 over 128 pixels) is about 0.0016 per pixel: not flat. Connected flat patches
    /// smaller than <paramref name="minArea"/> pixels are dropped.
    /// </summary>
    public static bool[] FlatAreas(RgbaImage image, int alphaThreshold, double slope = 0.0008, int minArea = 150, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        int w = image.Width, h = image.Height, n = w * h;
        var masked = image;
        if (Enumerable.Range(0, n).Any(i => image.Pixels[i * 4 + 3] > 0 && image.Pixels[i * 4 + 3] < alphaThreshold))
        {
            masked = image.Clone();
            for (int i = 0; i < n; i++) if (masked.Pixels[i * 4 + 3] < alphaThreshold) masked.Pixels[i * 4 + 3] = 0;
        }
        var den = Denoise(masked, 4, ct);
        var ok = new Lab[n];
        var valid = new bool[n];
        for (int i = 0; i < n; i++)
        {
            valid[i] = image.Pixels[i * 4 + 3] >= alphaThreshold;
            ok[i] = ColorMath.ToOklab(den.Pixels[i * 4], den.Pixels[i * 4 + 1], den.Pixels[i * 4 + 2]);
        }
        static double Dist(Lab a, Lab b) => Math.Sqrt((a.L - b.L) * (a.L - b.L) + (a.A - b.A) * (a.A - b.A) + (a.B - b.B) * (a.B - b.B));
        // Two and four steps away: the wider step sees a slow gradient over its 8-bit steps.
        (int Dx, int Dy)[] dirs = [(-2, -2), (0, -2), (2, -2), (-2, 0), (2, 0), (-2, 2), (0, 2), (2, 2),
            (-4, -4), (0, -4), (4, -4), (-4, 0), (4, 0), (-4, 4), (0, 4), (4, 4)];
        var raw = new bool[n];
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!valid[i]) continue;
                double sum = 0;
                int count = 0;
                foreach (var (dx, dy) in dirs)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    int j = yy * w + xx;
                    if (!valid[j]) continue;
                    double d = Dist(ok[i], ok[j]) / Math.Sqrt(dx * dx + dy * dy);
                    if (d * Math.Sqrt(dx * dx + dy * dy) >= 0.06) continue;
                    sum += d;
                    count++;
                }
                raw[i] = count >= 6 && sum / count < slope;
            }
        });
        // Averaging directions dilutes a one-axis gradient with its zero-slope perpendicular neighbours. Check the
        // directional trend over 8/16 pixels too, so quantized skies do not become one large "flat" component.
        // Only pixels that are locally level themselves are compared: an outline's anti-aliased pixels are within
        // the colour limit of the area they border, and comparing against them un-flattened every pixel within
        // 16 pixels of an outline (a whole small disc in an illustration).
        var local = (bool[])raw.Clone();
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!local[i]) continue;
                for (int direction = 0; direction < 8 && raw[i]; direction++)
                    for (int scale = 4; scale <= 8; scale *= 2)
                    {
                        int dx = dirs[direction].Dx * scale, dy = dirs[direction].Dy * scale;
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int j = yy * w + xx;
                        if (!valid[j] || !local[j]) continue;
                        // The path must not cross an outline: two similar flat areas separated by one (a cream face on
                        // a cream background) are not one gradient. An outline is a jump between neighbouring pixels;
                        // a quantized sky only steps by one level.
                        bool crosses = false;
                        int prev = i;
                        for (int k = 1; k <= scale * 2 && !crosses; k++)
                        {
                            int px = x + dirs[direction].Dx * k / 2, py = y + dirs[direction].Dy * k / 2;
                            int m = py * w + px;
                            if (m == prev) continue;
                            if (!valid[m] || Dist(ok[prev], ok[m]) > 0.015) crosses = true;
                            prev = m;
                        }
                        if (crosses) continue;
                        double distance = Dist(ok[i], ok[j]);
                        if (distance < 0.06 && distance / Math.Sqrt(dx * dx + dy * dy) >= slope)
                            raw[i] = false;
                    }
            }
        });
        var flat = new bool[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!raw[i]) continue;
                int around = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        // Neighbours outside the image or in another area (an outline) do not count against it.
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) { around++; continue; }
                        int j = yy * w + xx;
                        if (raw[j] || !valid[j] || Dist(ok[i], ok[j]) >= 0.03) around++;
                    }
                flat[i] = around == 8;
            }
        // Small flat patches are dropped: a nearly level spot inside a sky or a slow gradient is not a flat area, and
        // freezing it left a solid square in the gradient. Real flat areas of illustrations are much larger.
        {
            var seen = new bool[n];
            var stack = new Stack<int>();
            var members = new List<int>();
            for (int s = 0; s < n; s++)
            {
                if ((s & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (!flat[s] || seen[s]) continue;
                members.Clear();
                stack.Push(s);
                seen[s] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    members.Add(i);
                    int x = i % w, y = i / w;
                    if ((members.Count & 4095) == 0) ct.ThrowIfCancellationRequested();
                    if (x > 0) Visit(i - 1); if (x < w - 1) Visit(i + 1);
                    if (y > 0) Visit(i - w); if (y < h - 1) Visit(i + w);
                }
                if (members.Count < minArea) foreach (int i in members) flat[i] = false;
            }
            void Visit(int j) { if (flat[j] && !seen[j]) { seen[j] = true; stack.Push(j); } }
        }
        // Grow each flat area up to its outline: pixels within 4 steps of a flat pixel of the same colour (Oklab < 0.02)
        // belong to it, so the strip next to an outline is not left to the other model. Outline and anti-aliasing
        // pixels differ in colour and stay out.
        var grown = (bool[])flat.Clone();
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (flat[i] || !valid[i]) continue;
                for (int dy = -4; dy <= 4 && !grown[i]; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int j = yy * w + xx;
                        if (flat[j] && Dist(ok[i], ok[j]) < 0.02) { grown[i] = true; break; }
                    }
            }
        });
        return grown;
    }

    /// <summary>Gaussian blur in linear light, returned as sRGB bytes (3 per pixel).</summary>
    private static byte[] BlurLinear(RgbaImage img, double sigma)
    {
        int w = img.Width, h = img.Height;
        var linear = new double[w * h * 3];
        var valid = new bool[w * h];
        for (int i = 0; i < w * h; i++)
        {
            valid[i] = img.Pixels[i * 4 + 3] != 0;
            for (int c = 0; c < 3; c++) linear[i * 3 + c] = ColorMath.SrgbToLinear(img.Pixels[i * 4 + c]);
        }
        var blurred = DitherFilter.Blur(linear, valid, w, h, sigma);
        return Array.ConvertAll(blurred, v => (byte)Math.Clamp(Math.Round(ColorMath.LinearToSrgb(v)), 0, 255));
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
    /// <summary>Passes of neighbour swaps after the single-pixel passes (0 = none).</summary>
    public int SwapPasses { get; init; }
    /// <summary>Rounds of single-pixel and swap passes.</summary>
    public int Rounds { get; init; } = 1;
    /// <summary>Extra per-pixel weight on edges (0 = off): keeps line art and contours crisp.</summary>
    public double EdgeWeight { get; init; }
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
    /// <summary>
    /// Weight of the chroma a plan loses along the target's hue (beyond half of it) when choosing it: a pale pink drawn
    /// as plain grey is close by distance but has lost its colour.
    /// </summary>
    public double PlanChroma { get; init; }
    /// <summary>Weight of lightness in the distance of a plan's colour to the target (1 = plain Oklab distance).</summary>
    public double PlanLightness { get; init; } = 1;
    /// <summary>Extra cost of any plan that mixes two or three colours, so a close single colour stays solid.</summary>
    public double MixCost { get; init; }
    /// <summary>Weight of chroma beyond the target's in a plan's colour (1 = plain distance; below 1 a stronger colour of
    /// the same hue is preferred to mixing in white or grey).</summary>
    public double PlanChromaExcess { get; init; } = 1;
    /// <summary>Weight of colour (chroma) errors relative to errors along the grey axis in the optimiser (1 = plain).</summary>
    public double ChromaWeight { get; init; } = 1;
    /// <summary>Quadratic part of the mix contrast penalty.</summary>
    public double PairQuad { get; init; }
    /// <summary>Plans within this cost of the best one also lend their colours (smooth transitions between plans).</summary>
    public double PlanMargin { get; init; }
    /// <summary>At most this many colours from near-best plans per pixel.</summary>
    public int PlanColours { get; init; } = 6;
    /// <summary>Plans within this fraction of the best cost also lend their colours.</summary>
    public double PlanRelative { get; init; }
    /// <summary>Per-pixel weight of the squared off-hue distance in the optimiser.</summary>
    public double PixelHue { get; init; }
    /// <summary>Off-hue distance (Oklab) below which a colour counts as on the target's hue.</summary>
    public double HueTolerance { get; init; }
    /// <summary>Blur (σ px) of the copy whose plans are added to each pixel's own (0 = off).</summary>
    public double PlanBlur { get; init; }
    /// <summary>Oklab distance from the smoothed colour above which a pixel counts as an edge and keeps its own plan.</summary>
    public double PlanEdge { get; init; } = 0.03;
    /// <summary>Edge-preserving smoothing of the source first: range σ in sRGB steps (0 = off).</summary>
    public double Denoise { get; init; }
}
