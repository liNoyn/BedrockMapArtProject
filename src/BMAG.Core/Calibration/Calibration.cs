using BMAG.BlockEngine;
using BMAG.ColorEngine;
using BMAG.Imaging;
using BMAG.MapData;
using BMAG.Palette;

namespace BMAG.Calibration;

/// <summary>
/// In-game colour calibration (F20, design 3.3 / 6.6).
/// A 128x128 test pad with an 8x8 swatch per block is built inside one scale-0 map area. Each swatch uses the
/// height pattern 0,0,0,1,1,0,0,0 (north to south) so rows 1-2 show the normal shade, row 3 the bright shade and
/// row 5 the dark shade. After the player has drawn a map of the area, the map is read back from the world and
/// every swatch is sampled, replacing estimated colours with measured ones.
/// </summary>
public static class CalibrationPad
{
    public const int Swatch = 8;
    public const int PerRow = 128 / Swatch;
    public const int MaxBlocks = PerRow * PerRow;
    private static readonly int[] HeightPattern = [0, 0, 0, 1, 1, 0, 0, 0];

    /// <summary>Rows inside a swatch to sample for each shade.</summary>
    public static readonly IReadOnlyDictionary<Shade, int[]> SampleRows = new Dictionary<Shade, int[]>
    {
        [Shade.Normal] = [1, 2],
        [Shade.Bright] = [3],
        [Shade.Dark] = [5],
    };

    /// <summary>Blocks that can be calibrated: falling blocks would collapse the height pattern.</summary>
    public static List<BlockDefinition> CalibratableBlocks(ColorProfile profile) =>
        profile.Blocks.Where(b => !b.Gravity).Take(MaxBlocks).ToList();

    public static BlockLayout CreateLayout(ColorProfile profile, BlockDefinition supportBlock)
    {
        var blocks = CalibratableBlocks(profile);
        var entries = blocks.Select(b => new PaletteEntry(b, [])).ToList();
        const int w = 128, depth = 129;
        var cells = new int[w * depth];
        var heights = new int[w * depth];
        for (int x = 0; x < w; x++) cells[x] = BlockLayout.SupportEntry;
        for (int r = 0; r < 128; r++)
            for (int x = 0; x < w; x++)
            {
                int slot = r / Swatch * PerRow + x / Swatch;
                int i = (r + 1) * w + x;
                if (slot < entries.Count)
                {
                    cells[i] = slot;
                    heights[i] = HeightPattern[r % Swatch];
                }
                else cells[i] = BlockLayout.SupportEntry; // unused slots: flat filler
            }
        return new BlockLayout
        {
            Width = w, Depth = depth, Cells = cells, Heights = heights, Entries = entries,
            SupportBlock = supportBlock, Mode = BuildMode.Staircase,
        };
    }
}

public sealed record CalibrationSample(BlockDefinition Block, Shade Shade, Rgba Estimated, Rgba? Measured, double DeltaE, string? Problem);

public static class Calibrator
{
    /// <summary>True if the map is a scale-0 overworld map whose area is exactly map grid cell (gridX, gridZ).</summary>
    public static bool CoversGridCell(MapRecord map, int gridX, int gridZ) =>
        map.Scale == 0 && map.Dimension == 0 && map.XCenter == 128 * gridX && map.ZCenter == 128 * gridZ;

    public static List<CalibrationSample> Measure(MapRecord map, ColorProfile profile)
    {
        var img = map.ToImage() ?? throw new InvalidDataException("地図の色データを読み取れません。");
        if (img.Width != 128 || img.Height != 128) throw new InvalidDataException("128x128 の地図ではありません。");
        var blocks = CalibrationPad.CalibratableBlocks(profile);
        var result = new List<CalibrationSample>();
        for (int slot = 0; slot < blocks.Count; slot++)
        {
            int sx = slot % CalibrationPad.PerRow * CalibrationPad.Swatch;
            int sz = slot / CalibrationPad.PerRow * CalibrationPad.Swatch;
            foreach (var (shade, rows) in CalibrationPad.SampleRows)
            {
                var pixels = new List<Rgba>();
                foreach (int row in rows)
                    for (int dx = 2; dx <= 5; dx++)
                        pixels.Add(img[sx + dx, sz + row]);
                var est = profile.ColorOf(blocks[slot], shade);
                if (pixels.Any(p => p.A < 255))
                {
                    result.Add(new(blocks[slot], shade, est, null, double.NaN, "未探索（透明）のピクセルがあります"));
                    continue;
                }
                var distinct = pixels.Distinct().ToList();
                var measured = distinct.OrderByDescending(c => pixels.Count(p => p == c)).First(); // mode
                string? problem = distinct.Count > 2 ? "サンプルの色がばらついています（位置ずれ・影・植生の可能性）" : null;
                double de = ColorMath.DeltaE2000(ColorMath.ToLab(est.R, est.G, est.B), ColorMath.ToLab(measured.R, measured.G, measured.B));
                result.Add(new(blocks[slot], shade, est, measured, de, problem));
            }
        }
        return result;
    }

    public static Dictionary<string, Dictionary<Shade, string>> ToMeasurements(IEnumerable<CalibrationSample> samples)
    {
        var dict = new Dictionary<string, Dictionary<Shade, string>>();
        foreach (var s in samples)
        {
            if (s.Measured is not { } m || s.Problem != null) continue;
            if (!dict.TryGetValue(s.Block.Key, out var d)) dict[s.Block.Key] = d = [];
            d[s.Shade] = m.ToHex();
        }
        return dict;
    }

    public static string UserProfileDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BMAG", "profiles");

    public static List<ColorProfile> LoadUserProfiles()
    {
        var list = new List<ColorProfile>();
        if (!Directory.Exists(UserProfileDirectory)) return list;
        foreach (var f in Directory.EnumerateFiles(UserProfileDirectory, "*.json"))
        {
            try { list.Add(ColorProfile.LoadFile(f)); } catch { /* ignore broken files */ }
        }
        return list;
    }

    public static string SaveUserProfile(ColorProfile profile)
    {
        Directory.CreateDirectory(UserProfileDirectory);
        string path = Path.Combine(UserProfileDirectory, profile.Id + ".json");
        File.WriteAllText(path, profile.ToJson(), new System.Text.UTF8Encoding(false));
        return path;
    }
}
