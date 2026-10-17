using BMAG.ColorEngine;
using BMAG.Imaging;
using BMAG.Palette;

namespace BMAG.BlockEngine;

/// <summary>Result of converting the image with one height limit.</summary>
public sealed record HeightOption(
    StaircaseMode Mode, int Limit, int MaxHeight, int BuildHeight, double MeanDeltaE, double Quality, int Blocks, bool Recommended)
{
    /// <summary>Limit 0 = unlimited (or flat for <see cref="StaircaseMode.Flat"/>).</summary>
    public bool Unlimited => Limit == 0 && Mode != StaircaseMode.Flat;
}

/// <summary>
/// 3D map art planning: how much colour accuracy each height budget buys for this particular image, which budget
/// is worth it, and where in the world a build of that height fits.
/// </summary>
public static class HeightAdvisor
{
    public static readonly int[] DefaultLimits = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128];

    /// <summary>
    /// Converts the image flat, with each height limit and unlimited, and measures the mean colour error (CIELAB ΔE)
    /// against the processed image. The recommendation is the lowest build whose error is within a small margin of
    /// the best result (a "knee" in the quality/height curve).
    /// </summary>
    public static List<HeightOption> Analyze(RgbaImage image, ColorProfile profile, PaletteSelection selection,
        QuantizeOptions options, BlockDefinition supportBlock, SupportMode supports, IEnumerable<int>? limits = null,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var mode = options.Mode is StaircaseMode.Classic or StaircaseMode.Valley ? options.Mode : StaircaseMode.Valley;
        var raw = new List<(StaircaseMode Mode, int Limit, int MaxHeight, int BuildHeight, double Error, int Blocks)>();

        (int MaxHeight, int BuildHeight, double Error, int Blocks) Run(StaircaseMode m, int limit)
        {
            ct.ThrowIfCancellationRequested();
            var q = ColorQuantizer.Quantize(image, profile, selection, options with { Mode = m, MaxHeight = limit }, ct);
            var layout = BlockLayout.Generate(q, supportBlock, 0, supports);
            int top = 0, bottom = int.MaxValue;
            for (int i = 0; i < layout.Cells.Length; i++)
            {
                if (layout.Cells[i] == BlockLayout.Empty) continue;
                top = Math.Max(top, layout.Heights[i]);
                bottom = Math.Min(bottom, layout.Heights[i]);
            }
            return (bottom == int.MaxValue ? 0 : top - bottom, layout.MaxHeight + 1, PerceivedDeltaE(image, q.Preview), layout.Placements().Count);
        }

        // Flat and unlimited first: limits at or above the unlimited height give the same result and are skipped.
        var flatRun = Run(StaircaseMode.Flat, 0);
        raw.Add((StaircaseMode.Flat, 0, flatRun.MaxHeight, flatRun.BuildHeight, flatRun.Error, flatRun.Blocks));
        var free = Run(mode, 0);
        var todo = (limits ?? DefaultLimits).Distinct().Where(l => l > 0 && l < free.MaxHeight).Order().ToList();
        int done = 2, total = todo.Count + 2;
        progress?.Report(done * 100 / total);
        foreach (int limit in todo)
        {
            var r = Run(mode, limit);
            raw.Add((mode, limit, r.MaxHeight, r.BuildHeight, r.Error, r.Blocks));
            progress?.Report(++done * 100 / total);
        }
        raw.Add((mode, 0, free.MaxHeight, free.BuildHeight, free.Error, free.Blocks));
        double best = raw.Min(r => r.Error);
        double flat = raw.First(r => r.Mode == StaircaseMode.Flat).Error;
        double margin = Math.Max(0.3, best * 0.05);
        var recommended = raw.Where(r => r.Error <= best + margin).OrderBy(r => r.BuildHeight).ThenBy(r => r.Error).First();
        return raw.Select(r => new HeightOption(r.Mode, r.Limit, r.MaxHeight, r.BuildHeight, r.Error,
                Quality: flat <= best ? 100 : Math.Clamp(100 * (flat - r.Error) / (flat - best), 0, 100), r.Blocks,
                Recommended: r == recommended))
            .OrderBy(o => o.Mode == StaircaseMode.Flat ? 0 : 1).ThenBy(o => o.Limit == 0 ? int.MaxValue : o.Limit)
            .ToList();
    }

    /// <summary>
    /// Mean CIELAB ΔE76 after a light blur of both images — roughly how different the map looks from a normal
    /// viewing distance. Unlike a per-pixel comparison this does not punish dithering, which mixes colours on purpose.
    /// </summary>
    public static double PerceivedDeltaE(RgbaImage source, RgbaImage map) => MeanDeltaE(Blur(source), Blur(map));

    /// <summary>3x3 binomial blur (1-2-1), alpha copied.</summary>
    internal static RgbaImage Blur(RgbaImage img)
    {
        int w = img.Width, h = img.Height;
        var dst = new RgbaImage(w, h);
        int[] k = [1, 2, 1];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    int sum = 0, wsum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sx = x + dx, sy = y + dy;
                            if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                            int si = (sy * w + sx) * 4;
                            if (img.Pixels[si + 3] == 0) continue;
                            int wt = k[dx + 1] * k[dy + 1];
                            sum += img.Pixels[si + ch] * wt;
                            wsum += wt;
                        }
                    dst.Pixels[i + ch] = (byte)(wsum == 0 ? 0 : (sum + wsum / 2) / wsum);
                }
                dst.Pixels[i + 3] = img.Pixels[i + 3];
            }
        return dst;
    }

    /// <summary>Mean CIELAB ΔE76 between the source and the predicted map (transparent pixels skipped).</summary>
    public static double MeanDeltaE(RgbaImage source, RgbaImage map)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < source.Pixels.Length; i += 4)
        {
            if (map.Pixels[i + 3] == 0) continue;
            var a = ColorMath.ToLab(source.Pixels[i], source.Pixels[i + 1], source.Pixels[i + 2]);
            var b = ColorMath.ToLab(map.Pixels[i], map.Pixels[i + 1], map.Pixels[i + 2]);
            sum += Math.Sqrt(ColorMath.DeltaE76(a, b));
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }
}

