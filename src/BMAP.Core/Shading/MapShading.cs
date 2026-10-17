using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Shading;

/// <summary>
/// The map shading rule, written down in one place (see docs/shading-rules.md).
/// A scale-0 map pixel shows the top-most block of its column. Its brightness comes only from the height of that
/// block compared with the top-most block of the column directly north of it (Z - 1): at least <c>step</c> higher →
/// bright, at least <c>step</c> lower → dark, otherwise normal. Java Edition uses step 1; the step of the profile
/// (<see cref="ShadeMultipliers.MinStep"/>) is used so a measured Bedrock rule can replace it. East/west neighbours play
/// no part. The first row of a map is compared with the row just outside the map to the north.
/// </summary>
public static class ShadeRule
{
    public static Shade Compare(int height, int northHeight, int step = 1) => Compare(height, northHeight, step, step);

    /// <summary>Separate steps for light and dark (they are measured separately; they need not be equal).</summary>
    public static Shade Compare(int height, int northHeight, int brightStep, int darkStep) =>
        height - northHeight >= Math.Max(1, brightStep) ? Shade.Bright
        : northHeight - height >= Math.Max(1, darkStep) ? Shade.Dark
        : Shade.Normal;
}

/// <summary>What a map pixel shows according to the simulation.</summary>
public enum PixelState : byte
{
    /// <summary>A block whose shade is fully defined by the build.</summary>
    Defined,
    /// <summary>No block in this column: the map shows whatever is below the build (terrain).</summary>
    Empty,
    /// <summary>The column north of this pixel is empty, so its shade depends on the terrain there.</summary>
    DependsOnTerrain,
}

/// <summary>A map drawn from block positions alone, the way the game draws it.</summary>
public sealed class SimulatedMap
{
    public required int Width { get; init; }
    /// <summary>Image rows (layout depth − 1: the support row is not part of the picture).</summary>
    public required int Height { get; init; }
    public required RgbaImage Image { get; init; }
    public required Shade[] Shades { get; init; }
    public required PixelState[] States { get; init; }
    /// <summary>Top-most block of each pixel's column (null when empty).</summary>
    public required BlockDefinition?[] TopBlocks { get; init; }
    /// <summary>Y of the top-most block per pixel (−1 when empty).</summary>
    public required int[] TopY { get; init; }

    public int Index(int x, int row) => row * Width + x;
}

/// <summary>
/// Draws the map that a block layout produces in game, using only the placed blocks (visible blocks, the support row
/// and support blocks). Because it ignores how the layout was planned, comparing its result with the planned colours
/// verifies the whole chain: quantizer shades → heights → supports.
/// </summary>
public static class MapSimulator
{
    public static SimulatedMap Simulate(BlockLayout layout, ColorProfile profile)
    {
        int w = layout.Width, depth = layout.Depth, rows = depth - 1;
        var topY = new int[w * depth];
        var top = new BlockDefinition?[w * depth];
        Array.Fill(topY, int.MinValue);
        foreach (var p in layout.Placements())
        {
            int i = p.Z * w + p.X;
            if (p.Y > topY[i]) { topY[i] = p.Y; top[i] = p.Block; }
        }

        var img = new RgbaImage(w, rows);
        var model = profile.Shades.ShadingModel;
        var shades = new Shade[w * rows];
        var states = new PixelState[w * rows];
        var blocks = new BlockDefinition?[w * rows];
        var ys = new int[w * rows];
        for (int z = 1; z < depth; z++)
            for (int x = 0; x < w; x++)
            {
                int o = (z - 1) * w + x, i = z * w + x, n = (z - 1) * w + x;
                blocks[o] = top[i];
                ys[o] = top[i] is null ? -1 : topY[i];
                if (top[i] is not { } block) { states[o] = PixelState.Empty; shades[o] = Shade.Normal; continue; }
                Shade shade;
                int dy = 0;
                if (top[n] is null) { states[o] = PixelState.DependsOnTerrain; shade = Shade.Normal; }
                else { dy = topY[i] - topY[n]; shade = model.Classify(dy, x, z - 1); }
                shades[o] = shade;
                // Colours come from the given profile (by key), so a layout can be checked against another profile,
                // e.g. one with colours measured in game. Measured colours win; graded models compute from the base.
                var def = profile.Find(block.Key) ?? block;
                img[x, z - 1] = model.Kind == ShadingModelKind.Graded && !def.Measured.ContainsKey(shade)
                    ? model.Colour(def.BaseColor, dy, x, z - 1, profile.Shades)
                    : profile.ColorOf(def, shade);
            }
        return new SimulatedMap { Width = w, Height = rows, Image = img, Shades = shades, States = states, TopBlocks = blocks, TopY = ys };
    }
}

