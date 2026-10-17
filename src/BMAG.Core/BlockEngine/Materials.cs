using BMAG.Palette;

namespace BMAG.BlockEngine;

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
    public string ShulkerBoxes => (Count / (27.0 * Block.MaxStack)).ToString("0.00");
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
    public static List<MaterialLine> Calculate(BlockLayout layout, int mapsX)
    {
        var lines = new Dictionary<(string, bool), MaterialLine>();
        foreach (var p in layout.Placements())
        {
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
            int mapX = Math.Max(0, p.SourcePixelX) / 128;
            int mapY = Math.Max(0, p.SourcePixelY) / 128;
            line.MapNumbers.Add(mapY * mapsX + mapX + 1);
        }
        return lines.Values.OrderBy(l => l.IsSupport).ThenByDescending(l => l.Count).ToList();
    }
}
