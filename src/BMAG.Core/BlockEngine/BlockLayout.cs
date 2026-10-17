using BMAG.Palette;

namespace BMAG.BlockEngine;

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
    public required int[] Heights { get; init; }
    public required List<PaletteEntry> Entries { get; init; }
    public required BlockDefinition SupportBlock { get; init; }
    public required BuildMode Mode { get; init; }

    public int MaxHeight => Heights.Length == 0 ? 0 : Heights.Max();
    public int ImageHeight => Depth - 1;

    public int CellAt(int x, int z) => Cells[z * Width + x];
    public int HeightAt(int x, int z) => Heights[z * Width + x];

    public BlockDefinition? BlockAt(int x, int z) => CellAt(x, z) switch
    {
        SupportEntry => SupportBlock,
        Empty => null,
        var e => Entries[e].Block,
    };

    public IEnumerable<BlockPlacement> Placements()
    {
        for (int z = 0; z < Depth; z++)
            for (int x = 0; x < Width; x++)
            {
                var b = BlockAt(x, z);
                if (b is null) continue;
                yield return new BlockPlacement(x, HeightAt(x, z), z, b, z == 0, z == 0 ? -1 : x, z - 1);
            }
    }

    public static BlockLayout Generate(QuantizeResult q, BlockDefinition supportBlock, int maxHeight = 0)
    {
        int w = q.Width, depth = q.Height + 1;
        var cells = new int[w * depth];
        var heights = new int[w * depth];
        bool staircase = q.Shades.Any(s => s != Shade.Normal);

        for (int x = 0; x < w; x++)
        {
            cells[x] = SupportEntry;
            for (int r = 0; r < q.Height; r++) cells[(r + 1) * w + x] = q.Get(x, r);
            if (!staircase) continue;

            // need[z]: minimal height of cell z so that all shades south of it are still reachable with heights >= 0.
            var need = new int[depth];
            for (int z = depth - 2; z >= 0; z--)
            {
                var shadeNext = PixelShade(q, x, z + 1);
                need[z] = shadeNext switch
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

        if (maxHeight > 0 && heights.Length > 0 && heights.Max() > maxHeight)
            throw new InvalidOperationException($"高さ {heights.Max()} が上限 {maxHeight} を超えています。Flat 方式にするか、ディザリングを変更してください。");

        return new BlockLayout
        {
            Width = w, Depth = depth, Cells = cells, Heights = heights, Entries = q.Entries,
            SupportBlock = supportBlock, Mode = staircase ? BuildMode.Staircase : BuildMode.Flat,
        };
    }

    /// <summary>Shade of the pixel at layout row z (z >= 1). Empty pixels keep the previous height.</summary>
    private static Shade PixelShade(QuantizeResult q, int x, int z) =>
        q.Get(x, z - 1) < 0 ? Shade.Normal : q.GetShade(x, z - 1);
}

/// <summary>One placed block (design 13.4).</summary>
public readonly record struct BlockPlacement(
    int X, int Y, int Z, BlockDefinition Block, bool IsSupport, int SourcePixelX, int SourcePixelY);
