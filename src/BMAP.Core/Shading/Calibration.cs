using System.Text;
using BMAP.Bedrock;
using BMAP.ColorEngine;
using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Shading;

/// <summary>Brightness of one height difference relative to the flat (Δ = 0) colour of the same block.</summary>
public sealed record StepMeasurement(string Case, int Delta, double Ratio, double Spread, int Count);

/// <summary>A block colour read from the map, per shade.</summary>
public sealed record MeasuredColour(string Block, Shade Shade, Rgba Colour, int Count, int Variants);

public sealed class CalibrationResult
{
    public required PadDescriptor Pad { get; init; }
    public required MapRecord Map { get; init; }
    public string GameVersion { get; init; } = "";
    /// <summary>The map as the game drew it, oriented like the pad (north up).</summary>
    public required RgbaImage Measured { get; init; }
    /// <summary>What BMAP predicts for the pad with the current profile.</summary>
    public required RgbaImage Predicted { get; init; }
    /// <summary>Transform applied to the measured image to match the pad (0 = none; otherwise the map is stored rotated or flipped).</summary>
    public int Orientation { get; init; }
    public int OffsetX { get; init; }
    public int OffsetZ { get; init; }
    /// <summary>Sample pixels that were drawn (others are unexplored or outside the map).</summary>
    public int Covered { get; init; }
    public int SampleCount { get; init; }
    /// <summary>Mean / max CIEDE2000 between prediction and map over the covered samples.</summary>
    public double MeanDeltaE { get; init; }
    public double MaxDeltaE { get; init; }
    /// <summary>Samples whose colour matches the prediction exactly.</summary>
    public int Exact { get; init; }

    // Shading pad
    public List<StepMeasurement> Steps { get; init; } = [];
    /// <summary>Smallest height difference that changes the shade (light / dark), or null when it was never seen.</summary>
    public int? BrightStep { get; init; }
    public int? DarkStep { get; init; }
    /// <summary>True when larger steps give a different brightness than the smallest effective step (graded shading).</summary>
    public bool Graded { get; init; }
    public double BrightRatio { get; init; }
    public double DarkRatio { get; init; }
    /// <summary>Normal-shade colour / base colour of the profile (220/255 ≈ 0.863 on Java), from fixed-colour blocks.</summary>
    public double NormalFactor { get; init; }
    /// <summary>Largest deviation of the east/west-only and flat controls from the normal colour.</summary>
    public double ControlDeviation { get; init; }

    /// <summary>Multipliers fitted as integers (base x m / 255 rounded down), 0 when not measured.</summary>
    public int FittedDark { get; init; }
    public int FittedNormal { get; init; }
    public int FittedBright { get; init; }
    /// <summary>Smallest step that changed the shade on at least some pixels (e.g. on every other one).</summary>
    public int? PartialBrightStep { get; init; }
    public int? PartialDarkStep { get; init; }

    /// <summary>Candidate shading models scored against the measurements (best first).</summary>
    public List<ModelScore> Models { get; init; } = [];
    public ModelScore? BestModel { get; init; }
    /// <summary>Brightness of ±1 / ±2 steps on even and odd pixels.</summary>
    public List<ParityMeasurement> Parity { get; init; } = [];

    // Colour pad
    public List<MeasuredColour> Colours { get; init; } = [];

    public List<string> Notes { get; init; } = [];

    /// <summary>The shading rule the measurements support (null when the pad was not a shading pad or data is missing).</summary>
    public ShadeMultipliers? MeasuredRule(string gameVersion)
    {
        if (Pad.Kind != PadKind.Shading || BrightStep is null || DarkStep is null || NormalFactor <= 0) return null;
        int normal = FittedNormal > 0 ? FittedNormal : (int)Math.Round(NormalFactor * 255);
        var best = BestModel?.Model;
        // Light and dark steps are kept separately; they are not merged into one threshold.
        return new ShadeMultipliers
        {
            Normal = normal,
            Bright = FittedBright > 0 ? FittedBright : Math.Min(255, (int)Math.Round(normal * BrightRatio)),
            Dark = FittedDark > 0 ? FittedDark : (int)Math.Round(normal * DarkRatio),
            MinStep = Math.Max(BrightStep.Value, DarkStep.Value),
            BrightStep = BrightStep.Value,
            DarkStep = DarkStep.Value,
            Model = best?.Kind ?? ShadingModelKind.Threshold,
            Rounding = best?.Rounding ?? ShadeRounding.FromBase,
            ModelId = best?.Id ?? "",
            Verified = true,
            Status = ColorStatus.Verified,
            StepStatus = ColorStatus.Verified,
            StatusNote = $"Bedrock {gameVersion}, shading pad, map {Map.Id}, {DateTime.Now:yyyy-MM-dd}, best model {best?.Id ?? "?"}"
                + (BestModel is { } s ? $" (validation {s.ValidationAccuracy:P1}, exact {s.ExactRate:P1})" : "") + (Graded ? ", graded shading seen" : ""),
        };
    }