/// <summary>One problem found by the analysis, at layout coordinates.</summary>
public readonly record struct ShadingIssue(int X, int Z, ShadingIssueKind Kind);

public enum ShadingIssueKind
{
    /// <summary>The simulated map colour differs from the planned colour.</summary>
    Mismatch,
    /// <summary>The pixel's shade depends on terrain north of it (empty column).</summary>
    DependsOnTerrain,
    /// <summary>A falling block (sand, gravel, concrete powder…) with nothing below: it will fall and change the map.</summary>
    WillFall,
    /// <summary>A block that pops off without a block below (carpet, pressure plate…).</summary>
    WillBreak,
}

/// <summary>Summary of the shading and buildability analysis.</summary>
public sealed class ShadingReport
{
    public required SimulatedMap Map { get; init; }
    public required List<ShadingIssue> Issues { get; init; }
    public int Pixels { get; init; }
    public int Dark { get; init; }
    public int Normal { get; init; }
    public int Bright { get; init; }
    public int Empty { get; init; }
    public int Mismatches { get; init; }
    public int DependsOnTerrain { get; init; }
    public int WillFall { get; init; }
    public int WillBreak { get; init; }
    /// <summary>Highest block (layout Y) and the column it is in.</summary>
    public int MaxY { get; init; }
    public int TallestX { get; init; }
    public int TallestZ { get; init; }
    /// <summary>Changes of height between north/south neighbours (each is one step of staircase to build).</summary>
    public int Steps { get; init; }
    /// <summary>North/south neighbours that differ by 2 or more (allowed; the size of a drop does not change the shade).</summary>
    public int Drops { get; init; }
    public double MeanHeight { get; init; }
    /// <summary>Number of columns (x) per height of their highest block: index = height.</summary>
    public required int[] ColumnHeights { get; init; }

    public bool IsExact => Mismatches == 0 && WillFall == 0 && WillBreak == 0;
}

