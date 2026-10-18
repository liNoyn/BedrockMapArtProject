using BMAP.ColorEngine;
using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.BlockEngine;

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
    /// <summary>Ordered dithering with a 64x64 blue-noise matrix (void-and-cluster): no regular pattern.</summary>
    BlueNoise,
    /// <summary>
    /// Knoll's pattern dithering (Thomas Knoll, used in Photoshop) with the Bayer 8x8 matrix: each colour is
    /// approximated by a mix of palette colours found by accumulating the error, and the matrix picks one of them.
    /// Much better than adding noise to the colour when the palette is irregular, as map colours are.
    /// </summary>
    KnollBayer,
    /// <summary>Knoll's pattern dithering with the blue-noise matrix.</summary>
    KnollBlueNoise,
    /// <summary>
    /// Error diffusion followed by direct optimisation of the whole picture (<see cref="ColorQuantizer.Refine"/>):
    /// the closest match to the source at every viewing distance. Slower.
    /// </summary>
    Optimized,
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
    /// <summary>
    /// Valley only: the largest height difference the build may have (0 = unlimited). The shades are then chosen so
    /// that no column needs more levels: optimally (dynamic programming) without dithering or with ordered dithering.
    /// With error diffusion a run is stopped when it reaches the limit and the colour error is diffused to the
    /// neighbours (measured better than planning the shades first and dithering inside the plan).
    /// </summary>
    public int MaxHeight { get; init; }
    /// <summary>
    /// Extra cost (0..1, relative) for the dark and light shades. Higher values prefer the normal shade when the
    /// colours are close, which gives lower, calmer staircases at a small colour cost.
    /// </summary>
    public double ShadePenalty { get; init; }

    /// <summary>True when the height limit applies (it is meaningless for flat and full dark/light).</summary>
    /// <remarks>
    /// Valley only: a Classic staircase never drops back, so its height is the running sum of all steps and cannot
    /// be bounded by limiting the length of runs.
    /// </remarks>
    public bool HeightLimited => MaxHeight > 0 && Mode == StaircaseMode.Valley;
    public DitherMode Dither { get; init; } = DitherMode.None;
    public StaircaseMode Mode { get; init; } = StaircaseMode.Flat;
    /// <summary>
    /// Error diffusion and Knoll mixing in linear light instead of sRGB values. Light mixes linearly, so a dithered
    /// area seen from a distance matches the source brightness more closely (dark tones especially).
    /// </summary>
    public bool LinearDiffusion { get; init; } = true;
    /// <summary>Pixels with alpha below this become empty (no block). Only relevant when transparency is kept.</summary>
    public int AlphaThreshold { get; init; } = 128;
    /// <summary>Optimise the result directly afterwards (see <see cref="ColorQuantizer.Refine"/>); null = off.</summary>
    public RefineSettings? Refine { get; init; }
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
    /// <summary>Height difference used for one light / dark step (the profile's shading rule): the larger of the two.</summary>
    public int Step { get; init; } = 1;
    /// <summary>Height difference for a light step and for a dark step (may differ).</summary>
    public int UpStep { get; init; } = 1;
    public int DownStep { get; init; } = 1;
    /// <summary>
    /// The game shades a ±1 step only on every other pixel (light where x + row is odd, dark where it is even) and ±2
    /// always (measured on Bedrock 1.26.51). Layouts placed on the map grid can then use single steps where the parity
    /// allows it.
    /// </summary>
    public bool ParityShading { get; init; }

    public int Get(int x, int y) => EntryIndex[y * Width + x];
    public Shade GetShade(int x, int y) => Shades[y * Width + x];

    /// <summary>Deep copy (manual edits are applied to copies so the automatic result can be reused).</summary>
    public QuantizeResult Clone() => new()
    {
        Width = Width, Height = Height, EntryIndex = (int[])EntryIndex.Clone(), Shades = (Shade[])Shades.Clone(),
        Entries = [.. Entries], Preview = Preview.Clone(), Mode = Mode, Step = Step, UpStep = UpStep, DownStep = DownStep, ParityShading = ParityShading,
    };
}