    /// <summary>Block colours per shade for <see cref="ColorProfile.WithMeasurements"/>.</summary>
    public Dictionary<string, Dictionary<Shade, string>> Measurements() =>
        Colours.Where(c => c.Variants <= 2)
            .GroupBy(c => c.Block)
            .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Shade, c => c.Colour.ToHex()));
}

/// <summary>
/// Reads back a calibration pad from a map the game drew (read only: the world database is copied first) and compares
/// it with BMAP's prediction. Nothing is assumed about the rule: brightness is measured for every height difference and
/// the rule is derived from the numbers.
/// </summary>
public static class Calibrator
{
    private const double Tolerance = 0.02;

    /// <summary>The scale-0 overworld map that covers the pad best (by its world position, or by comparison).</summary>
    public static MapRecord? FindMap(IEnumerable<MapRecord> maps, PadDescriptor pad, RgbaImage predicted)
    {
        var candidates = maps.Where(m => m.Scale == 0 && m.Dimension == 0 && m.ToImage() != null).ToList();
        if (pad.OriginX is int ox && pad.OriginZ is int oz)
        {
            var exact = candidates.FirstOrDefault(m => m.MinX == ox && m.MinZ == oz);
            if (exact != null) return exact;
            var overlapping = candidates.Where(m => Math.Abs(m.MinX - ox) < 128 && Math.Abs(m.MinZ - oz) < 128)
                .OrderBy(m => Math.Abs(m.MinX - ox) + Math.Abs(m.MinZ - oz)).FirstOrDefault();
            if (overlapping != null) return overlapping;
        }
        // Unknown position: the map that looks most like the prediction.
        return candidates.OrderBy(m => Score(m.ToImage()!, predicted, pad, 0, 0)).FirstOrDefault();
    }

