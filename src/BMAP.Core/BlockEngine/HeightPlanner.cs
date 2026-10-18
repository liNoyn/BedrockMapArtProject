using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.BlockEngine;

/// <summary>
/// Uses a height limit fully. The quantizer limits the number of light / dark steps in a row; how high that makes the
/// build depends on the layout (with the parity rule many steps need only one block). This searches the largest
/// number of steps whose layout still fits the limit, so the extra room is turned into better colours.
/// </summary>
public static class HeightPlanner
{
    public static QuantizeResult Quantize(RgbaImage image, ColorProfile profile, PaletteSelection selection, QuantizeOptions options,
        BlockDefinition supportBlock, SupportMode supports, bool onMapGrid, CancellationToken ct = default)
    {
        int step = profile.Shades.Step;
        bool parity = profile.Shades.Model == Shading.ShadingModelKind.Checker && onMapGrid;
        if (!options.HeightLimited || !parity || step <= 1)
            return ColorQuantizer.Quantize(image, profile, selection, options, ct);

        int limit = options.MaxHeight;
        // MaxHeight / step runs always fit (each step is at most `step` blocks); try more.
        QuantizeResult Run(int runs) => ColorQuantizer.Quantize(image, profile, selection, options with { MaxHeight = runs * step }, ct);
        int Range(QuantizeResult q)
        {
            var layout = BlockLayout.Generate(q, supportBlock, 0, supports, onMapGrid: true);
            int top = 0, bottom = int.MaxValue;
            for (int i = 0; i < layout.Cells.Length; i++)
            {
                if (layout.Cells[i] == BlockLayout.Empty) continue;
                top = Math.Max(top, layout.Heights[i]);
                bottom = Math.Min(bottom, layout.Heights[i]);
            }
            return bottom == int.MaxValue ? 0 : top - bottom;
        }

        // lo runs are known to fit (0 runs = the quantizer's flat result for limits below one step).
        int lo = limit / step, hi = limit;
        var best = lo == 0 ? ColorQuantizer.Quantize(image, profile, selection, options, ct) : Run(lo);
        while (lo < hi)
        {
            ct.ThrowIfCancellationRequested();
            int mid = (lo + hi + 1) / 2;
            var q = Run(mid);
            if (Range(q) <= limit) { best = q; lo = mid; }
            else hi = mid - 1;
        }
        return best;
    }
}