public static class ShadingAnalyzer
{
    /// <summary>
    /// Simulates the layout and checks it against the planned colours (<paramref name="planned"/>, normally the
    /// quantizer's preview). Also finds blocks that would fall or break, which would change the map after building.
    /// </summary>
    public static ShadingReport Analyze(BlockLayout layout, ColorProfile profile, RgbaImage? planned)
    {
        var map = MapSimulator.Simulate(layout, profile);
        var issues = new List<ShadingIssue>();
        int w = map.Width, rows = map.Height;
        int dark = 0, normal = 0, bright = 0, empty = 0, mismatch = 0, terrain = 0, fall = 0, brk = 0;
        for (int r = 0; r < rows; r++)
            for (int x = 0; x < w; x++)
            {
                int o = r * w + x;
                switch (map.States[o])
                {
                    case PixelState.Empty: empty++; continue;
                    case PixelState.DependsOnTerrain: terrain++; issues.Add(new(x, r + 1, ShadingIssueKind.DependsOnTerrain)); break;
                }
                switch (map.Shades[o]) { case Shade.Dark: dark++; break; case Shade.Bright: bright++; break; default: normal++; break; }
                if (planned != null && planned.Pixels[o * 4 + 3] != 0 && map.Image[x, r] != planned[x, r])
                {
                    mismatch++;
                    issues.Add(new(x, r + 1, ShadingIssueKind.Mismatch));
                }
            }

        // Blocks that need something under them.
        var columns = new ColumnIndex(layout.Width, layout.Depth, layout.Placements());
        foreach (var p in layout.Placements())
        {
            if (p.Y == 0 || columns.Occupied(p.X, p.Y - 1, p.Z)) continue;
            if (p.Block.Gravity) { fall++; issues.Add(new(p.X, p.Z, ShadingIssueKind.WillFall)); }
            else if (p.Block.NeedsSupport) { brk++; issues.Add(new(p.X, p.Z, ShadingIssueKind.WillBreak)); }
        }

        // Heights.
        int maxY = 0, tx = 0, tz = 0, steps = 0, drops = 0;
        long sum = 0;
        int count = 0;
        var columnMax = new int[w];
        for (int z = 0; z < layout.Depth; z++)
            for (int x = 0; x < w; x++)
            {
                if (layout.CellAt(x, z) == BlockLayout.Empty) continue;
                int y = layout.HeightAt(x, z);
                sum += y; count++;
                columnMax[x] = Math.Max(columnMax[x], y);
                if (y > maxY) { maxY = y; tx = x; tz = z; }
                if (z > 0 && layout.CellAt(x, z - 1) != BlockLayout.Empty)
                {
                    int d = Math.Abs(y - layout.HeightAt(x, z - 1));
                    if (d > 0) steps++;
                    if (d > 1) drops++;
                }
            }
        var hist = new int[maxY + 1];
        foreach (int h in columnMax) hist[Math.Min(h, maxY)]++;

        return new ShadingReport
        {
            Map = map, Issues = issues, Pixels = w * rows, Dark = dark, Normal = normal, Bright = bright, Empty = empty,
            Mismatches = mismatch, DependsOnTerrain = terrain, WillFall = fall, WillBreak = brk,
            MaxY = maxY, TallestX = tx, TallestZ = tz, Steps = steps, Drops = drops,
            MeanHeight = count == 0 ? 0 : sum / (double)count, ColumnHeights = hist,
        };
    }

    /// <summary>
    /// Shade map of the simulated result: dark shade blue, normal grey, bright yellow, with problems marked
    /// (mismatch red, terrain-dependent magenta, falling/breaking blocks orange). Empty pixels stay transparent.
    /// </summary>
    public static RgbaImage RenderShadeMap(ShadingReport report)
    {
        var map = report.Map;
        var img = new RgbaImage(map.Width, map.Height);
        for (int r = 0; r < map.Height; r++)
            for (int x = 0; x < map.Width; x++)
            {
                int o = r * map.Width + x;
                if (map.States[o] == PixelState.Empty) continue;
                // Keep a hint of the picture so the shade map is easy to relate to the art.
                var c = map.Image[x, r];
                int lum = (c.R * 3 + c.G * 6 + c.B) / 10;
                var tint = map.Shades[o] switch
                {
                    Shade.Dark => new Rgba(60, 95, 210),
                    Shade.Bright => new Rgba(250, 210, 70),
                    _ => new Rgba(150, 150, 150),
                };
                img[x, r] = Mix(tint, new Rgba((byte)lum, (byte)lum, (byte)lum), 0.75);
            }
        foreach (var issue in report.Issues)
        {
            int r = issue.Z - 1;
            if (r < 0 || r >= map.Height) continue;
            img[issue.X, r] = issue.Kind switch
            {
                ShadingIssueKind.Mismatch => new Rgba(235, 30, 30),
                ShadingIssueKind.DependsOnTerrain => new Rgba(230, 40, 230),
                _ => new Rgba(255, 140, 0),
            };
        }
        return img;
    }

    private static Rgba Mix(Rgba a, Rgba b, double t) => new(
        (byte)(a.R * t + b.R * (1 - t)), (byte)(a.G * t + b.G * (1 - t)), (byte)(a.B * t + b.B * (1 - t)));
}