    public static CalibrationResult Analyze(PadDescriptor pad, MapRecord map, ColorProfile profile, string gameVersion = "", bool english = false)
    {
        var cal = ShadingTestPad.Recreate(pad, profile);
        var predicted = MapSimulator.Simulate(cal.Layout, profile).Image;
        var raw = map.ToImage() ?? throw new InvalidDataException("The map has no colour data.");
        string T(string ja, string en) => english ? en : ja;
        var notes = new List<string>();

        int offX = pad.OriginX is int ox ? ox - map.MinX : 0;
        int offZ = pad.OriginZ is int oz ? oz - map.MinZ : 0;
        if (map.Scale != 0) notes.Add(T("縮尺 0 の地図ではありません。", "The map is not scale 0."));
        if (offX != 0 || offZ != 0)
            notes.Add(T($"パッドは地図の範囲から ({offX}, {offZ}) ずれています。重なった部分だけを比べます。",
                $"The pad is offset by ({offX}, {offZ}) from the map area; only the overlap is compared."));

        // Try all 8 orientations; normally the identity wins. Anything else means the map is stored rotated/flipped.
        int best = 0;
        double bestScore = double.MaxValue;
        for (int o = 0; o < 8; o++)
        {
            double s = Score(Orient(raw, o), predicted, pad, offX, offZ);
            if (s < bestScore - 1e-9) { bestScore = s; best = o; }
        }
        if (best != 0)
            notes.Add(T($"地図の画像は向き {best}（回転・反転）で保存されていました。補正して比べます。",
                $"The map image was stored with orientation {best} (rotated / flipped); corrected before comparing."));
        var img = Orient(raw, best);

        // Sample colours.
        var measured = new Dictionary<PadSample, Rgba>();
        foreach (var s in pad.Samples)
        {
            int mx = s.X + offX, my = s.Row + offZ;
            if (mx < 0 || my < 0 || mx >= img.Width || my >= img.Height) continue;
            var c = img[mx, my];
            if (c.A == 0) continue; // not explored yet
            measured[s] = c;
        }
        if (measured.Count < pad.Samples.Count)
            notes.Add(T($"{pad.Samples.Count - measured.Count} 個の測定点が地図に描かれていません（未探索・範囲外）。地図を持って全体を歩き回ってください。",
                $"{pad.Samples.Count - measured.Count} sample points are not drawn (unexplored or outside). Walk over the whole pad with the map."));

        double sum = 0, max = 0;
        int exact = 0;
        foreach (var (s, c) in measured)
        {
            var p = predicted[s.X, s.Row];
            double de = ColorMath.DeltaE2000(ColorMath.ToLab(c.R, c.G, c.B), ColorMath.ToLab(p.R, p.G, p.B));
            sum += de;
            max = Math.Max(max, de);
            if (c.R == p.R && c.G == p.G && c.B == p.B) exact++;
        }

        var result = new CalibrationResult
        {
            Pad = pad, Map = map, GameVersion = gameVersion, Measured = img, Predicted = predicted, Orientation = best,
            OffsetX = offX, OffsetZ = offZ, Covered = measured.Count, SampleCount = pad.Samples.Count,
            MeanDeltaE = measured.Count == 0 ? 0 : sum / measured.Count, MaxDeltaE = max, Exact = exact, Notes = notes,
        };
        return pad.Kind == PadKind.Shading
            ? AnalyzeShading(result, measured, profile, T)
            : AnalyzeColours(result, measured, profile, T);
    }