/// <summary>Where a build fits vertically (overworld build limits Y -64 .. 319).</summary>
public sealed record PlacementAdvice(
    int BuildHeight, int MinBaseY, int MaxBaseY, int RecommendedBaseY, bool Fits, int? CurrentBaseY, IReadOnlyList<string> Notes);

public static class PlacementAdvisor
{
    public const int WorldMinY = -64;
    public const int WorldMaxY = 319;
    /// <summary>Ground level of a typical overworld (sea level 62/63); a flat area there is easy to prepare.</summary>
    public const int TypicalGround = 64;

    /// <param name="baseY">World Y of layout Y = 0 that the user chose, if any.</param>
    public static PlacementAdvice Advise(BlockLayout layout, int? baseY, bool english = false)
    {
        int height = layout.MaxHeight + 1;
        int minBase = WorldMinY, maxBase = WorldMaxY - height + 1;
        bool fits = maxBase >= minBase;
        int recommended = !fits ? WorldMinY : Math.Clamp(TypicalGround, minBase, maxBase);
        var notes = new List<string>();
        string T(string ja, string en) => english ? en : ja;

        if (!fits)
            notes.Add(T($"高さ {height} ブロックはワールドの高さ（384）に収まりません。最大の高さを下げてください。",
                $"{height} blocks do not fit into the world height (384). Lower the maximum height."));
        if (baseY is int y)
        {
            if (y < minBase) notes.Add(T($"Y {y} は低すぎます（Y {WorldMinY} 未満）。", $"Y {y} is below the world bottom ({WorldMinY})."));
            if (y + height - 1 > WorldMaxY)
                notes.Add(T($"Y {y} に置くと頂上が Y {y + height - 1} になり、建築上限 {WorldMaxY} を超えます。Y {maxBase} 以下にしてください。",
                    $"At Y {y} the top would be Y {y + height - 1}, above the build limit {WorldMaxY}. Use Y {maxBase} or lower."));
        }
        if (layout.IsFlat)
            notes.Add(T("平面なので高さはどこでも構いません。地図の範囲内に、アートより高い地形・木・建物がないようにしてください。",
                "Flat art works at any height. Make sure nothing in the map area is taller than the art (terrain, trees, buildings)."));
        else
        {
            notes.Add(T("地図は各列で一番上のブロックを描きます。範囲内の地形・木・建物は、アートの一番低い部分より下にしてください（空中に建てるのが確実です）。",
                "Maps draw the top-most block of each column. Keep terrain, trees and buildings in the area below the lowest part of the art (building in the air is safest)."));
            notes.Add(T("1 行目の陰影は北側の支え行で決まります。支え行も必ず置いてください。",
                "The first row's shade depends on the support row to the north. Always place it."));
            if (height > 64)
                notes.Add(T("高い建築は雲（Y 192 付近）と重なると作業しにくくなります。設定で雲を消すと見やすくなります。",
                    "Tall builds reach the clouds around Y 192; turning clouds off makes building easier."));
        }
        return new PlacementAdvice(height, minBase, Math.Max(minBase, maxBase), recommended, fits, baseY, notes);
    }
}
