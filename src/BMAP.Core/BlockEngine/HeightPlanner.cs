using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.BlockEngine;

/// <summary>
/// Makes a height limit mean what the user sees: the height of the whole build, support blocks included. The quantizer
/// limits the number of light / dark steps in a row; how high that makes the build depends on the layout (with the
/// parity rule many steps need only one block, supports add a block below). This searches the largest number of steps
/// whose layout still fits the limit, so the room is used fully and never exceeded.
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
        var search = options.Dither == DitherMode.Optimized && options.Refine is null ? options with { Dither = DitherMode.FloydSteinberg, DitherStrength = 1 } : options;
        QuantizeResult Run(QuantizeOptions o, int runs) => ColorQuantizer.Quantize(image, profile, selection,
            runs == 0 ? o with { Mode = StaircaseMode.Flat, MaxHeight = 0 } : o with { MaxHeight = runs * step }, ct);
        int Height(QuantizeResult q) => BlockLayout.Generate(q, supportBlock, 0, supports, onMapGrid: onMapGrid).MaxHeight;

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
        return ReferenceEquals(search, options) ? best : Run(options, lo);
    }
}