    private static CalibrationResult AnalyzeShading(CalibrationResult r, Dictionary<PadSample, Rgba> measured, ColorProfile profile,
        Func<string, string, string> T)
    {
        // Normal reference per group: samples of the group with Δ = 0.
        var reference = measured.Where(kv => kv.Key.Delta == 0)
            .GroupBy(kv => kv.Key.Group)
            .ToDictionary(g => g.Key, g => Median(g.Select(kv => kv.Value)));
        var steps = new List<StepMeasurement>();
        foreach (var group in measured.Where(kv => kv.Key.Delta is not null && reference.ContainsKey(kv.Key.Group))
                     .GroupBy(kv => (kv.Key.Case, Delta: kv.Key.Delta!.Value)))
        {
            var ratios = group.Select(kv => Ratio(kv.Value, reference[kv.Key.Group])).Where(x => !double.IsNaN(x)).ToList();
            if (ratios.Count == 0) continue;
            double mean = ratios.Average();
            steps.Add(new StepMeasurement(group.Key.Case, group.Key.Delta, mean, ratios.Max() - ratios.Min(), ratios.Count));
        }
        steps = steps.OrderBy(s => s.Case).ThenBy(s => s.Delta).ToList();

        var full = steps.Where(s => s.Case == "full").ToDictionary(s => s.Delta);
        // Classify every full-block sample against the flat colour of its strip. A step can change the shade on every
        // pixel ("reliable") or only on some (e.g. alternating by coordinate parity); both are reported.
        var classified = measured.Where(kv => kv.Key.Case == "full" && kv.Key.Delta is not null && reference.ContainsKey(kv.Key.Group))
            .Select(kv => (S: kv.Key, C: kv.Value, R: Ratio(kv.Value, reference[kv.Key.Group])))
            .Where(t => !double.IsNaN(t.R))
            .Select(t => (t.S, t.C, t.R, Class: t.R > 1 + Tolerance ? Shade.Bright : t.R < 1 - Tolerance ? Shade.Dark : Shade.Normal))
            .ToList();
        double Share(int delta, Shade cls) =>
            classified.Where(t => t.S.Delta == delta) is var g && g.Any() ? g.Count(t => t.Class == cls) / (double)g.Count() : 0;
        int? Reliable(int sign, Shade cls) => Enumerable.Range(1, 4).Where(k => Share(sign * k, cls) >= 0.95).Select(k => (int?)k).FirstOrDefault();
        int? Partial(int sign, Shade cls) => Enumerable.Range(1, 4).Where(k => Share(sign * k, cls) >= 0.05).Select(k => (int?)k).FirstOrDefault();
        int? brightStep = Reliable(+1, Shade.Bright), darkStep = Reliable(-1, Shade.Dark);
        int? brightPartial = Partial(+1, Shade.Bright), darkPartial = Partial(-1, Shade.Dark);
        // Brightness of shaded pixels only (mixing light and normal pixels of a partial step would give a false value).
        double MeanRatio(Shade cls) => classified.Where(t => t.Class == cls).Select(t => t.R).DefaultIfEmpty(1).Average();
        double brightRatio = MeanRatio(Shade.Bright), darkRatio = MeanRatio(Shade.Dark);
        // Graded: shaded pixels are brighter / darker for larger steps.
        bool Spread(Shade cls) => classified.Where(t => t.Class == cls).GroupBy(t => Math.Abs(t.S.Delta!.Value))
            .Select(g => g.Average(t => t.R)).ToList() is { Count: > 1 } m && m.Max() - m.Min() > Tolerance;
        bool graded = Spread(Shade.Bright) || Spread(Shade.Dark);
        // Multipliers: the integer m with base x m / 255 (rounded down) equal to the measured colour for most samples.
        (int M, double Rate) Fit(Shade cls, IEnumerable<(PadSample S, Rgba C)> src)
        {
            var list = src.Select(t => (t.C, Block: profile.Find(t.S.Block))).Where(t => t.Block is { BiomeTinted: false })
                .Select(t => (t.C, Base: t.Block!.BaseColor)).ToList();
            if (list.Count == 0) return (0, 0);
            var best = Enumerable.Range(100, 156).Select(m => (M: m, N: list.Count(t =>
                t.Base.R * m / 255 == t.C.R && t.Base.G * m / 255 == t.C.G && t.Base.B * m / 255 == t.C.B)))
                .OrderByDescending(x => x.N).ThenBy(x => Math.Abs(x.M - (cls == Shade.Dark ? 180 : cls == Shade.Bright ? 255 : 220))).First();
            return (best.M, best.N / (double)list.Count);
        }
        var fitNormal = Fit(Shade.Normal, classified.Where(t => t.S.Delta == 0).Select(t => (t.S, t.C)));
        var fitBright = Fit(Shade.Bright, classified.Where(t => t.Class == Shade.Bright).Select(t => (t.S, t.C)));
        var fitDark = Fit(Shade.Dark, classified.Where(t => t.Class == Shade.Dark).Select(t => (t.S, t.C)));

        // Normal colour / profile base colour, from fixed-colour full blocks.
        var factors = measured.Where(kv => kv.Key.Delta == 0 && kv.Key.Case is "full" or "flat")
            .Select(kv => (kv.Value, Block: profile.Find(kv.Key.Block)))
            .Where(t => t.Block is { BiomeTinted: false })
            .Select(t => Ratio(t.Value, t.Block!.BaseColor))
            .Where(x => !double.IsNaN(x)).ToList();
        double normalFactor = factors.Count == 0 ? 0 : factors.Order().ElementAt(factors.Count / 2);

        double control = steps.Where(s => s.Case is "east-west" or "flat").Select(s => Math.Abs(s.Ratio - 1)).DefaultIfEmpty(0).Max();

        var notes = r.Notes;
        if (measured.Count == 0)
            return new CalibrationResult
            {
                Pad = r.Pad, Map = r.Map, GameVersion = r.GameVersion, Measured = r.Measured, Predicted = r.Predicted,
                Orientation = r.Orientation, OffsetX = r.OffsetX, OffsetZ = r.OffsetZ, Covered = 0, SampleCount = r.SampleCount, Notes = notes,
            };
        notes.Add(brightStep is int b2
            ? T($"必ず明るくなる最小の段差: +{b2}（明 / 通常 = {brightRatio:0.000}）", $"Smallest step that always gives the light shade: +{b2} (light / normal = {brightRatio:0.000})")
            : T("必ず明るくなる段差が見つかりませんでした。", "No step always produced the light shade."));
        if (brightPartial is int bp && bp != brightStep)
            notes.Add(T($"段差 +{bp} では {Share(bp, Shade.Bright):P0} のピクセルだけが明るくなりました。", $"Step +{bp} made only {Share(bp, Shade.Bright):P0} of the pixels light."));
        notes.Add(darkStep is int d2
            ? T($"必ず暗くなる最小の段差: −{d2}（暗 / 通常 = {darkRatio:0.000}）", $"Smallest step that always gives the dark shade: −{d2} (dark / normal = {darkRatio:0.000})")
            : T("必ず暗くなる段差が見つかりませんでした。", "No step always produced the dark shade."));
        if (darkPartial is int dp && dp != darkStep)
            notes.Add(T($"段差 −{dp} では {Share(-dp, Shade.Dark):P0} のピクセルだけが暗くなりました。", $"Step −{dp} made only {Share(-dp, Shade.Dark):P0} of the pixels dark."));
        if (fitNormal.M > 0)
            notes.Add(T($"倍率（基本色 × m ÷ 255 の切り捨てで一致する m）: 暗 {fitDark.M}（{fitDark.Rate:P0}）・通常 {fitNormal.M}（{fitNormal.Rate:P0}）・明 {fitBright.M}（{fitBright.Rate:P0}）",
                $"Multipliers (m with base x m / 255 rounded down matching): dark {fitDark.M} ({fitDark.Rate:P0}), normal {fitNormal.M} ({fitNormal.Rate:P0}), light {fitBright.M} ({fitBright.Rate:P0})"));
        if (graded) notes.Add(T("段差の大きさで明るさが変わっています（段階的な陰影）。", "The brightness changes with the size of the step (graded shading)."));
        if (control > Tolerance)
            notes.Add(T($"対照実験（東西の段差・平ら）が通常色から {control:P1} ずれています。東西の高さも影響している可能性があります。",
                $"Controls (east/west steps, flat) deviate from the normal colour by {control:P1}; east/west heights may matter too."));
        else notes.Add(T("東西だけの段差は陰影に影響しませんでした。", "East/west-only steps did not change the shade."));
        if (normalFactor > 0)
            notes.Add(T($"通常色 / 基本色 = {normalFactor:0.000}（220/255 = 0.863）", $"Normal / base colour = {normalFactor:0.000} (220/255 = 0.863)"));
        foreach (var c in new[] { "carpet", "slab" })
        {
            var cs = steps.Where(s => s.Case == c).ToList();
            if (cs.Count == 0) continue;
            double diff = cs.Where(s => full.ContainsKey(s.Delta)).Select(s => Math.Abs(s.Ratio - full[s.Delta].Ratio)).DefaultIfEmpty(0).Max();
            notes.Add(diff > Tolerance
                ? T($"{c} は通常のブロックと違う陰影になりました（最大 {diff:P1} の差）。", $"{c} shades differently from full blocks (up to {diff:P1}).")
                : T($"{c} も通常のブロックと同じ規則でした。", $"{c} follows the same rule as full blocks."));
        }

        // Candidate models (Java, step 2, gd-codes rounding, checkerboard, graded, and the fitted thresholds).
        var eval = ModelEvaluation.Evaluate(measured, profile);
        if (eval.Best is { } bestScore)
            notes.Add(T($"最も一致したモデル: {bestScore.Model.Name}（検証データの分類一致 {bestScore.ValidationAccuracy:P1}・RGB 完全一致 {bestScore.ExactRate:P1}）",
                $"Best matching model: {bestScore.Model.Id} (validation class accuracy {bestScore.ValidationAccuracy:P1}, exact RGB {bestScore.ExactRate:P1})"));
        foreach (var p in eval.Parity.Where(p => !double.IsNaN(p.EvenRatio) && !double.IsNaN(p.OddRatio) && Math.Abs(p.EvenRatio - p.OddRatio) > Tolerance))
            notes.Add(T($"段差 {p.Delta:+0;-0} で偶数・奇数の座標の明るさが違います（{p.EvenRatio:0.000} / {p.OddRatio:0.000}）: 座標の偶奇による補正があります。",
                $"Step {p.Delta:+0;-0} differs between even and odd pixels ({p.EvenRatio:0.000} / {p.OddRatio:0.000}): a parity correction exists."));
        if (brightStep is int bsA && darkStep is int dsA && bsA != dsA)
            notes.Add(T($"明と暗で必要な段差が違います（明 +{bsA}・暗 −{dsA}）。別々の値として保存します。",
                $"Light and dark need different steps (+{bsA} / −{dsA}); stored separately."));

        return new CalibrationResult
        {
            Pad = r.Pad, Map = r.Map, GameVersion = r.GameVersion, Measured = r.Measured, Predicted = r.Predicted,
            Orientation = r.Orientation, OffsetX = r.OffsetX, OffsetZ = r.OffsetZ, Covered = r.Covered, SampleCount = r.SampleCount,
            MeanDeltaE = r.MeanDeltaE, MaxDeltaE = r.MaxDeltaE, Exact = r.Exact, Notes = notes,
            Steps = steps, BrightStep = brightStep, DarkStep = darkStep, Graded = graded, BrightRatio = brightRatio,
            DarkRatio = darkRatio, NormalFactor = normalFactor, ControlDeviation = control,
            FittedDark = fitDark.M, FittedNormal = fitNormal.M, FittedBright = fitBright.M,
            PartialBrightStep = brightPartial, PartialDarkStep = darkPartial,
            Models = eval.Scores.OrderByDescending(s => s.ValidationAccuracy).ThenByDescending(s => s.ExactRate).ToList(),
            BestModel = eval.Best, Parity = eval.Parity,
        };
    }

