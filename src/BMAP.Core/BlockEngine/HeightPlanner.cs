using BMAP.Imaging;
using BMAP.ColorEngine;
using BMAP.Palette;

namespace BMAP.BlockEngine;

/// <summary>
/// Makes a height limit mean what the user sees: the height of the whole build, support blocks included. The quantizer
/// limits the number of light / dark steps in a row; how high that makes the build depends on the layout (with the
/// parity rule many steps need only one block, supports add a block below). Searches feasible step budgets and,
/// when material pruning can change the palette, retains the best model-scored feasible render encountered.
/// </summary>
public static class HeightPlanner
{
    public static QuantizeResult Quantize(RgbaImage image, ColorProfile profile, PaletteSelection selection, QuantizeOptions options,
        BlockDefinition supportBlock, SupportMode supports, bool onMapGrid, CancellationToken ct = default)
    {
        if (!options.HeightLimited)
            return ColorQuantizer.Quantize(image, profile, selection, options, ct);

        int step = Math.Max(1, profile.Shades.Step);
        int limit = options.MaxHeight;
        // The optimiser only swaps colours of the same shade under a height limit, so the heights are those of the
        // diffusion it starts from: search with the faster diffusion and optimise once at the end.
        bool usesDiffusionSeed = options.Dither == DitherMode.Optimized ||
            (options.Dither is DitherMode.Perceptual or DitherMode.Hybrid && options.DitherStrength > 0);
        // Material pruning may choose a different palette after refinement, which can change the shade plan.
        var search = usesDiffusionSeed && options.Refine is null && selection.MaxBlockTypes == 0
            ? options with { Dither = DitherMode.FloydSteinberg, DitherStrength = 1 } : options;
        int Height(QuantizeResult q) => BlockLayout.Generate(q, supportBlock, 0, supports, onMapGrid: onMapGrid).MaxHeight;
        List<QuantizeResult>? feasiblePlans=selection.MaxBlockTypes>0?[]:null;
        QuantizeResult Run(QuantizeOptions o,int runs)
        {
            var q=ColorQuantizer.QuantizeForLayout(image,profile,selection,
                runs==0?o with { Mode=StaircaseMode.Flat,MaxHeight=0 }:o with { MaxHeight=runs*step },ct,candidate=>Height(candidate)<=limit);
            if(feasiblePlans is not null && Height(q)<=limit)feasiblePlans.Add(q);
            return q;
        }

        // Each step needs at most `step` blocks, so limit / step runs nearly always fit; supports may add one.
        int lo = limit / step;
        var best = Run(search, lo);
        while (lo > 0 && Height(best) > limit) best = Run(search, --lo);
        int hi = limit;
        while (lo < hi)
        {
            ct.ThrowIfCancellationRequested();
            int mid = (lo + hi + 1) / 2;
            var q = Run(search, mid);
            if (Height(q) <= limit) { best = q; lo = mid; }
            else hi = mid - 1;
        }
        var result = ReferenceEquals(search, options) ? best : Run(options, lo);
        // Verify the final model too, including any palette changes. Prefer a feasible result over an unverified limit.
        while (lo > 0 && Height(result) > limit) result = Run(options, --lo);
        if (Height(result) > limit) throw new InvalidOperationException("The requested height cannot contain the supported layout.");
        // Changing a limited palette can improve a lower shade budget more than a higher one. Keep the best
        // feasible render already encountered rather than assuming more permitted steps always improve quality.
        if(feasiblePlans is not null)
        {
            var importance=QualityScore.PaletteWeights(image,result.Preview,options.Dither,ct);
            foreach(var candidate in feasiblePlans)
                if(!ReferenceEquals(candidate,result) && QualityScore.PreferCandidate(image,candidate.Preview,result.Preview,
                    ColorMetric.Ciede2000,ct,importance))result=candidate;
        }
        return result;
    }
}
