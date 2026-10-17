using BMAG.Palette;

namespace BMAG.BlockEngine;

/// <summary>Where extra support blocks go (MapartCraft's "add blocks under" option).</summary>
public enum SupportMode
{
    /// <summary>No extra blocks (falling blocks fall, pressure plates pop off).</summary>
    None,
    /// <summary>One block under every block that needs support. Good for pasting.</summary>
    Important,
    /// <summary>
    /// Survival building: under blocks that need support plus the blocks north and south of that support, and under
    /// blocks where the height changes so staircases can be built from either side.
    /// </summary>
    AllOptimized,
    /// <summary>A full copy of the art one layer lower, made of support blocks, plus height-change supports.</summary>
    AllDouble,
}

/// <summary>
/// The 3D block arrangement (BlockLayoutGenerator, design 6.5 - 6.7).
/// Layout coordinates: X = east (image column), Z = south. Row Z=0 is the support row ("noobline") just north of
/// the map area that gives the first image row its shade; image row r is at Z = r + 1. Y = up, minimum 0.
/// </summary>
public sealed class BlockLayout
{
    public const int SupportEntry = -2;
    public const int Empty = -1;

    public required int Width { get; init; }
    /// <summary>Image height + 1 (support row).</summary>
    public required int Depth { get; init; }
    /// <summary>Per (x, z): entry index into <see cref="Entries"/>, <see cref="SupportEntry"/> or <see cref="Empty"/>.</summary>
    public required int[] Cells { get; init; }
    /// <summary>Height of the visible (top) block per (x, z).</summary>
    public required int[] Heights { get; init; }
    public required List<PaletteEntry> Entries { get; init; }
    public required BlockDefinition SupportBlock { get; init; }
    public required StaircaseMode Mode { get; init; }
    public SupportMode Supports { get; init; }
    /// <summary>Extra support block positions (x, y, z) below the visible blocks.</summary>
    public HashSet<(int X, int Y, int Z)> SupportPositions { get; init; } = [];

    private List<BlockPlacement>? _placements;

    public int MaxHeight => Placements().Count == 0 ? 0 : Placements().Max(p => p.Y);
    public int ImageHeight => Depth - 1;
    public bool IsFlat => Mode == StaircaseMode.Flat;

    public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Depth;
    /// <summary>Cell content; positions outside the layout are <see cref="Empty"/>.</summary>
    public int CellAt(int x, int z) => InBounds(x, z) ? Cells[z * Width + x] : Empty;
    public int HeightAt(int x, int z) => InBounds(x, z) ? Heights[z * Width + x] : 0;

    public BlockDefinition? BlockAt(int x, int z) => CellAt(x, z) switch
    {
        SupportEntry => SupportBlock,
        Empty => null,
        var e => Entries[e].Block,
    };

    /// <summary>All blocks: the visible layer first (row by row), then the support blocks.</summary>
    public IReadOnlyList<BlockPlacement> Placements()
    {
        if (_placements != null) return _placements;
        var list = new List<BlockPlacement>(Width * Depth + SupportPositions.Count);
        for (int z = 0; z < Depth; z++)
            for (int x = 0; x < Width; x++)
            {
                var b = BlockAt(x, z);
                if (b is null) continue;
                list.Add(new BlockPlacement(x, HeightAt(x, z), z, b, z == 0, x, z - 1));
            }
        foreach (var (x, y, z) in SupportPositions.OrderBy(p => p.Z).ThenBy(p => p.X).ThenBy(p => p.Y))
            list.Add(new BlockPlacement(x, y, z, SupportBlock, true, x, z - 1));
        return _placements = list;
    }