    private static CalibrationResult AnalyzeColours(CalibrationResult r, Dictionary<PadSample, Rgba> measured, ColorProfile profile,
        Func<string, string, string> T)
    {
        var colours = new List<MeasuredColour>();
        foreach (var g in measured.Where(kv => kv.Key.Intended is not null).GroupBy(kv => (kv.Key.Block, Shade: kv.Key.Intended!.Value)))
        {
            var values = g.Select(kv => kv.Value with { A = 255 }).ToList();
            var mode = values.GroupBy(v => v).OrderByDescending(x => x.Count()).First();
            colours.Add(new MeasuredColour(g.Key.Block, g.Key.Shade, mode.Key, values.Count, values.Distinct().Count()));
        }
        int noisy = colours.Count(c => c.Variants > 2);
        r.Notes.Add(T($"{colours.Select(c => c.Block).Distinct().Count()} ブロックの色を読み取りました。",
            $"Read the colours of {colours.Select(c => c.Block).Distinct().Count()} blocks."));
        if (noisy > 0)
            r.Notes.Add(T($"{noisy} 個は色がばらついたため使いません（バイオーム・影・位置ずれの可能性）。",
                $"{noisy} were inconsistent and are not used (biome, shadow or misalignment)."));
        return new CalibrationResult
        {
            Pad = r.Pad, Map = r.Map, GameVersion = r.GameVersion, Measured = r.Measured, Predicted = r.Predicted,
            Orientation = r.Orientation, OffsetX = r.OffsetX, OffsetZ = r.OffsetZ, Covered = r.Covered, SampleCount = r.SampleCount,
            MeanDeltaE = r.MeanDeltaE, MaxDeltaE = r.MaxDeltaE, Exact = r.Exact, Notes = r.Notes, Colours = colours,
        };
    }

