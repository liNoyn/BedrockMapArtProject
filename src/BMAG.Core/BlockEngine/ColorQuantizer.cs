using BMAG.ColorEngine;
using BMAG.Imaging;
using BMAG.Palette;

namespace BMAG.BlockEngine;

public enum DitherMode
{
    None,
    FloydSteinberg,
    /// <summary>Jarvis–Judice–Ninke ("minimized average error").</summary>
    JarvisJudiceNinke,
    Burkes,
    SierraLite,
    Stucki,
    Atkinson,
    Bayer2,
    Bayer4,
    Bayer8,
    Ordered3,
}

/// <summary>How heights are used for shading (MapartCraft's "3D" setting).</summary>
public enum StaircaseMode
{
    /// <summary>Flat: every block at one height, normal shade only.</summary>
    Flat,
    /// <summary>Three shades; each column climbs/descends continuously from the support row.</summary>
    Classic,
    /// <summary>Three shades; columns drop back to the lowest possible height whenever they can (lower build).</summary>
    Valley,
    /// <summary>Dark shade only (every block one lower than its northern neighbour).</summary>
    FullDark,
    /// <summary>Bright shade only (every block one higher than its northern neighbour).</summary>
    FullBright,
}

public static class StaircaseModes
{
    public static Shade[] Shades(StaircaseMode mode) => mode switch
    {
        StaircaseMode.Flat => [Shade.Normal],
        StaircaseMode.FullDark => [Shade.Dark],
        StaircaseMode.FullBright => [Shade.Bright],
        _ => [Shade.Dark, Shade.Normal, Shade.Bright],
    };
}

public sealed record QuantizeOptions
{
    public ColorMetric Metric { get; init; } = ColorMetric.Oklab;
    /// <summary>Error diffusion: alternate the scan direction per row (removes directional artefacts).</summary>
    public bool Serpentine { get; init; } = true;
    /// <summary>Error diffusion / ordered dithering strength, 0..1.</summary>
    public double DitherStrength { get; init; } = 1.0;
    public DitherMode Dither { get; init; } = DitherMode.None;
    public StaircaseMode Mode { get; init; } = StaircaseMode.Flat;
    /// <summary>Pixels with alpha below this become empty (no block). Only relevant when transparency is kept.</summary>
    public int AlphaThreshold { get; init; } = 128;
}

/// <summary>A palette entry in one particular shade, i.e. one reachable map colour.</summary>
public readonly record struct Candidate(int Entry, Shade Shade, Rgba Color);

public sealed class QuantizeResult
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Index into <see cref="Entries"/> per pixel, -1 = empty.</summary>
    public required int[] EntryIndex { get; init; }
    public required Shade[] Shades { get; init; }
    public required List<PaletteEntry> Entries { get; init; }
    /// <summary>Predicted map colours ("Block Map" preview, design 11.3).</summary>
    public required RgbaImage Preview { get; init; }
    public StaircaseMode Mode { get; init; }

    public int Get(int x, int y) => EntryIndex[y * Width + x];
    public Shade GetShade(int x, int y) => Shades[y * Width + x];

    /// <summary>Deep copy (manual edits are applied to copies so the automatic result can be reused).</summary>
    public QuantizeResult Clone() => new()
    {
        Width = Width, Height = Height, EntryIndex = (int[])EntryIndex.Clone(), Shades = (Shade[])Shades.Clone(),
        Entries = [.. Entries], Preview = Preview.Clone(), Mode = Mode,
    };
}

/// <summary>Converts an image into block map colours (ColorQuantizer, design 12.1). Deterministic for the same input.</summary>
public static class ColorQuantizer
{
    public static List<Candidate> BuildCandidates(ColorProfile profile, List<PaletteEntry> entries, StaircaseMode mode)
    {
        var list = new List<Candidate>();
        for (int i = 0; i < entries.Count; i++)
            foreach (var s in StaircaseModes.Shades(mode))
                list.Add(new Candidate(i, s, profile.ColorOf(entries[i].Block, s)));
        return list;
    }

    public static QuantizeResult Quantize(RgbaImage image, ColorProfile profile, PaletteSelection selection,
        QuantizeOptions options, CancellationToken ct = default)
    {
        var entries = selection.Resolve(profile);
        if (entries.Count == 0) throw new InvalidOperationException("使用できるブロックがありません。パレットでブロックを選んでください。");

        var result = QuantizeWith(image, profile, entries, options, ct);
        // Survival limit: drop the least used blocks until the number of block types fits (design 6.8).
        if (selection.MaxBlockTypes > 0)
        {
            while (true)
            {
                var counts = new int[entries.Count];
                foreach (int e in result.EntryIndex) if (e >= 0) counts[e]++;
                int used = counts.Count(c => c > 0);
                if (used <= selection.MaxBlockTypes) break;
                int keepCount = Math.Max(selection.MaxBlockTypes, used - Math.Max(1, (used - selection.MaxBlockTypes) / 2));
                entries = Enumerable.Range(0, entries.Count).Where(i => counts[i] > 0)
                    .OrderByDescending(i => counts[i]).Take(keepCount).OrderBy(i => i).Select(i => entries[i]).ToList();
                result = QuantizeWith(image, profile, entries, options, ct);
            }
        }
        return result;
    }

