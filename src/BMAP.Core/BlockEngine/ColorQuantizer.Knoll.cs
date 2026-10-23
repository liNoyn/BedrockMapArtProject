using BMAP.ColorEngine;

namespace BMAP.BlockEngine;

public static partial class ColorQuantizer
{
    /// <summary>Candidates per Knoll mix (one per threshold level of the 8x8 Bayer matrix).</summary>
    private const int KnollN = 64;

    internal sealed partial class NearestMatcher
    {
        private readonly Dictionary<int, int[]?> _plans = new();
        private double[]? _luma;
        private double[]? _lin;

        /// <summary>
        /// Knoll's pattern dithering: approximates the colour by a mix of <see cref="KnollN"/> palette colours
        /// (repeats allowed), found by moving the goal by the accumulated error each time; the mix is sorted by
        /// luminance and <paramref name="threshold"/> (0..1, from the dither matrix) picks one entry.
        /// Plans use the exact 24-bit source colour. Cache capacity only affects speed, never the target colour.
        /// </summary>
        public int KnollPick(byte r, byte g, byte b, int shadeMask, QuantizeOptions options, double threshold)
        {
            int closest = Find(r, g, b, shadeMask, options.ShadePenalty);
            if (closest < 0) return -1;
            var exact = _candidates[closest].Color;
            if (options.DitherStrength <= 0 || (exact.R == r && exact.G == g && exact.B == b)) return closest;
            int key = (shadeMask << 24) | (r << 16) | (g << 8) | b;
            if (!_plans.TryGetValue(key, out var plan))
            {
                plan = BuildPlan(r, g, b, shadeMask, options);
                if (_plans.Count < 4096) _plans[key] = plan;
            }
            if (plan is null) return -1;
            return plan[Math.Clamp((int)(threshold * plan.Length), 0, plan.Length - 1)];
        }

        private int[]? BuildPlan(double r, double g, double b, int shadeMask, QuantizeOptions options)
        {
            _lin ??= _candidates.SelectMany(c => new[] { Lin(c.Color.R), Lin(c.Color.G), Lin(c.Color.B) }).ToArray();
            _luma ??= Enumerable.Range(0, _candidates.Count)
                .Select(i => 0.2126 * _lin[i * 3] + 0.7152 * _lin[i * 3 + 1] + 0.0722 * _lin[i * 3 + 2]).ToArray();
            bool linear = options.LinearDiffusion;
            double k = Math.Clamp(options.DitherStrength, 0, 1);
            // Target and error in linear light (or sRGB values when linear mixing is off).
            double pr = linear ? LinOf(r) : r, pg = linear ? LinOf(g) : g, pb = linear ? LinOf(b) : b;
            double er = 0, eg = 0, eb = 0;
            var plan = new int[KnollN];
            for (int n = 0; n < KnollN; n++)
            {
                double gr = Math.Clamp(pr + er * k, 0, 1 * Scale(linear)), gg = Math.Clamp(pg + eg * k, 0, Scale(linear)), gb = Math.Clamp(pb + eb * k, 0, Scale(linear));
                double sr = linear ? ColorMath.LinearToSrgb(gr) : gr, sg = linear ? ColorMath.LinearToSrgb(gg) : gg, sb = linear ? ColorMath.LinearToSrgb(gb) : gb;
                int c = Find(sr, sg, sb, shadeMask, options.ShadePenalty);
                if (c < 0) return null;
                plan[n] = c;
                double cr = linear ? _lin[c * 3] : _candidates[c].Color.R;
                double cg = linear ? _lin[c * 3 + 1] : _candidates[c].Color.G;
                double cb = linear ? _lin[c * 3 + 2] : _candidates[c].Color.B;
                er += pr - cr; eg += pg - cg; eb += pb - cb;
            }
            var luma = _luma;
            Array.Sort(plan, (a, c) => a == c ? 0 : luma[a] != luma[c] ? luma[a].CompareTo(luma[c]) : a.CompareTo(c));
            return plan;

            static double Scale(bool lin) => lin ? 1 : 255;
            static double Lin(byte v) => ColorMath.SrgbToLinear(v);
            static double LinOf(double v)
            {
                double c = Math.Clamp(v, 0, 255) / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
        }
    }
}