    /// <summary>Report text for the UI / CLI.</summary>
    public static string Report(CalibrationResult r, bool english = false)
    {
        string T(string ja, string en) => english ? en : ja;
        var sb = new StringBuilder();
        sb.AppendLine(T($"地図 map_{r.Map.Id}（中心 {r.Map.XCenter}, {r.Map.ZCenter}・縮尺 {r.Map.Scale}）／ゲーム {r.GameVersion}",
            $"Map map_{r.Map.Id} (centre {r.Map.XCenter}, {r.Map.ZCenter}, scale {r.Map.Scale}) / game {r.GameVersion}"));
        sb.AppendLine(T($"測定点 {r.Covered}/{r.SampleCount}、予測と完全一致 {r.Exact}、色差 ΔE2000 平均 {r.MeanDeltaE:0.00}・最大 {r.MaxDeltaE:0.00}",
            $"Samples {r.Covered}/{r.SampleCount}, exactly as predicted {r.Exact}, ΔE2000 mean {r.MeanDeltaE:0.00}, max {r.MaxDeltaE:0.00}"));
        foreach (var n in r.Notes) sb.AppendLine("• " + n);
        if (r.Models.Count > 0)
        {
            sb.AppendLine(T("モデルの比較（分類一致: 全体 / 検証、RGB 完全一致、平均誤差、最大誤差）:",
                "Models (class accuracy: all / validation, exact RGB, mean error, max error):"));
            foreach (var s in r.Models)
                sb.AppendLine($"  {s.Model.Id,-16} {s.ClassAccuracy,7:P1} / {s.ValidationAccuracy,7:P1}  {s.ExactRate,7:P1}  {s.MeanAbsError,6:0.00}  {s.MaxError,4}");
        }
        if (r.Parity.Count > 0)
            sb.AppendLine(T("偶奇別の明るさ（偶数 / 奇数）: ", "Brightness on even / odd pixels: ") +
                string.Join("  ", r.Parity.Select(p => $"{p.Delta:+0;-0}: {p.EvenRatio:0.000}/{p.OddRatio:0.000}")));
        if (r.Steps.Count > 0)
        {
            sb.AppendLine(T("段差ごとの明るさ（通常 = 1.000）:", "Brightness per step (normal = 1.000):"));
            foreach (var g in r.Steps.GroupBy(s => s.Case))
                sb.AppendLine($"  {g.Key,-11} " + string.Join("  ", g.Select(s => $"{(s.Delta > 0 ? "+" : "")}{s.Delta}:{s.Ratio:0.000}")));
        }
        return sb.ToString();
    }

