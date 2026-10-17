using BMAG.BlockEngine;
using BMAG.Palette;

namespace BMAG.Shading;

/// <summary>One sample point of the test pad and the shade the rule predicts for it.</summary>
public sealed record PadCase(int X, int Z, Shade Expected, string Description);

public sealed record ShadingPad(BlockLayout Layout, IReadOnlyList<PadCase> Cases, IReadOnlyList<string> Legend);

/// <summary>
/// A 128x128 build (one map) that shows every part of the shading rule side by side: steps of different sizes up and
/// down, staircases, zigzags, columns of different heights next to each other (east/west must not matter), and the
/// same patterns with carpets and slabs. Load it in game, make a map of it and compare with the predicted picture to
/// check the rule on Bedrock without reading any world data.
/// </summary>
public static class ShadingTestPad
{
    private const int StripW = 8, StripD = 16;

    private sealed record Pattern(string Ja, string En, Func<int, int, int> Height);

    private static readonly Pattern[] Patterns =
    [
        new("1 段上がる", "step up 1", (x, z) => z < 8 ? 0 : 1),
        new("3 段上がる", "step up 3", (x, z) => z < 8 ? 0 : 3),
        new("1 段下がる", "step down 1", (x, z) => z < 8 ? 4 : 3),
        new("5 段下がる", "step down 5", (x, z) => z < 8 ? 6 : 1),
        new("階段（上り）", "stairs up", (x, z) => z),
        new("階段（下り）", "stairs down", (x, z) => 15 - z),
        new("ジグザグ", "zigzag", (x, z) => z % 2),
        new("東西の高さ違い（影響なし）", "east/west differ (no effect)", (x, z) => (x % 2) * 3),
    ];

    public static ShadingPad Create(ColorProfile profile, BlockDefinition supportBlock, bool english = false)
    {
        var kinds = new List<BlockDefinition>();
        foreach (var key in new[] { "lime_concrete", "lime_carpet", "granite_slab", "orange_concrete" })
            if (profile.Find(key) is { } b) kinds.Add(b);
        if (kinds.Count == 0) kinds.Add(profile.Blocks.First(b => !b.Gravity && !b.NeedsSupport));

        const int w = 128, depth = 129;
        var entries = kinds.Select(b => new PaletteEntry(b, [])).ToList();
        var cells = new int[w * depth];
        var heights = new int[w * depth];
        var supports = new HashSet<(int X, int Y, int Z)>();
        var cases = new List<PadCase>();
        var legend = new List<string>();
        const int baseY = 2; // room for supports under carpets / slabs at the lowest level

        for (int x = 0; x < w; x++) { cells[x] = BlockLayout.SupportEntry; heights[x] = baseY; }

        int stripsAcross = w / StripW; // 16
        for (int row = 0; row < 128 / StripD; row++)        // 8 rows of strips (north → south)
            for (int col = 0; col < stripsAcross; col++)    // 16 strips per row
            {
                var pattern = Patterns[col % Patterns.Length];
                int kind = row % kinds.Count;
                int x0 = col * StripW, z0 = 1 + row * StripD;
                for (int dz = 0; dz < StripD; dz++)
                    for (int dx = 0; dx < StripW; dx++)
                    {
                        int x = x0 + dx, z = z0 + dz, i = z * w + x;
                        cells[i] = kind;
                        heights[i] = baseY + pattern.Height(dx, dz);
                        if (kinds[kind].NeedsSupport || kinds[kind].Gravity) supports.Add((x, heights[i] - 1, z));
                    }
                if (row == 0)
                    legend.Add($"x {x0}-{x0 + StripW - 1}: {(english ? pattern.En : pattern.Ja)}");
                if (col == 0)
                    legend.Add($"z {z0}-{z0 + StripD - 1}: {kinds[kind].LocalName(english)}");
                // Sample points: inside the strip, away from its first row (which compares with the strip to the north).
                for (int dz = 1; dz < StripD; dz++)
                {
                    int h = pattern.Height(2, dz), north = pattern.Height(2, dz - 1);
                    var expected = ShadeRule.Compare(h, north);
                    if (dz == 1 || h != north)
                        cases.Add(new PadCase(x0 + 2, z0 + dz, expected, $"{pattern.En} / {kinds[kind].Key}"));
                }
            }

        // Supports must not stick up above a neighbouring visible block (they would be drawn on the map).
        var layout = new BlockLayout
        {
            Width = w, Depth = depth, Cells = cells, Heights = heights, Entries = entries, SupportBlock = supportBlock,
            Mode = StaircaseMode.Valley, Supports = SupportMode.Important, SupportPositions = supports,
        };
        legend.Insert(0, english
            ? "Shading test pad (one map, 128 x 128). Each strip is 8 x 16 blocks; carpets and slabs stand on support blocks."
            : "陰影テストパッド（地図 1 枚分 128 x 128）。各帯は 8 x 16 ブロックで、カーペットとハーフブロックは支えの上にあります。");
        return new ShadingPad(layout, cases, legend);
    }
}