    private static QuantizeResult QuantizeWith(RgbaImage image, ColorProfile profile, List<PaletteEntry> entries,
        QuantizeOptions options, CancellationToken ct)
    {
        var candidates = BuildCandidates(profile, entries, options.Mode);
        int w = image.Width, h = image.Height, n = w * h;
        var idx = new int[n];
        var shades = new Shade[n];
        var preview = new RgbaImage(w, h);

        void Assign(int i, int c)
        {
            var cand = candidates[c];
            idx[i] = cand.Entry;
            shades[i] = cand.Shade;
            preview.Pixels[i * 4] = cand.Color.R;
            preview.Pixels[i * 4 + 1] = cand.Color.G;
            preview.Pixels[i * 4 + 2] = cand.Color.B;
            preview.Pixels[i * 4 + 3] = 255;
        }

        bool IsEmpty(int i) => image.Pixels[i * 4 + 3] < options.AlphaThreshold;

        var ordered = OrderedMatrix(options.Dither);
        if (options.Dither == DitherMode.None || ordered != null)
        {
            int size = ordered is null ? 1 : (int)Math.Sqrt(ordered.Length);
            const double spread = 32; // roughly the typical distance between neighbouring palette colours
            Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, () => new NearestMatcher(candidates, options.Metric), (y, _, local) =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (IsEmpty(i)) { idx[i] = -1; shades[i] = Shade.Normal; continue; }
                    double r = image.Pixels[i * 4], g = image.Pixels[i * 4 + 1], b = image.Pixels[i * 4 + 2];
                    if (ordered != null)
                    {
                        double t = (ordered[(y % size) * size + x % size] + 0.5) / ordered.Length - 0.5;
                        double k = t * spread * options.DitherStrength;
                        r += k; g += k; b += k;
                    }
                    Assign(i, local.Find(r, g, b));
                }
                return local;
            }, _ => { });
        }
        else
        {
            ErrorDiffuse(image, w, h, options, new NearestMatcher(candidates, options.Metric), candidates, IsEmpty, idx, shades, Assign, ct);
        }

        return new QuantizeResult
        {
            Width = w, Height = h, EntryIndex = idx, Shades = shades, Entries = entries, Preview = preview, Mode = options.Mode,
        };
    }

    /// <summary>Threshold matrix (values 0..n-1) for ordered dithering, or null for error diffusion.</summary>
    internal static int[]? OrderedMatrix(DitherMode mode) => mode switch
    {
        DitherMode.Bayer2 => BayerMatrix(2),
        DitherMode.Bayer4 => BayerMatrix(4),
        DitherMode.Bayer8 => BayerMatrix(8),
        DitherMode.Ordered3 => [0, 6, 3, 4, 7, 2, 5, 1, 8],
        _ => null,
    };

    internal static (int dx, int dy, double weight)[] DiffusionKernel(DitherMode mode)
    {
        (int, int, double)[] K(double div, params (int, int, double)[] k) => k.Select(t => (t.Item1, t.Item2, t.Item3 / div)).ToArray();
        return mode switch
        {
            DitherMode.Atkinson => K(8, (1, 0, 1), (2, 0, 1), (-1, 1, 1), (0, 1, 1), (1, 1, 1), (0, 2, 1)),
            DitherMode.JarvisJudiceNinke => K(48, (1, 0, 7), (2, 0, 5), (-2, 1, 3), (-1, 1, 5), (0, 1, 7), (1, 1, 5), (2, 1, 3),
                (-2, 2, 1), (-1, 2, 3), (0, 2, 5), (1, 2, 3), (2, 2, 1)),
            DitherMode.Burkes => K(32, (1, 0, 8), (2, 0, 4), (-2, 1, 2), (-1, 1, 4), (0, 1, 8), (1, 1, 4), (2, 1, 2)),
            DitherMode.SierraLite => K(4, (1, 0, 2), (-1, 1, 1), (0, 1, 1)),
            DitherMode.Stucki => K(42, (1, 0, 8), (2, 0, 4), (-2, 1, 2), (-1, 1, 4), (0, 1, 8), (1, 1, 4), (2, 1, 2),
                (-2, 2, 1), (-1, 2, 2), (0, 2, 4), (1, 2, 2), (2, 2, 1)),
            _ => K(16, (1, 0, 7), (-1, 1, 3), (0, 1, 5), (1, 1, 1)),
        };
    }

    private static void ErrorDiffuse(RgbaImage image, int w, int h, QuantizeOptions options, NearestMatcher matcher,
        List<Candidate> candidates, Func<int, bool> isEmpty, int[] idx, Shade[] shades, Action<int, int> assign, CancellationToken ct)
    {
        var kernel = DiffusionKernel(options.Dither);
        var buf = new float[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            buf[i * 3] = image.Pixels[i * 4];
            buf[i * 3 + 1] = image.Pixels[i * 4 + 1];
            buf[i * 3 + 2] = image.Pixels[i * 4 + 2];
        }
        double strength = Math.Clamp(options.DitherStrength, 0, 1);
        for (int y = 0; y < h; y++)
        {
            if ((y & 15) == 0) ct.ThrowIfCancellationRequested();
            bool reverse = options.Serpentine && (y & 1) == 1;
            for (int step = 0; step < w; step++)
            {
                int x = reverse ? w - 1 - step : step;
                int i = y * w + x;
                if (isEmpty(i)) { idx[i] = -1; shades[i] = Shade.Normal; continue; }
                double r = Math.Clamp(buf[i * 3], 0, 255), g = Math.Clamp(buf[i * 3 + 1], 0, 255), b = Math.Clamp(buf[i * 3 + 2], 0, 255);
                int c = matcher.Find(r, g, b);
                assign(i, c);
                var col = candidates[c].Color;
                double er = (r - col.R) * strength, eg = (g - col.G) * strength, eb = (b - col.B) * strength;
                foreach (var (dx, dy, wt) in kernel)
                {
                    int nx = reverse ? x - dx : x + dx, ny = y + dy;
                    if (nx < 0 || nx >= w || ny >= h) continue;
                    int ni = (ny * w + nx) * 3;
                    buf[ni] += (float)(er * wt);
                    buf[ni + 1] += (float)(eg * wt);
                    buf[ni + 2] += (float)(eb * wt);
                }
            }
        }
    }

    internal static int[] BayerMatrix(int size)
    {
        int[] m = [0];
        int n = 1;
        while (n < size)
        {
            var next = new int[n * 2 * n * 2];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int v = m[y * n + x] * 4;
                next[y * 2 * n + x] = v;
                next[y * 2 * n + x + n] = v + 2;
                next[(y + n) * 2 * n + x] = v + 3;
                next[(y + n) * 2 * n + x + n] = v + 1;
            }
            m = next;
            n *= 2;
        }
        return m;
    }

    /// <summary>Finds the nearest candidate colour, with a cache per quantised input colour.</summary>
    private sealed class NearestMatcher
    {
        private readonly List<Candidate> _candidates;
        private readonly ColorMetric _metric;
        private readonly Lab[] _labs;
        private readonly Dictionary<int, int> _cache = new();

        public NearestMatcher(List<Candidate> candidates, ColorMetric metric)
        {
            _candidates = candidates;
            _metric = metric;
            _labs = candidates.Select(c => metric == ColorMetric.Oklab
                ? ColorMath.ToOklab(c.Color.R, c.Color.G, c.Color.B)
                : ColorMath.ToLab(c.Color.R, c.Color.G, c.Color.B)).ToArray();
        }

        public int Find(double r, double g, double b)
        {
            int ri = (int)Math.Round(Math.Clamp(r, 0, 255)), gi = (int)Math.Round(Math.Clamp(g, 0, 255)), bi = (int)Math.Round(Math.Clamp(b, 0, 255));
            int key = (ri << 16) | (gi << 8) | bi;
            if (_cache.TryGetValue(key, out int hit)) return hit;

            int best = 0;
            double bestD = double.MaxValue;
            Lab lab = _metric switch
            {
                ColorMetric.Lab or ColorMetric.Ciede2000 => ColorMath.ToLab(ri, gi, bi),
                ColorMetric.Oklab => ColorMath.ToOklab(ri, gi, bi),
                _ => default,
            };
            for (int i = 0; i < _candidates.Count; i++)
            {
                var c = _candidates[i].Color;
                double d = _metric switch
                {
                    ColorMetric.Rgb => Sq(ri - c.R) + Sq(gi - c.G) + Sq(bi - c.B),
                    ColorMetric.WeightedRgb => ColorMath.WeightedRgbSq(ri, gi, bi, c.R, c.G, c.B),
                    ColorMetric.Lab or ColorMetric.Oklab => ColorMath.DeltaE76(lab, _labs[i]),
                    _ => ColorMath.DeltaE2000(lab, _labs[i]),
                };
                if (d < bestD) { bestD = d; best = i; } // strict '<' keeps the first (preferred) candidate on ties
            }
            if (_cache.Count < 1 << 20) _cache[key] = best;
            return best;
        }

        private static double Sq(double v) => v * v;
    }
}