    /// <summary>Side-by-side picture: map as drawn | prediction | difference (×4).</summary>
    public static RgbaImage ComparisonImage(CalibrationResult r)
    {
        int w = 128, h = 128;
        var img = new RgbaImage(w * 3 + 8, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int mx = x + r.OffsetX, my = y + r.OffsetZ;
                var m = mx >= 0 && my >= 0 && mx < r.Measured.Width && my < r.Measured.Height ? r.Measured[mx, my] : default;
                var p = y < r.Predicted.Height ? r.Predicted[x, y] : default;
                img[x, y] = m with { A = 255 };
                img[w + 4 + x, y] = p with { A = 255 };
                int d = m.A == 0 ? 0 : Math.Min(255, (Math.Abs(m.R - p.R) + Math.Abs(m.G - p.G) + Math.Abs(m.B - p.B)) * 4);
                img[2 * w + 8 + x, y] = new Rgba((byte)d, (byte)d, (byte)d);
            }
        return img.ScaleNearest(4);
    }

    /// <summary>
    /// Checks the colours of drawn maps (any terrain) against the profile's colour model: every map pixel should be a
    /// base colour times one of the multipliers. Needs no knowledge of the blocks, so any explored map is evidence.
    /// Returns (drawn pixels, pixels explained per multiplier, unexplained colours with counts).
    /// </summary>
    public static (int Drawn, Dictionary<int, int> PerMultiplier, List<(Rgba Colour, int Count)> Unexplained) PaletteCheck(
        IEnumerable<RgbaImage> maps, IEnumerable<Rgba> baseColours, int[] multipliers)
    {
        var lookup = new Dictionary<Rgba, int>();
        foreach (var b in baseColours.Distinct())
            foreach (int m in multipliers)
                lookup.TryAdd(new Rgba((byte)(b.R * m / 255), (byte)(b.G * m / 255), (byte)(b.B * m / 255)), m);
        int drawn = 0;
        var per = multipliers.ToDictionary(m => m, _ => 0);
        var unexplained = new Dictionary<Rgba, int>();
        foreach (var img in maps)
            for (int i = 0; i < img.Width * img.Height; i++)
            {
                if (img.Pixels[i * 4 + 3] == 0) continue;
                drawn++;
                var c = new Rgba(img.Pixels[i * 4], img.Pixels[i * 4 + 1], img.Pixels[i * 4 + 2]);
                if (lookup.TryGetValue(c, out int m)) per[m]++;
                else unexplained[c] = unexplained.GetValueOrDefault(c) + 1;
            }
        return (drawn, per, unexplained.Select(kv => (kv.Key, kv.Value)).OrderByDescending(t => t.Value).ToList());
    }

