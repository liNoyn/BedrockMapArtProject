using System.Text.Json;
using System.Text.Json.Serialization;
using BMAP.BlockEngine;
using BMAP.Palette;

namespace BMAP.Shading;

public enum PadKind
{
    /// <summary>Height differences −4 … +4 and controls: measures the shading rule.</summary>
    Shading,
    /// <summary>Every block with a normal, light and dark area: measures the map colours.</summary>
    Colours,
}

/// <summary>One pixel of a pad to measure, in map pixel coordinates (row 0 = first image row, north).</summary>
public sealed record PadSample
{
    public int X { get; init; }
    public int Row { get; init; }
    public string Block { get; init; } = "";
    /// <summary>Height of this column's top block and of the column north of it (layout Y; null = empty).</summary>
    public int Height { get; init; }
    public int? North { get; init; }
    /// <summary>What this sample tests: "full", "carpet", "slab", "east-west", "flat", "north-empty", "tint", "colour".</summary>
    public string Case { get; init; } = "";
    /// <summary>Group of samples that share the same block and reference (normal) area.</summary>
    public string Group { get; init; } = "";
    /// <summary>Shade the sample is meant to show on the colour pad (normal / light / dark); null on the shading pad.</summary>
    public Shade? Intended { get; init; }

    [JsonIgnore] public int? Delta => North is int n ? Height - n : null;
}

