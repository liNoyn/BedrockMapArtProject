using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Shading;

/// <summary>How well one shading model explains the measured pad.</summary>
public sealed record ModelScore(
    ShadingModel Model,
    /// <summary>Share of samples whose light / normal / dark class the model predicts (all samples).</summary>
    double ClassAccuracy,
    /// <summary>
    /// Class accuracy on the validation half (bands 5-8, never used for fitting), counting only samples with a height
    /// difference: flat samples are predicted the same by every model and would hide the differences.
    /// </summary>
    double ValidationAccuracy,
    /// <summary>Share of samples whose RGB the model predicts exactly from the profile's base colours.</summary>
    double ExactRate,
    /// <summary>Mean absolute RGB error per channel, and the largest channel error.</summary>
    double MeanAbsError,
    int MaxError,
    int Samples);

/// <summary>Brightness of ±1 / ±2 steps on even and odd pixels (x + row): a difference means a checkerboard rule.</summary>
public sealed record ParityMeasurement(int Delta, double EvenRatio, double OddRatio, int EvenCount, int OddCount);

/// <summary>
/// Compares candidate shading models with a measured shading pad. Measurements are classified against the flat colour
/// of the same strip, so the comparison of rules does not depend on the (possibly wrong) base colours; the colour metrics
/// do use the profile's base colours.
/// </summary>
public static class ModelEvaluation
{
    private const double Tolerance = 0.02;

    public sealed record Result(List<ModelScore> Scores, ModelScore? Best, ShadingModel? Fitted, List<ParityMeasurement> Parity);

    public static Result Evaluate(IReadOnlyDictionary<PadSample, Rgba> measured, ColorProfile profile)
    {
        var reference = measured.Where(kv => kv.Key.Delta == 0)
            .GroupBy(kv => kv.Key.Group)
            .ToDictionary(g => g.Key, g => Median(g.Select(kv => kv.Value)));
        // Usable samples: a defined northern neighbour, a flat reference in the same strip, no biome tint.
        var samples = measured
            .Where(kv => kv.Key.Delta is not null && reference.ContainsKey(kv.Key.Group) && kv.Key.Case != "tint")
            .Select(kv => (S: kv.Key, C: kv.Value, Class: Classify(Ratio(kv.Value, reference[kv.Key.Group]))))
            .Where(t => t.Class is not null)
            .Select(t => (t.S, t.C, Class: t.Class!.Value, Validation: Band(t.S.Group) >= 4))
            .ToList();
        if (samples.Count == 0) return new Result([], null, null, []);

        // Fit a threshold model (separate light / dark steps) on the training half only.
        var training = samples.Where(s => !s.Validation).ToList();
        int? up = MinStep(training, +1), down = MinStep(training, -1);
        ShadingModel? fitted = up is int u && down is int d
            ? new ShadingModel($"measured-{u}-{d}", $"実測（明 +{u}・暗 −{d}）", ShadingModelKind.Threshold, u, d, ShadeRounding.FromBase, "fitted on bands 1-4")
            : null;

        var candidates = ShadingModel.Candidates.ToList();
        if (fitted != null && candidates.All(c => !(c.Kind == ShadingModelKind.Threshold && c.BrightStep == fitted.BrightStep && c.DarkStep == fitted.DarkStep && c.Rounding == ShadeRounding.FromBase)))
            candidates.Add(fitted);

        var scores = new List<ModelScore>();
        foreach (var m in candidates)
        {
            int right = 0, vRight = 0, vCount = 0, exact = 0, colourCount = 0, maxErr = 0;
            double errSum = 0;
            foreach (var (s, c, cls, validation) in samples)
            {
                var predicted = m.Kind == ShadingModelKind.Graded ? GradedClass(s.Delta!.Value) : m.Classify(s.Delta!.Value, s.X, s.Row);
                bool ok = predicted == cls;
                if (ok) right++;
                if (validation && s.Delta != 0) { vCount++; if (ok) vRight++; }
                if (profile.Find(s.Block) is { BiomeTinted: false } b)
                {
                    var p = m.Colour(b.BaseColor, s.Delta!.Value, s.X, s.Row, profile.Shades);
                    int er = Math.Abs(p.R - c.R), eg = Math.Abs(p.G - c.G), eb = Math.Abs(p.B - c.B);
                    errSum += er + eg + eb;
                    maxErr = Math.Max(maxErr, Math.Max(er, Math.Max(eg, eb)));
                    if (er + eg + eb == 0) exact++;
                    colourCount++;
                }
            }
            scores.Add(new ModelScore(m, right / (double)samples.Count, vCount == 0 ? double.NaN : vRight / (double)vCount,
                colourCount == 0 ? 0 : exact / (double)colourCount, colourCount == 0 ? 0 : errSum / (3.0 * colourCount), maxErr, samples.Count));
        }
        var best = scores.OrderByDescending(s => double.IsNaN(s.ValidationAccuracy) ? s.ClassAccuracy : s.ValidationAccuracy)
            .ThenByDescending(s => s.ExactRate).ThenBy(s => s.MeanAbsError).FirstOrDefault();

        var parity = new List<ParityMeasurement>();
        foreach (int delta in new[] { -2, -1, 1, 2 })
        {
            var group = measured.Where(kv => kv.Key.Delta == delta && kv.Key.Case == "full" && reference.ContainsKey(kv.Key.Group))
                .Select(kv => (Even: ((kv.Key.X + kv.Key.Row) & 1) == 0, R: Ratio(kv.Value, reference[kv.Key.Group])))
                .Where(t => !double.IsNaN(t.R)).ToList();
            var even = group.Where(t => t.Even).Select(t => t.R).ToList();
            var odd = group.Where(t => !t.Even).Select(t => t.R).ToList();
            if (even.Count + odd.Count == 0) continue;
            parity.Add(new ParityMeasurement(delta, even.Count == 0 ? double.NaN : even.Average(), odd.Count == 0 ? double.NaN : odd.Average(), even.Count, odd.Count));
        }
        return new Result(scores, best, fitted, parity);
    }