    /// <summary>
    /// Which rounding explains the light and dark pixels of drawn maps: Java's base × m / 255 or gd-codes' rounding from
    /// the normal colour. Normal pixels are the same in both and are not counted. Pixels explained by exactly one model
    /// decide between them.
    /// </summary>
    public static (int Shaded, int Both, int OnlyFromBase, int OnlyFromNormal, int Neither) RoundingCheck(
        IEnumerable<RgbaImage> maps, IEnumerable<Rgba> baseColours, ShadeMultipliers m)
    {
        var fromBase = new HashSet<Rgba>();
        var fromNormal = new HashSet<Rgba>();
        var normal = new HashSet<Rgba>();
        foreach (var b in baseColours.Distinct())
        {
            normal.Add(ShadingModel.Shaded(b, Shade.Normal, m, ShadeRounding.FromBase));
            foreach (var s in new[] { Shade.Dark, Shade.Bright })
            {
                fromBase.Add(ShadingModel.Shaded(b, s, m, ShadeRounding.FromBase));
                fromNormal.Add(ShadingModel.Shaded(b, s, m, ShadeRounding.FromNormal));
            }
        }
        int shaded = 0, both = 0, onlyBase = 0, onlyNormal = 0, neither = 0;
        foreach (var img in maps)
            for (int i = 0; i < img.Width * img.Height; i++)
            {
                if (img.Pixels[i * 4 + 3] == 0) continue;
                var c = new Rgba(img.Pixels[i * 4], img.Pixels[i * 4 + 1], img.Pixels[i * 4 + 2]);
                bool a = fromBase.Contains(c), n = fromNormal.Contains(c);
                if (normal.Contains(c) && !a && !n) continue; // a normal pixel
                if (!a && !n) { if (!normal.Contains(c)) neither++; continue; }
                shaded++;
                if (a && n) both++; else if (a) onlyBase++; else onlyNormal++;
            }
        return (shaded, both, onlyBase, onlyNormal, neither);
    }

    // ---------------------------------------------------------------- helpers

    private static double Score(RgbaImage measured, RgbaImage predicted, PadDescriptor pad, int offX, int offZ)
    {
        double sum = 0;
        int n = 0;
        foreach (var s in pad.Samples)
        {
            int mx = s.X + offX, my = s.Row + offZ;
            if (mx < 0 || my < 0 || mx >= measured.Width || my >= measured.Height) continue;
            var c = measured[mx, my];
            if (c.A == 0) continue;
            var p = predicted[s.X, s.Row];
            sum += Math.Abs(c.R - p.R) + Math.Abs(c.G - p.G) + Math.Abs(c.B - p.B);
            n++;
        }
        return n == 0 ? double.MaxValue : sum / n;
    }

    /// <summary>One of the 8 rotations / reflections of a square image.</summary>
    public static RgbaImage Orient(RgbaImage img, int o)
    {
        if (o == 0) return img;
        int n = img.Width;
        var dst = new RgbaImage(n, n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int sx = x, sy = y;
                if ((o & 4) != 0) sx = n - 1 - sx;                         // mirror
                for (int k = 0; k < (o & 3); k++) (sx, sy) = (sy, n - 1 - sx); // rotate 90°
                dst[x, y] = img[sx, sy];
            }
        return dst;
    }

    /// <summary>Mean of the per-channel ratios a / b (channels below 8 are ignored as too dark to be reliable).</summary>
    private static double Ratio(Rgba a, Rgba b)
    {
        double s = 0;
        int n = 0;
        if (b.R >= 8) { s += a.R / (double)b.R; n++; }
        if (b.G >= 8) { s += a.G / (double)b.G; n++; }
        if (b.B >= 8) { s += a.B / (double)b.B; n++; }
        return n == 0 ? double.NaN : s / n;
    }

    private static Rgba Median(IEnumerable<Rgba> colours)
    {
        var list = colours.ToList();
        byte M(Func<Rgba, byte> f) => list.Select(f).Order().ElementAt(list.Count / 2);
        return new Rgba(M(c => c.R), M(c => c.G), M(c => c.B));
    }
}
