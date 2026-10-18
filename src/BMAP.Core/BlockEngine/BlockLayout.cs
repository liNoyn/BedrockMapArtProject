using BMAP.Palette;

namespace BMAP.BlockEngine;

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
    /// <summary>Height difference of one light / dark step (the shading rule the layout was made for; the larger one).</summary>
    public int Step { get; init; } = 1;
    public int UpStep { get; init; } = 1;
    public int DownStep { get; init; } = 1;
    /// <summary>
    /// Steps depend on the pixel parity (see <see cref="QuantizeResult.ParityShading"/>): light needs +1 where
    /// x + row is odd and +2 where even, dark needs −1 where even and −2 where odd. Only valid when the build is placed on
    /// the map grid (otherwise the parity in the world is unknown and steps of 2 are used).
    /// </summary>
    public bool ParitySteps { get; init; }
    /// <summary>True when the game shades by the parity rule (used to show the right shade in the 3D view).</summary>
    public bool ParityShading { get; init; }

    /// <summary>Height difference needed for a light / dark pixel at layout (x, z).</summary>
    public static int UpAt(int x, int z, int up, bool parity) => parity ? (((x + z - 1) & 1) == 1 ? 1 : 2) : up;
    public static int DownAt(int x, int z, int down, bool parity) => parity ? (((x + z - 1) & 1) == 0 ? 1 : 2) : down;
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

    /// <param name="onMapGrid">
    /// The build will be placed aligned to the map grid (BMAP's world position option), so the pixel parity in the world
    /// is known and parity-dependent single steps can be used.
    /// </param>
    public static BlockLayout Generate(QuantizeResult q, BlockDefinition supportBlock, int maxHeight = 0,
        SupportMode supports = SupportMode.None, StaircaseMode? mode = null, bool onMapGrid = false)
    {
        bool parity = q.ParityShading && onMapGrid;
        var staircase = mode ?? q.Mode;
        int step = Math.Max(1, q.Step);
        int up = Math.Max(1, q.UpStep), down = Math.Max(1, q.DownStep);
        int w = q.Width, depth = q.Height + 1;
        var cells = new int[w * depth];
        var heights = new int[w * depth];

        for (int x = 0; x < w; x++)
        {
            cells[x] = SupportEntry;
            for (int r = 0; r < q.Height; r++) cells[(r + 1) * w + x] = q.Get(x, r);
            if (staircase == StaircaseMode.Flat) continue;
            if (staircase == StaircaseMode.Classic) ClassicColumn(q, x, w, depth, heights, up, down, parity);
            else ValleyColumn(q, x, w, depth, heights, up, down, parity);
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
                // A support must stay below the visible block of its column, otherwise it becomes the top block and
                // changes the map (a neighbour's support in a valley, or a block in a transparent pixel).
                if (Block(x, z) is null || y >= heights[z * w + x]) return;
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
                            // With steps of more than one block, fill the column below up to the lower neighbour
                            // so the staircase can still be climbed.
                            for (int k = 2; k <= step; k++)
                                if (z >= 1 && Block(x, z - 1) != null && y - k >= heights[(z - 1) * w + x]) Add(x, y - k, z);
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
            Mode = staircase, Supports = supports, SupportPositions = supportSet, Step = step, UpStep = up, DownStep = down,
            ParitySteps = parity, ParityShading = q.ParityShading,
        };
        if (maxHeight > 0 && layout.MaxHeight > maxHeight)
            throw new InvalidOperationException($"高さ {layout.MaxHeight} が上限 {maxHeight} を超えています。Flat 方式・Valley 方式にするか、ディザリングを変更してください。");
        return layout;
    }

    /// <summary>Shade of the pixel at layout row z (z >= 1). Empty pixels keep the previous height.</summary>
    private static Shade PixelShade(QuantizeResult q, int x, int z) =>
        q.Get(x, z - 1) < 0 ? Shade.Normal : q.GetShade(x, z - 1);

    /// <summary>Running height from the support row, then shifted so the column's lowest block is at 0.</summary>
    private static void ClassicColumn(QuantizeResult q, int x, int w, int depth, int[] heights, int up, int down, bool parity)
    {
        int h = 0, min = 0;
        heights[x] = 0;
        for (int z = 1; z < depth; z++)
        {
            h += PixelShade(q, x, z) switch { Shade.Bright => UpAt(x, z, up, parity), Shade.Dark => -DownAt(x, z, down, parity), _ => 0 };
            heights[z * w + x] = h;
            min = Math.Min(min, h);
        }
        for (int z = 0; z < depth; z++) heights[z * w + x] -= min;
    }

    /// <summary>Lowest heights that still give every pixel its shade ("valley" staircasing).</summary>
    /// <summary>
    /// Height differences to the north neighbour that give the wanted shade at layout (x, z) under the parity rule:
    /// light needs ≥ +1 (odd) / ≥ +2 (even), dark ≤ −1 (even) / ≤ −2 (odd), and normal allows 0 plus −1 (odd) or +1 (even).
    /// </summary>
    private static (int Lo, int Hi) ParityRange(QuantizeResult q, int x, int z)
    {
        if (q.Get(x, z - 1) < 0) return (0, 0); // empty pixel: keep the height
        bool odd = ((x + z - 1) & 1) == 1;
        return q.GetShade(x, z - 1) switch
        {
            Shade.Bright => (odd ? 1 : 2, int.MaxValue / 4),
            Shade.Dark => (int.MinValue / 4, odd ? -2 : -1),
            _ => odd ? (-1, 0) : (0, 1),
        };
    }

    /// <summary>
    /// Lowest heights for the parity rule: every pixel has a range of allowed differences to its northern neighbour,
    /// so the column can also drift down by one on "normal" pixels. need[z] = the lowest height of cell z from which
    /// the rest of the column is still reachable with heights ≥ 0; then each cell takes the lowest allowed height.
    /// </summary>
    private static void ValleyParityColumn(QuantizeResult q, int x, int w, int depth, int[] heights)
    {
        var need = new int[depth];
        for (int z = depth - 2; z >= 0; z--)
        {
            var (_, hi) = ParityRange(q, x, z + 1);
            need[z] = hi >= int.MaxValue / 8 ? 0 : Math.Max(0, need[z + 1] - hi);
        }
        heights[x] = need[0];
        for (int z = 1; z < depth; z++)
        {
            var (lo, _) = ParityRange(q, x, z);
            int prev = heights[(z - 1) * w + x];
            heights[z * w + x] = lo <= int.MinValue / 8 ? need[z] : Math.Max(need[z], prev + lo);
        }
    }

    private static void ValleyColumn(QuantizeResult q, int x, int w, int depth, int[] heights, int up, int down, bool parity)
    {
        if (parity) { ValleyParityColumn(q, x, w, depth, heights); return; }
        // need[z]: minimal height of cell z so that all shades south of it are still reachable with heights >= 0.
        var need = new int[depth];
        for (int z = depth - 2; z >= 0; z--)
        {
            need[z] = PixelShade(q, x, z + 1) switch
            {
                Shade.Dark => need[z + 1] + DownAt(x, z + 1, down, parity),
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
                _ => Math.Max(need[z], prev + UpAt(x, z, up, parity)),
            };
        }
    }
}

/// <summary>One placed block (design 13.4).</summary>
public readonly record struct BlockPlacement(
    int X, int Y, int Z, BlockDefinition Block, bool IsSupport, int SourcePixelX, int SourcePixelY);