/// <summary>Converts an image into block map colours (ColorQuantizer, design 12.1). Deterministic for the same input.</summary>
public static partial class ColorQuantizer
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
        return Distribute(result, selection.Mixing);
    }

    /// <summary>
    /// Splits each colour between the selected blocks that share it. Afterwards every entry is a single block, so
    /// layouts, material lists and structures see the actual block per pixel.
    /// Balanced: equal shares in reading order (map by map, then row by row) so each block covers one stretch.
    /// Mixed: equal shares scattered with a deterministic per-pixel hash.
    /// </summary>
    public static QuantizeResult Distribute(QuantizeResult q, BlockMixing mixing)
    {
        if (q.Entries.All(e => e.AllMembers.Count <= 1)) return q;
        var flat = new List<PaletteEntry>();
        var memberIndex = new int[q.Entries.Count][];
        for (int e = 0; e < q.Entries.Count; e++)
        {
            var entry = q.Entries[e];
            memberIndex[e] = entry.AllMembers.Select(m =>
            {
                var others = entry.AllMembers.Where(o => o != m).Concat(entry.Alternatives).ToList();
                flat.Add(new PaletteEntry(m, others));
                return flat.Count - 1;
            }).ToArray();
        }

        int w = q.Width, h = q.Height;
        var idx = new int[q.EntryIndex.Length];
        if (mixing == BlockMixing.Mixed)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, e = q.EntryIndex[i];
                    idx[i] = e < 0 ? -1 : memberIndex[e][(int)(Jitter(x, y) * memberIndex[e].Length)];
                }
        }
        else
        {
            // Reading order: maps left to right, top to bottom; inside a map row by row.
            IEnumerable<int> Order()
            {
                for (int my = 0; my * 128 < h; my++)
                    for (int mx = 0; mx * 128 < w; mx++)
                        for (int y = my * 128; y < Math.Min(h, my * 128 + 128); y++)
                            for (int x = mx * 128; x < Math.Min(w, mx * 128 + 128); x++)
                                yield return y * w + x;
            }
            var total = new int[q.Entries.Count];
            foreach (int e in q.EntryIndex) if (e >= 0) total[e]++;
            var seen = new int[q.Entries.Count];
            foreach (int i in Order())
            {
                int e = q.EntryIndex[i];
                if (e < 0) { idx[i] = -1; continue; }
                int m = memberIndex[e].Length;
                idx[i] = memberIndex[e][Math.Min(m - 1, (int)((long)seen[e]++ * m / total[e]))];
            }
        }
        return new QuantizeResult
        {
            Width = w, Height = h, EntryIndex = idx, Shades = q.Shades, Entries = flat, Preview = q.Preview, Mode = q.Mode, Step = q.Step, UpStep = q.UpStep, DownStep = q.DownStep, ParityShading = q.ParityShading,
        };
    }

    private static QuantizeResult QuantizeWith(RgbaImage image, ColorProfile profile, List<PaletteEntry> entries,
        QuantizeOptions options, CancellationToken ct)
    {
        // The height limit counts blocks, the run limit counts light / dark steps: with a measured rule that needs a
        // difference of 2 per step, a limit of 8 blocks allows runs of 4. A limit below one step means flat.
        int step = profile.Shades.Step;
        var resultMode = options.Mode;
        if (options.HeightLimited)
        {
            int runs = options.MaxHeight / step;
            options = runs == 0 ? options with { Mode = StaircaseMode.Flat, MaxHeight = 0 } : options with { MaxHeight = runs };
        }
        var candidates = BuildCandidates(profile, entries, options.Mode);
        int w = image.Width, h = image.Height, n = w * h;
        var idx = new int[n];
        var shades = new Shade[n];
        var preview = new RgbaImage(w, h);
        var chosen = new int[n];
        Array.Fill(chosen, -1);

        void Assign(int i, int c)
        {
            chosen[i] = c;
            var cand = candidates[c];
            idx[i] = cand.Entry;
            shades[i] = cand.Shade;
            preview.Pixels[i * 4] = cand.Color.R;
            preview.Pixels[i * 4 + 1] = cand.Color.G;
            preview.Pixels[i * 4 + 2] = cand.Color.B;
            preview.Pixels[i * 4 + 3] = 255;
        }

        bool IsEmpty(int i) => image.Pixels[i * 4 + 3] < options.AlphaThreshold;

        // Optimised: start from linear-light Floyd–Steinberg, then optimise with two-colour mixes (docs/benchmarks.md).
        // The strength slider trades mixing (truer colours) against calm, sharp pixels.
        if (options.Dither == DitherMode.Optimized)
        {
            double calm = 1 - Math.Clamp(options.DitherStrength, 0, 1);
            options = options with
            {
                Dither = DitherMode.FloydSteinberg, DitherStrength = 1,
                Refine = options.Refine ?? new RefineSettings
                {
                    PixelWeight = 0.1 + calm * 0.3, PerceptualWeight = 0.2, PairLambda = 0.1 + calm * 0.3, Neighbours = 24,
                },
            };
        }
        var ordered = OrderedMatrix(options.Dither);
        var knoll = KnollMatrix(options.Dither);
        if (options.HeightLimited && (options.Dither == DitherMode.None || ordered != null || knoll != null))
        {
            HeightLimitedOptimal(image, w, h, options, candidates, ordered, IsEmpty, idx, shades, Assign, ct, knoll);
        }
        else if (knoll != null)
        {
            int size = (int)Math.Sqrt(knoll.Length);
            Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, () => new NearestMatcher(candidates, options.Metric), (y, _, local) =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (IsEmpty(i)) { idx[i] = -1; shades[i] = Shade.Normal; continue; }
                    double t = (knoll[(y % size) * size + x % size] + 0.5) / knoll.Length;
                    Assign(i, local.KnollPick(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2], AllShades, options, t));
                }
                return local;
            }, _ => { });
        }
        else if (options.Dither == DitherMode.None || ordered != null)
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
                    Assign(i, local.Find(r, g, b, AllShades, options.ShadePenalty));
                }
                return local;
            }, _ => { });
        }
        else
        {
            ErrorDiffuse(image, w, h, options, new NearestMatcher(candidates, options.Metric), candidates, IsEmpty, idx, shades, Assign, ct);
        }

        if (options.Refine is { } refine)
        {
            // With a height limit or a shade penalty only same-shade swaps are allowed, so the staircase stays as planned.
            Refine(image, w, h, candidates, chosen, options.HeightLimited || options.ShadePenalty > 0, refine, ct);
            for (int i = 0; i < n; i++) if (chosen[i] >= 0) Assign(i, chosen[i]);
        }

        return new QuantizeResult
        {
            Width = w, Height = h, EntryIndex = idx, Shades = shades, Entries = entries, Preview = preview,
            Mode = options.Mode == StaircaseMode.Flat ? StaircaseMode.Flat : resultMode, Step = step,
            UpStep = profile.Shades.BuildUp, DownStep = profile.Shades.BuildDown,
            ParityShading = profile.Shades.Model == BMAP.Shading.ShadingModelKind.Checker,
        };
    }

    private const int AllShades = 0b111;

    /// <summary>Deterministic pseudo-random value in [0, 1) per pixel (so results stay reproducible).</summary>
    private static double Jitter(int x, int y)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xFFFFFF) / (double)0x1000000;
    }
    private static int Bit(Shade s) => 1 << (int)s;

    /// <summary>
    /// Height-limited staircasing, solved optimally per column (Viterbi). A column needs H+1 height levels when it has
    /// a run of H darker (or H lighter) steps, normal steps not breaking the run, so the state is the current run:
    /// k = H is "no run", k &lt; H a dark run of H-k, k &gt; H a light run of k-H.
    /// </summary>
    private static void HeightLimitedOptimal(RgbaImage image, int w, int h, QuantizeOptions options, List<Candidate> candidates,
        int[]? ordered, Func<int, bool> isEmpty, int[] idx, Shade[] shades, Action<int, int> assign, CancellationToken ct,
        int[]? knoll = null)
    {
        int H = options.MaxHeight, S = 2 * H + 1;
        int size = ordered is null ? 1 : (int)Math.Sqrt(ordered.Length);
        int ksize = knoll is null ? 1 : (int)Math.Sqrt(knoll.Length);
        Parallel.For(0, w, new ParallelOptions { CancellationToken = ct }, () => new NearestMatcher(candidates, options.Metric), (x, _, matcher) =>
        {
            // Per row: best candidate and cost for each shade.
            var bestCand = new int[h * 3];
            var bestCost = new double[h * 3];
            for (int y = 0; y < h; y++)
            {
                int i = y * w + x;
                if (isEmpty(i)) { for (int s = 0; s < 3; s++) { bestCand[y * 3 + s] = -1; bestCost[y * 3 + s] = 0; } continue; }
                double r = image.Pixels[i * 4], g = image.Pixels[i * 4 + 1], b = image.Pixels[i * 4 + 2];
                if (ordered != null)
                {
                    double t = (ordered[(y % size) * size + x % size] + 0.5) / ordered.Length - 0.5;
                    double k = t * 32 * options.DitherStrength;
                    r += k; g += k; b += k;
                }
                for (int s = 0; s < 3; s++)
                {
                    if (knoll != null)
                    {
                        // Knoll: the pattern picks the candidate; the cost compares it with the source colour.
                        double t = (knoll[(y % ksize) * ksize + x % ksize] + 0.5) / knoll.Length;
                        int kc = matcher.KnollPick(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2], Bit((Shade)s), options, t);
                        bestCand[y * 3 + s] = kc;
                        bestCost[y * 3 + s] = kc < 0 ? double.PositiveInfinity : matcher.Cost(r, g, b, kc, options.ShadePenalty);
                        continue;
                    }
                    int c = matcher.Find(r, g, b, Bit((Shade)s), options.ShadePenalty);
                    bestCand[y * 3 + s] = c;
                    bestCost[y * 3 + s] = c < 0 ? double.PositiveInfinity : matcher.Cost(r, g, b, c, options.ShadePenalty);
                }
            }

            var cost = new double[S];
            var next = new double[S];
            var from = new int[h * S];
            var choice = new byte[h * S];
            Array.Fill(cost, double.PositiveInfinity);
            cost[H] = 0; // the support row is a neutral start
            for (int y = 0; y < h; y++)
            {
                Array.Fill(next, double.PositiveInfinity);
                bool empty = bestCand[y * 3 + 1] < 0;
                for (int k = 0; k < S; k++)
                {
                    double c0 = cost[k];
                    if (double.IsPositiveInfinity(c0)) continue;
                    // Normal (or an empty pixel): the run continues unchanged.
                    Relax(k, c0 + (empty ? 0 : bestCost[y * 3 + (int)Shade.Normal]), k, (byte)Shade.Normal);
                    if (empty) continue;
                    // Dark: extends a dark run or starts a new one. A tiny per-pixel jitter breaks exact ties so that
                    // columns in flat areas drop back at different rows (scattered, like dithering) instead of all
                    // at the same row, which would show as a dark horizontal line.
                    int kd = k <= H ? k - 1 : H - 1;
                    if (kd >= 0) Relax(kd, c0 + bestCost[y * 3 + (int)Shade.Dark] + matcher.TieBreak * Jitter(x, y), k, (byte)Shade.Dark);
                    // Light: extends a light run or starts a new one.
                    int kb = k >= H ? k + 1 : H + 1;
                    if (kb < S) Relax(kb, c0 + bestCost[y * 3 + (int)Shade.Bright], k, (byte)Shade.Bright);
                }
                (cost, next) = (next, cost);

                void Relax(int to, double value, int fromState, byte shade)
                {
                    if (value >= next[to]) return;
                    next[to] = value;
                    from[y * S + to] = fromState;
                    choice[y * S + to] = shade;
                }
            }

            int state = 0;
            for (int k = 1; k < S; k++) if (cost[k] < cost[state]) state = k;
            for (int y = h - 1; y >= 0; y--)
            {
                int i = y * w + x;
                var shade = (Shade)choice[y * S + state];
                int c = bestCand[y * 3 + (int)shade];
                if (c < 0) { idx[i] = -1; shades[i] = Shade.Normal; }
                else assign(i, c);
                state = from[y * S + state];
            }
            return matcher;
        }, _ => { });
    }

    /// <summary>Threshold matrix (values 0..n-1) for ordered dithering, or null for error diffusion.</summary>
    internal static int[]? OrderedMatrix(DitherMode mode) => mode switch
    {
        DitherMode.Bayer2 => BayerMatrix(2),
        DitherMode.Bayer4 => BayerMatrix(4),
        DitherMode.Bayer8 => BayerMatrix(8),
        DitherMode.Ordered3 => [0, 6, 3, 4, 7, 2, 5, 1, 8],
        DitherMode.BlueNoise => BlueNoise.Ranks,
        _ => null,
    };

    /// <summary>Threshold matrix for Knoll pattern dithering, or null.</summary>
    internal static int[]? KnollMatrix(DitherMode mode) => mode switch
    {
        DitherMode.KnollBayer => BayerMatrix(8),
        DitherMode.KnollBlueNoise => BlueNoise.Ranks,
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
        bool linear = options.LinearDiffusion;
        // The buffer holds sRGB values, or linear light scaled to 0..255 when diffusing in linear light.
        static double Lin(double srgb) => ColorMath.SrgbToLinear((byte)Math.Clamp(Math.Round(srgb), 0, 255)) * 255;
        var buf = new float[w * h * 3];
        for (int i = 0; i < w * h; i++)
            for (int ch = 0; ch < 3; ch++)
                buf[i * 3 + ch] = linear ? (float)(ColorMath.SrgbToLinear(image.Pixels[i * 4 + ch]) * 255) : image.Pixels[i * 4 + ch];
        double strength = Math.Clamp(options.DitherStrength, 0, 1);
        // Height limit: per column, the current run of darker (<0) or lighter (>0) steps.
        var run = new int[w];
        int H = options.HeightLimited ? options.MaxHeight : int.MaxValue;
        for (int y = 0; y < h; y++)
        {
            if ((y & 15) == 0) ct.ThrowIfCancellationRequested();
            bool reverse = options.Serpentine && (y & 1) == 1;
            for (int step = 0; step < w; step++)
            {
                int x = reverse ? w - 1 - step : step;
                int i = y * w + x;
                if (isEmpty(i)) { idx[i] = -1; shades[i] = Shade.Normal; continue; }
                // The error is computed from a value that may overshoot the gamut a little: clamping exactly at 0..255
                // throws away the error next to very dark / light tones and shifts their brightness, while no bound
                // lets the error of out-of-gamut colours smear across the picture. A margin of 16 measured best
                // (docs/benchmarks.md).
                const double Margin = 16;
                double r = Math.Clamp(buf[i * 3], -Margin, 255 + Margin), g = Math.Clamp(buf[i * 3 + 1], -Margin, 255 + Margin), b = Math.Clamp(buf[i * 3 + 2], -Margin, 255 + Margin);
                // Matching is always done on in-gamut sRGB values; only the error bookkeeping is linear.
                double mr = linear ? ColorMath.LinearToSrgb(r / 255) : Math.Clamp(r, 0, 255);
                double mg = linear ? ColorMath.LinearToSrgb(g / 255) : Math.Clamp(g, 0, 255);
                double mb = linear ? ColorMath.LinearToSrgb(b / 255) : Math.Clamp(b, 0, 255);
                int mask = AllShades;
                if (-run[x] >= H) mask &= ~Bit(Shade.Dark);
                if (run[x] >= H) mask &= ~Bit(Shade.Bright);
                int c = matcher.Find(mr, mg, mb, mask, options.ShadePenalty);
                if (c < 0) c = matcher.Find(mr, mg, mb, AllShades, options.ShadePenalty); // modes without a normal shade
                assign(i, c);
                run[x] = candidates[c].Shade switch
                {
                    Shade.Dark => run[x] < 0 ? run[x] - 1 : -1,
                    Shade.Bright => run[x] > 0 ? run[x] + 1 : 1,
                    _ => run[x],
                };
                var col = candidates[c].Color;
                double cr = linear ? Lin(col.R) : col.R, cg = linear ? Lin(col.G) : col.G, cb = linear ? Lin(col.B) : col.B;
                double er = (r - cr) * strength, eg = (g - cg) * strength, eb = (b - cb) * strength;
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

    /// <summary>
    /// Finds the nearest candidate colour, restricted to a set of shades and with an optional extra cost for the
    /// dark/light shades. Results are cached per (quantised colour, shade mask).
    /// </summary>
    private sealed partial class NearestMatcher
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

        public int Find(double r, double g, double b) => Find(r, g, b, AllShades, 0);

        /// <summary>Nearest candidate whose shade is in <paramref name="shadeMask"/>, or -1 if there is none.</summary>
        public int Find(double r, double g, double b, int shadeMask, double penalty)
        {
            int ri = Q(r), gi = Q(g), bi = Q(b);
            int key = (shadeMask << 24) | (ri << 16) | (gi << 8) | bi;
            bool cacheable = penalty == 0 || _penaltyForCache == penalty;
            if (cacheable && _cache.TryGetValue(key, out int hit)) return hit;

            int best = -1;
            double bestD = double.MaxValue;
            var lab = LabOf(ri, gi, bi);
            for (int i = 0; i < _candidates.Count; i++)
            {
                if ((shadeMask & (1 << (int)_candidates[i].Shade)) == 0) continue;
                double d = Distance(ri, gi, bi, lab, i, penalty);
                if (d < bestD) { bestD = d; best = i; } // strict '<' keeps the first (preferred) candidate on ties
            }
            if (_cache.Count == 0) _penaltyForCache = penalty;
            if (_penaltyForCache == penalty && _cache.Count < 1 << 20) _cache[key] = best;
            return best;
        }

        private double _penaltyForCache = double.NaN;

        /// <summary>The (penalised) distance of a candidate, in the same units <see cref="Find"/> minimises.</summary>
        public double Cost(double r, double g, double b, int candidate, double penalty)
        {
            int ri = Q(r), gi = Q(g), bi = Q(b);
            return Distance(ri, gi, bi, LabOf(ri, gi, bi), candidate, penalty);
        }

        private Lab LabOf(int r, int g, int b) => _metric switch
        {
            ColorMetric.Lab or ColorMetric.Ciede2000 => ColorMath.ToLab(r, g, b),
            ColorMetric.Oklab => ColorMath.ToOklab(r, g, b),
            _ => default,
        };

        private double Distance(int ri, int gi, int bi, Lab lab, int i, double penalty)
        {
            var c = _candidates[i].Color;
            double d = _metric switch
            {
                ColorMetric.Rgb => Sq(ri - c.R) + Sq(gi - c.G) + Sq(bi - c.B),
                ColorMetric.WeightedRgb => ColorMath.WeightedRgbSq(ri, gi, bi, c.R, c.G, c.B),
                ColorMetric.Lab or ColorMetric.Oklab => ColorMath.DeltaE76(lab, _labs[i]),
                _ => ColorMath.DeltaE2000(lab, _labs[i]),
            };
            // A small absolute floor makes the penalty work even for exact matches.
            if (penalty > 0 && _candidates[i].Shade != Shade.Normal) d = d * (1 + penalty) + penalty * Floor;
            return d;
        }

        /// <summary>A cost far below any visible difference, used only to break exact ties.</summary>
        public double TieBreak => Floor * 0.01;

        private double Floor => _metric switch
        {
            ColorMetric.Oklab => 0.0004,
            ColorMetric.Lab => 4,
            ColorMetric.Ciede2000 => 2,
            _ => 100,
        };

        private static int Q(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
        private static double Sq(double v) => v * v;
    }
}