    private static Shade GradedClass(int dy) => dy > 0 ? Shade.Bright : dy < 0 ? Shade.Dark : Shade.Normal;

    /// <summary>Smallest |Δy| in the direction whose samples are mostly light (+1) / dark (−1).</summary>
    private static int? MinStep(List<(PadSample S, Rgba C, Shade Class, bool Validation)> samples, int sign)
    {
        var target = sign > 0 ? Shade.Bright : Shade.Dark;
        return samples.Where(s => Math.Sign(s.S.Delta!.Value) == sign)
            .GroupBy(s => Math.Abs(s.S.Delta!.Value))
            .Where(g => g.Count(s => s.Class == target) * 2 > g.Count())
            .Select(g => (int?)g.Key).Min();
    }

    private static Shade? Classify(double ratio) =>
        double.IsNaN(ratio) ? null : ratio > 1 + Tolerance ? Shade.Bright : ratio < 1 - Tolerance ? Shade.Dark : Shade.Normal;

    /// <summary>Strip groups are named "b{band}s{strip}".</summary>
    private static int Band(string group) =>
        group.Length > 1 && group[0] == 'b' && int.TryParse(group.AsSpan(1, group.IndexOf('s') - 1), out int b) ? b : 0;

    internal static double Ratio(Rgba a, Rgba b)
    {
        double s = 0;
        int n = 0;
        if (b.R >= 8) { s += a.R / (double)b.R; n++; }
        if (b.G >= 8) { s += a.G / (double)b.G; n++; }
        if (b.B >= 8) { s += a.B / (double)b.B; n++; }
        return n == 0 ? double.NaN : s / n;
    }

    internal static Rgba Median(IEnumerable<Rgba> colours)
    {
        var list = colours.ToList();
        byte M(Func<Rgba, byte> f) => list.Select(f).Order().ElementAt(list.Count / 2);
        return new Rgba(M(c => c.R), M(c => c.G), M(c => c.B));
    }
}
