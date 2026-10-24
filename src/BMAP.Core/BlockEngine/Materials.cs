using BMAP.Palette;

namespace BMAP.BlockEngine;

/// <summary>One line of the material list (design 7.2).</summary>
public sealed class MaterialLine
{
    public required BlockDefinition Block { get; init; }
    public int Count { get; set; }
    public bool IsSupport { get; init; }
    public SortedSet<int> MapNumbers { get; } = [];
    public List<BlockDefinition> Alternatives { get; init; } = [];
    public int Owned { get; set; }

    public int Missing => Math.Max(0, Count - Owned);
    public string Stacks => FormatStacks(Count, Block.MaxStack);
    public string ShulkerBoxes => (Count / (27.0 * Block.MaxStack)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    public string MapsText => string.Join(", ", MapNumbers.Select(n => $"#{n}"));
    public string AlternativesText => string.Join(", ", Alternatives.Select(a => a.DisplayName));

    public static string FormatStacks(int count, int stack)
    {
        int s = count / stack, rest = count % stack;
        if (s == 0) return $"{rest}";
        return rest == 0 ? $"{s}st" : $"{s}st + {rest}";
    }
}

public static class MaterialCalculator
{
    /// <summary>Counts blocks. Support blocks are listed separately from the art itself.</summary>
    /// <param name="onlyMap">1-based map number to count, or null for everything.</param>
    public static List<MaterialLine> Calculate(BlockLayout layout, int mapsX, int? onlyMap = null)
    {
        var lines = new Dictionary<(string, bool), MaterialLine>();
        foreach (var p in layout.Placements())
        {
            if (onlyMap is int m && MapNumberOf(p.X, p.Z, mapsX) != m) continue;
            if (!lines.TryGetValue((p.Block.Key, p.IsSupport), out var line))
            {
                var entry = layout.Entries.FirstOrDefault(e => e.Block.Key == p.Block.Key);
                line = new MaterialLine
                {
                    Block = p.Block, IsSupport = p.IsSupport,
                    Alternatives = p.IsSupport ? [] : entry?.Alternatives ?? [],
                };
                lines[(p.Block.Key, p.IsSupport)] = line;
            }
            line.Count++;
            line.MapNumbers.Add(MapNumberOf(p.X, p.Z, mapsX));
        }
        return lines.Values.OrderBy(l => l.IsSupport).ThenByDescending(l => l.Count).ToList();
    }

    /// <summary>1-based map number of a layout position (the support row belongs to the top maps).</summary>
    public static int MapNumberOf(int x, int z, int mapsX) => Math.Max(0, z - 1) / 128 * mapsX + x / 128 + 1;

    /// <summary>Progress key of the 16x16 chunk (inside its map) that contains a layout position.</summary>
    public static string ChunkKey(int x, int z, int mapsX) =>
        $"{MapNumberOf(x, z, mapsX)}:{x % 128 / 16}:{Math.Max(0, z - 1) % 128 / 16}";
}
