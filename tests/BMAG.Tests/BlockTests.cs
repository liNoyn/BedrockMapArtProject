using BMAG.BlockEngine;
using BMAG.ColorEngine;
using BMAG.Export;
using BMAG.Imaging;
using BMAG.Palette;

namespace BMAG.Tests;

public class BlockTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();

    private static QuantizeResult Quantize(RgbaImage img, PaletteSelection? sel = null, QuantizeOptions? opt = null) =>
        ColorQuantizer.Quantize(img, Profile, sel ?? new PaletteSelection(), opt ?? new QuantizeOptions());

    [Fact]
    public void BuiltInProfile_LoadsAndIsMarkedUnverified()
    {
        Assert.True(Profile.Blocks.Count > 50);
        Assert.All(Profile.Blocks, b => Assert.False(b.Verified));
        Assert.False(Profile.Shades.Verified);
        Assert.Equal(Profile.Blocks.Count, Profile.Blocks.Select(b => b.Key).Distinct().Count());
    }

    [Fact] // T08
    public void ExactPaletteColour_MapsToThatBlock()
    {
        var white = Profile.Find("white_wool")!;
        var color = Profile.ColorOf(white, Shade.Normal);
        var q = Quantize(RgbaImage.Filled(128, 128, color));
        var block = q.Entries[q.Get(5, 5)].Block;
        Assert.Equal(color, Profile.ColorOf(block, Shade.Normal));
        Assert.Equal(color, q.Preview[5, 5]);
    }

    [Fact] // T09
    public void DisabledBlocks_NeverAppear()
    {
        var allowed = new HashSet<string> { "white_wool", "black_wool", "red_wool" };
        var q = Quantize(TestHelpers.Gradient(128, 128), new PaletteSelection { EnabledKeys = allowed });
        Assert.All(q.EntryIndex, e => Assert.Contains(q.Entries[e].Block.Key, allowed));
    }

    [Fact]
    public void GravityBlocks_ExcludedByDefault()
    {
        var q = Quantize(TestHelpers.Gradient(128, 128));
        Assert.DoesNotContain(q.Entries, e => e.Block.Gravity);
    }

    [Fact]
    public void MaxBlockTypes_IsRespected()
    {
        var q = Quantize(TestHelpers.Gradient(128, 128), new PaletteSelection { MaxBlockTypes = 6 });
        Assert.True(q.EntryIndex.Where(e => e >= 0).Distinct().Count() <= 6);
    }

    [Theory] // T06
    [InlineData(DitherMode.None, ColorMetric.Lab)]
    [InlineData(DitherMode.FloydSteinberg, ColorMetric.Rgb)]
    [InlineData(DitherMode.Bayer4, ColorMetric.WeightedRgb)]
    [InlineData(DitherMode.Atkinson, ColorMetric.Ciede2000)]
    public void Quantize_IsDeterministic(DitherMode dither, ColorMetric metric)
    {
        var img = TestHelpers.Gradient(128, 128);
        var opt = new QuantizeOptions { Dither = dither, Metric = metric };
        Assert.Equal(Quantize(img, opt: opt).EntryIndex, Quantize(img, opt: opt).EntryIndex);
    }

    [Fact] // T10, T11
    public void Flat_MaterialsMatchPlacements_AndNoHeightDifference()
    {
        var q = Quantize(TestHelpers.Gradient(256, 128));
        var layout = BlockLayout.Generate(q, Profile.Find("cobblestone")!);
        Assert.Equal(0, layout.MaxHeight);
        var materials = MaterialCalculator.Calculate(layout, 2);
        Assert.Equal(layout.Placements().Count(), materials.Sum(m => m.Count));
        Assert.Equal(256 * 128, materials.Where(m => !m.IsSupport).Sum(m => m.Count));
        Assert.Equal(256, materials.Where(m => m.IsSupport).Sum(m => m.Count));
    }

    [Fact] // T12 + shades are reproduced by the generated heights
    public void Staircase_HeightsReproduceShades_AndStayNonNegative()
    {
        var q = Quantize(TestHelpers.Gradient(128, 128), opt: new QuantizeOptions { Mode = BuildMode.Staircase, Dither = DitherMode.FloydSteinberg });
        Assert.Contains(q.Shades, s => s != Shade.Normal);
        var layout = BlockLayout.Generate(q, Profile.Find("cobblestone")!);
        Assert.All(layout.Heights, h => Assert.True(h >= 0));
        for (int x = 0; x < 128; x++)
            for (int z = 1; z < layout.Depth; z++)
            {
                int d = layout.HeightAt(x, z) - layout.HeightAt(x, z - 1);
                var expected = d > 0 ? Shade.Bright : d < 0 ? Shade.Dark : Shade.Normal;
                Assert.Equal(expected, q.GetShade(x, z - 1));
            }
        Assert.True(layout.MaxHeight > 1);
        Assert.Throws<InvalidOperationException>(() => BlockLayout.Generate(q, Profile.Find("cobblestone")!, maxHeight: layout.MaxHeight - 1));
    }

    [Fact] // T13, T14
    public void Structures_CoverEverything_AndReload()
    {
        var q = Quantize(TestHelpers.Gradient(256, 256));
        var layout = BlockLayout.Generate(q, Profile.Find("cobblestone")!);
        var tiles = StructureExporter.Split(layout, 2, new StructureExportOptions());
        Assert.Equal(16 + 4, tiles.Count); // 16 art pieces of 64x64 + 4 support-row pieces
        Assert.Empty(StructureExporter.ValidateCoverage(layout, tiles));
        foreach (var t in tiles)
        {
            Assert.True(t.Structure.SizeX <= 64 && t.Structure.SizeZ <= 64);
            var back = McStructure.FromBytes(t.Structure.ToBytes());
            Assert.Equal(t.Structure.Indices, back.Indices);
            Assert.Equal(t.Structure.Palette.Count, back.Palette.Count);
            Assert.Empty(back.Validate());
        }
    }

    [Fact] // T16: image left = west (low X), image top = north (low Z), no mirroring
    public void Structure_Orientation_IsNotMirrored()
    {
        var img = RgbaImage.Filled(128, 128, Profile.ColorOf(Profile.Find("white_wool")!, Shade.Normal));
        var black = Profile.ColorOf(Profile.Find("black_wool")!, Shade.Normal);
        img[3, 0] = black; // near the north-west corner
        var sel = new PaletteSelection { EnabledKeys = ["white_wool", "black_wool", "cobblestone"] };
        var layout = BlockLayout.Generate(Quantize(img, sel), Profile.Find("cobblestone")!);
        var tile = StructureExporter.Split(layout, 1, new StructureExportOptions()).Single(t => t.Id == "t_0_0");
        var s = tile.Structure;
        var p = s.Palette[s.Indices[s.IndexOf(3, 0, 0)]];
        Assert.Equal("minecraft:wool", p.Name);
        Assert.Equal("black", ((BMAG.Nbt.NbtString)p.States["color"]!).Value);
        Assert.Equal(0, tile.OffsetX);
        Assert.Equal(1, tile.OffsetZ); // the support row is at Z = 0
    }

    [Fact]
    public void Export_WritesPackAndReloadsEveryFile()
    {
        var dir = TestHelpers.TempDir();
        var q = Quantize(TestHelpers.Gradient(128, 128));
        var layout = BlockLayout.Generate(q, Profile.Find("cobblestone")!);
        var result = StructureExporter.Export(dir, "Test Art!", layout, 1, new StructureExportOptions(), new WorldOrigin(-64, 64, -65));
        Assert.True(File.Exists(result.McpackPath));
        Assert.True(File.Exists(Path.Combine(result.PackDirectory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.PackDirectory, "functions", "bmag", "test_art", "all.mcfunction")));
        Assert.Equal("bmag_test_art", result.Namespace);
    }

    [Fact]
    public void Ciede2000_MatchesReferenceValue()
    {
        // Sharma et al. test pair 1: expected 2.0425
        var d = ColorMath.DeltaE2000(new Lab(50, 2.6772, -79.7751), new Lab(50, 0, -82.7485));
        Assert.Equal(2.0425, d, 3);
    }
}