    public static BlockLayout Generate(QuantizeResult q, BlockDefinition supportBlock, int maxHeight = 0,
        SupportMode supports = SupportMode.None, StaircaseMode? mode = null)
    {
        var staircase = mode ?? q.Mode;
        int w = q.Width, depth = q.Height + 1;
        var cells = new int[w * depth];
        var heights = new int[w * depth];

        for (int x = 0; x < w; x++)
        {
            cells[x] = SupportEntry;
            for (int r = 0; r < q.Height; r++) cells[(r + 1) * w + x] = q.Get(x, r);
            if (staircase == StaircaseMode.Flat) continue;
            if (staircase == StaircaseMode.Classic) ClassicColumn(q, x, w, depth, heights);
            else ValleyColumn(q, x, w, depth, heights);
        }

        var supportSet = new HashSet<(int, int, int)>();
        if (supports != SupportMode.None)
        {
            BlockDefinition? Block(int x, int z) => cells[z * w + x] switch
            {
                SupportEntry => supportBlock,
                Empty => null,
                var e => q.Entries[e].Block,
            };
            bool Occupied(int x, int y, int z) => z >= 0 && z < depth && Block(x, z) != null && heights[z * w + x] == y;
            void Add(int x, int y, int z)
            {
                if (z < 0 || z >= depth || Occupied(x, y, z)) return;
                supportSet.Add((x, y, z));
            }

            for (int x = 0; x < w; x++)
                for (int z = 0; z < depth; z++)
                {
                    var b = Block(x, z);
                    if (b is null) continue;
                    int y = heights[z * w + x];
                    bool needs = b.NeedsSupport;
                    switch (supports)
                    {
                        case SupportMode.Important:
                            if (needs) Add(x, y - 1, z);
                            break;
                        case SupportMode.AllOptimized:
                            if (needs) { Add(x, y - 1, z - 1); Add(x, y - 1, z); Add(x, y - 1, z + 1); }
                            if (z >= 1 && Block(x, z - 1) != null && y > heights[(z - 1) * w + x]) Add(x, y - 1, z); // higher than north
                            if (z + 1 < depth && Block(x, z + 1) != null && heights[(z + 1) * w + x] < y) Add(x, y - 1, z); // south is lower
                            break;
                        case SupportMode.AllDouble:
                            Add(x, y - 1, z);
                            if (z + 1 < depth && Block(x, z + 1) != null && heights[(z + 1) * w + x] < y) Add(x, y - 1, z);
                            break;
                    }
                }
        }

        // Shift everything up so the lowest block (support or not) is at Y = 0.
        int minY = 0;
        for (int i = 0; i < cells.Length; i++) if (cells[i] != Empty) minY = Math.Min(minY, heights[i]);
        foreach (var p in supportSet) minY = Math.Min(minY, p.Item2);
        if (minY < 0)
        {
            for (int i = 0; i < heights.Length; i++) heights[i] -= minY;
            supportSet = supportSet.Select(p => (p.Item1, p.Item2 - minY, p.Item3)).ToHashSet();
        }

        var layout = new BlockLayout
        {
            Width = w, Depth = depth, Cells = cells, Heights = heights, Entries = q.Entries, SupportBlock = supportBlock,
            Mode = staircase, Supports = supports, SupportPositions = supportSet,
        };
        if (maxHeight > 0 && layout.MaxHeight > maxHeight)
            throw new InvalidOperationException($"高さ {layout.MaxHeight} が上限 {maxHeight} を超えています。Flat 方式・Valley 方式にするか、ディザリングを変更してください。");
        return layout;
    }

    /// <summary>Shade of the pixel at layout row z (z >= 1). Empty pixels keep the previous height.</summary>
    private static Shade PixelShade(QuantizeResult q, int x, int z) =>
        q.Get(x, z - 1) < 0 ? Shade.Normal : q.GetShade(x, z - 1);

    /// <summary>Running height from the support row, then shifted so the column's lowest block is at 0.</summary>
    private static void ClassicColumn(QuantizeResult q, int x, int w, int depth, int[] heights)
    {
        int h = 0, min = 0;
        heights[x] = 0;
        for (int z = 1; z < depth; z++)
        {
            h += PixelShade(q, x, z) switch { Shade.Bright => 1, Shade.Dark => -1, _ => 0 };
            heights[z * w + x] = h;
            min = Math.Min(min, h);
        }
        for (int z = 0; z < depth; z++) heights[z * w + x] -= min;
    }

    /// <summary>Lowest heights that still give every pixel its shade ("valley" staircasing).</summary>
    private static void ValleyColumn(QuantizeResult q, int x, int w, int depth, int[] heights)
    {
        // need[z]: minimal height of cell z so that all shades south of it are still reachable with heights >= 0.
        var need = new int[depth];
        for (int z = depth - 2; z >= 0; z--)
        {
            need[z] = PixelShade(q, x, z + 1) switch
            {
                Shade.Dark => need[z + 1] + 1,
                Shade.Normal => need[z + 1],
                _ => 0,
            };
        }
        heights[x] = need[0];
        for (int z = 1; z < depth; z++)
        {
            int prev = heights[(z - 1) * w + x];
            heights[z * w + x] = PixelShade(q, x, z) switch
            {
                Shade.Dark => need[z],
                Shade.Normal => prev,
                _ => Math.Max(need[z], prev + 1),
            };
        }
    }
}

/// <summary>One placed block (design 13.4).</summary>
public readonly record struct BlockPlacement(
    int X, int Y, int Z, BlockDefinition Block, bool IsSupport, int SourcePixelX, int SourcePixelY);