/// <summary>Everything needed to read a pad back from a map: written next to the exported structure as pad.json.</summary>
public sealed record PadDescriptor
{
    public string Format { get; init; } = "bmap-calibration-pad";
    public int Version { get; init; } = 1;
    public PadKind Kind { get; init; }
    /// <summary>Height difference used for the light / dark areas of the colour pad.</summary>
    public int Step { get; init; } = 1;
    public string SupportBlock { get; init; } = "cobblestone";
    public string ProfileId { get; init; } = "";
    public string Created { get; init; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    /// <summary>World position of the pad's first image row and column (north-west pixel) and of layout Y = 0.</summary>
    public int? OriginX { get; init; }
    public int? OriginY { get; init; }
    public int? OriginZ { get; init; }
    public List<PadSample> Samples { get; init; } = [];
    public List<string> Legend { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PadDescriptor Load(string path)
    {
        var d = JsonSerializer.Deserialize<PadDescriptor>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty pad.json");
        if (d.Format != "bmap-calibration-pad") throw new InvalidDataException("not a BMAP pad.json");
        return d;
    }
}

public sealed record CalibrationPad(BlockLayout Layout, PadDescriptor Descriptor)
{
    public IReadOnlyList<string> Legend => Descriptor.Legend;
}

/// <summary>
/// Test pads to measure Bedrock's map colours and shading rule from maps the game itself draws (docs/calibration.md).
/// Each pad fills exactly one map (128 × 128 pixels plus the support row to the north).
/// </summary>
public static class ShadingTestPad
{
    private const int StripW = 8, StripD = 16, BaseY = 2;

    /// <summary>
    /// The shading pad. Eight bands of sixteen 8 × 16 strips:
    /// band 0 full blocks, band 1 carpets, band 2 slabs (each in four blocks with steps of 1, 2, 3 and 4: rows go
    /// 0,0,+d,+d,0,0,+d,+d,… so every strip shows +d, −d and 0), band 3 controls (east/west-only differences, flat,
    /// empty column to the north, biome tinted blocks); bands 4-7 repeat bands 0-3 to check that results agree.
    /// </summary>
    public static CalibrationPad Create(ColorProfile profile, BlockDefinition supportBlock, bool english = false)
    {
        string[][] kinds =
        [
            ["lime_concrete", "white_concrete", "black_concrete", "red_concrete"],
            ["lime_carpet", "white_carpet", "black_carpet", "red_carpet"],
            ["granite_slab", "sandstone_slab", "birch_slab", "end_stone_brick_slab"],
        ];
        string[] flat = ["lime_concrete", "white_concrete", "black_concrete", "red_concrete"];
        string[] tint = ["grass_block", "oak_leaves", "jungle_leaves", "birch_leaves"];
        var b = new PadBuilder(profile, supportBlock);
        var legend = new List<string>
        {
            english
                ? "Shading test pad (one map). Rows of each strip go 0,0,+d,+d,0,0,… so each strip shows +d, −d and 0."
                : "陰影テストパッド（地図 1 枚）。各帯の行は 0,0,+d,+d,0,0,… の高さで、+d・−d・0 を 1 本の帯で調べます。",
        };

        for (int band = 0; band < 8; band++)
        {
            int z0 = 1 + band * StripD, kind = band % 4;
            for (int strip = 0; strip < 16; strip++)
            {
                int x0 = strip * StripW;
                if (kind < 3)
                {
                    string key = kinds[kind][strip / 4];
                    int d = strip % 4 + 1;
                    string caseName = kind switch { 0 => "full", 1 => "carpet", _ => "slab" };
                    b.Strip(x0, z0, key, (dx, dz) => dz / 2 % 2 == 1 ? d : 0, caseName, $"b{band}s{strip}");
                }
                else if (strip < 4)
                {
                    int d = strip + 1; // east/west only: columns alternate, every column is flat along z
                    b.Strip(x0, z0, "lime_concrete", (dx, dz) => dx % 2 * d, "east-west", $"b{band}s{strip}", allColumns: true);
                }
                else if (strip < 8) b.Strip(x0, z0, flat[strip - 4], (_, _) => 0, "flat", $"b{band}s{strip}");
                else if (strip < 12) b.Strip(x0, z0, "lime_concrete", (_, _) => 0, "north-empty", $"b{band}s{strip}", emptyEvery: 4);
                else b.Strip(x0, z0, tint[strip - 12], (_, _) => 0, "tint", $"b{band}s{strip}");
            }
        }
        legend.Add(english
            ? "Bands (16 rows each, north → south): full blocks, carpets, slabs, controls; then the same again."
            : "帯（各 16 行、北 → 南）: ブロック・カーペット・ハーフブロック・対照実験、その後に同じものをもう一度。");
        legend.Add(english
            ? "In bands 1-3 strips are grouped by 4 per block; inside a group the step is 1, 2, 3, 4."
            : "帯 1〜3 では 4 本ずつ同じブロックで、段差は 1, 2, 3, 4 の順です。");
        legend.Add(english
            ? "Controls: east/west differences 1-4 (should stay normal), flat blocks, empty column to the north every 4 rows, grass / oak / jungle / birch leaves."
            : "対照実験: 東西だけの段差 1〜4（通常のままのはず）、平らなブロック、4 行ごとに北が空、草・オーク・ジャングル・シラカバの葉。");
        return b.Finish(PadKind.Shading, 1, legend);
    }

    /// <summary>
    /// The colour pad: every block of the profile in a 4 × 8 swatch with heights 0,0,0,+s,+s,0,0,0 (north → south),
    /// so rows 1-2 and 6-7 show the normal colour, row 3 the light and row 5 the dark one. Up to 512 blocks per map.
    /// </summary>
    public static CalibrationPad CreateColourPad(ColorProfile profile, BlockDefinition supportBlock, int step, bool english = false)
    {
        step = Math.Max(1, step);
        var b = new PadBuilder(profile, supportBlock);
        int[] pattern = [0, 0, 0, step, step, 0, 0, 0];
        var blocks = profile.Blocks.Take(32 * 16).ToList();
        for (int i = 0; i < 32 * 16; i++)
        {
            int x0 = i % 32 * 4, z0 = 1 + i / 32 * 8;
            if (i >= blocks.Count) { b.Fill(x0, z0, 4, 8, supportBlock.Key); continue; }
            var key = blocks[i].Key;
            for (int dz = 0; dz < 8; dz++)
                for (int dx = 0; dx < 4; dx++)
                {
                    int z = z0 + dz, x = x0 + dx;
                    b.Place(x, z, key, BaseY + pattern[dz]);
                    if (dx is 1 or 2)
                    {
                        Shade? intended = dz switch { 1 or 2 or 6 or 7 => Shade.Normal, 3 => Shade.Bright, 5 => Shade.Dark, _ => null };
                        if (intended is { } s) b.Sample(x, z, key, "colour", $"c{i}", s);
                    }
                }
        }
        var legend = new List<string>
        {
            english
                ? $"Colour test pad (one map, {blocks.Count} blocks, 4 x 8 swatches, rows 0,0,0,+{step},+{step},0,0,0)."
                : $"色テストパッド（地図 1 枚・{blocks.Count} ブロック・4 x 8 の見本、行の高さ 0,0,0,+{step},+{step},0,0,0）。",
            english ? "Swatches go row by row from the north-west corner in the order of the block list." : "見本は北西の角から行ごとに、ブロック一覧の順に並んでいます。",
        };
        if (profile.Blocks.Count() > blocks.Count)
            legend.Add(english ? $"{profile.Blocks.Count() - blocks.Count} blocks did not fit." : $"{profile.Blocks.Count() - blocks.Count} ブロックは入りませんでした。");
        return b.Finish(PadKind.Colours, step, legend);
    }

    /// <summary>Re-creates the pad a descriptor was written for (same profile blocks, same layout).</summary>
    public static CalibrationPad Recreate(PadDescriptor d, ColorProfile profile)
    {
        var support = profile.Find(d.SupportBlock) ?? profile.Find("cobblestone") ?? profile.Blocks.First();
        return d.Kind == PadKind.Shading ? Create(profile, support) : CreateColourPad(profile, support, d.Step);
    }

    private sealed class PadBuilder(ColorProfile profile, BlockDefinition supportBlock)
    {
        private const int W = 128, Depth = 129;
        private readonly int[] _cells = Enumerable.Repeat(BlockLayout.Empty, W * Depth).ToArray();
        private readonly int[] _heights = new int[W * Depth];
        private readonly List<PaletteEntry> _entries = [];
        private readonly Dictionary<string, int> _entryOf = [];
        private readonly HashSet<(int X, int Y, int Z)> _supports = [];
        private readonly List<PadSample> _samples = [];

        public void Place(int x, int z, string key, int y)
        {
            var block = profile.Find(key) ?? supportBlock;
            if (!_entryOf.TryGetValue(block.Key, out int e))
            {
                e = _entries.Count;
                _entries.Add(new PaletteEntry(block, []));
                _entryOf[block.Key] = e;
            }
            _cells[z * W + x] = e;
            _heights[z * W + x] = y;
            // Everything that could fall or pop off stands on a support block.
            if (block.NeedsSupport || block.Gravity) _supports.Add((x, y - 1, z));
        }

        public void Fill(int x0, int z0, int w, int d, string key)
        {
            for (int z = z0; z < z0 + d; z++)
                for (int x = x0; x < x0 + w; x++) Place(x, z, key, BaseY);
        }

        public void Sample(int x, int z, string key, string caseName, string group, Shade? intended = null)
        {
            int i = z * W + x, n = (z - 1) * W + x;
            bool northEmpty = z == 0 || _cells[n] == BlockLayout.Empty;
            _samples.Add(new PadSample
            {
                X = x, Row = z - 1, Block = profile.Find(key)?.Key ?? key, Height = _heights[i],
                North = northEmpty ? null : _heights[n], Case = caseName, Group = group, Intended = intended,
            });
        }

        /// <summary>One 8 x 16 strip; samples the inner columns (all when <paramref name="allColumns"/>) of rows 1-15.</summary>
        public void Strip(int x0, int z0, string key, Func<int, int, int> height, string caseName, string group,
            bool allColumns = false, int emptyEvery = 0)
        {
            for (int dz = 0; dz < StripD; dz++)
                for (int dx = 0; dx < StripW; dx++)
                {
                    if (emptyEvery > 0 && dz % emptyEvery == emptyEvery - 1) continue; // leave the cell empty
                    Place(x0 + dx, z0 + dz, key, BaseY + height(dx, dz));
                }
            for (int dz = 1; dz < StripD; dz++)
                for (int dx = allColumns ? 0 : 2; dx < (allColumns ? StripW : 6); dx++)
                    if (_cells[(z0 + dz) * W + x0 + dx] != BlockLayout.Empty) Sample(x0 + dx, z0 + dz, key, caseName, group);
        }

        public CalibrationPad Finish(PadKind kind, int step, List<string> legend)
        {
            // Support row (z = 0) at the base height, so the first image row has a defined northern neighbour.
            for (int x = 0; x < W; x++) { _cells[x] = BlockLayout.SupportEntry; _heights[x] = BaseY; }
            var layout = new BlockLayout
            {
                Width = W, Depth = Depth, Cells = _cells, Heights = _heights, Entries = _entries, SupportBlock = supportBlock,
                Mode = StaircaseMode.Valley, Supports = SupportMode.Important, SupportPositions = _supports,
                Step = profile.Shades.Step,
            };
            var d = new PadDescriptor
            {
                Kind = kind, Step = step, SupportBlock = supportBlock.Key, ProfileId = profile.Id,
                Samples = _samples, Legend = legend,
            };
            return new CalibrationPad(layout, d);
        }
    }
}
