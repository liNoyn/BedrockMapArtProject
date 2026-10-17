using BMAG.BlockEngine;
using BMAG.ColorEngine;
using BMAG.Export;
using BMAG.Imaging;
using BMAG.Palette;
using BMAG.Preview;

namespace BMAG.Tests;

public class BlockTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static readonly List<BlockPreset> Presets = BlockPreset.BuiltIns(Profile);
    private static PaletteSelection Everything => Presets[0].ToSelection();
    private static BlockDefinition Cobble => Profile.Find("cobblestone")!;

    private static QuantizeResult Quantize(RgbaImage img, PaletteSelection? sel = null, QuantizeOptions? opt = null) =>
        ColorQuantizer.Quantize(img, Profile, sel ?? Everything, opt ?? new QuantizeOptions());

    private static PaletteSelection Only(params string[] keys) =>
        new() { Blocks = keys.Select(k => Profile.Find(k)!).ToDictionary(b => b.ColourSetId, b => b.Key) };

    [Fact]
    public void BuiltInProfile_HasAllMapartCraftColourSets_AndIsUnverified()
    {
        Assert.Equal(60, Profile.ColourSets.Count); // 61 map colours minus water
        Assert.True(Profile.Blocks.Count() > 300);
        Assert.All(Profile.Blocks, b => Assert.False(b.Verified));
        Assert.Equal(Profile.Blocks.Count(), Profile.Blocks.Select(b => b.Key).Distinct().Count());
        Assert.All(Profile.Blocks, b => Assert.StartsWith("minecraft:", b.Name));
        Assert.Equal("minecraft:silver_glazed_terracotta", Profile.Find("light_gray_glazed_terracotta")!.Name);
        Assert.True(Profile.Find("sand")!.Gravity);
        Assert.True(Profile.Find("white_carpet")!.NeedsSupport);
    }

    [Fact]
    public void Presets_PickOneBlockPerColourSet()
    {
        Assert.Equal(60, Presets[0].Blocks.Count);
        var survival = Presets[1].ToSelection().Resolve(Profile);
        Assert.DoesNotContain(survival, e => e.Block.Gravity || e.Block.NeedsSupport || e.Block.Rarity > 1);
        Assert.All(Presets.Single(p => p.NameEn == "Carpets").Blocks.Values, k => Assert.EndsWith("_carpet", k));
        Assert.Empty(Presets.Single(p => p.NameEn == "None").Blocks);
    }

    [Fact] // T08
    public void ExactPaletteColour_MapsToThatBlock()
    {
        var white = Profile.Find("white_wool")!;
        var color = Profile.ColorOf(white, Shade.Normal);
        var q = Quantize(RgbaImage.Filled(128, 128, color));
        Assert.Equal(color, Profile.ColorOf(q.Entries[q.Get(5, 5)].Block, Shade.Normal));
        Assert.Equal(color, q.Preview[5, 5]);
    }

    [Fact] // T09
    public void UnselectedBlocks_NeverAppear()
    {
        var sel = Only("white_wool", "black_concrete", "red_terracotta");
        var q = Quantize(TestHelpers.Gradient(128, 128), sel);
        Assert.All(q.EntryIndex, e => Assert.Contains(q.Entries[e].Block.Key, sel.Blocks.Values));
    }

    [Fact]
    public void MaxBlockTypes_IsRespected()
    {
        var q = Quantize(TestHelpers.Gradient(128, 128), Everything with { MaxBlockTypes = 6 });
        Assert.True(q.EntryIndex.Where(e => e >= 0).Distinct().Count() <= 6);
    }

    [Theory] // T06
    [InlineData(DitherMode.None, ColorMetric.Lab)]
    [InlineData(DitherMode.FloydSteinberg, ColorMetric.Rgb)]
    [InlineData(DitherMode.JarvisJudiceNinke, ColorMetric.Lab)]
    [InlineData(DitherMode.Burkes, ColorMetric.Lab)]
    [InlineData(DitherMode.SierraLite, ColorMetric.Lab)]
    [InlineData(DitherMode.Stucki, ColorMetric.Lab)]
    [InlineData(DitherMode.Atkinson, ColorMetric.Ciede2000)]
    [InlineData(DitherMode.Bayer2, ColorMetric.Lab)]
    [InlineData(DitherMode.Bayer4, ColorMetric.WeightedRgb)]
    [InlineData(DitherMode.Bayer8, ColorMetric.Lab)]
    [InlineData(DitherMode.Ordered3, ColorMetric.Lab)]
    public void Quantize_IsDeterministic(DitherMode dither, ColorMetric metric)
    {
        var img = TestHelpers.Gradient(128, 128);
        var opt = new QuantizeOptions { Dither = dither, Metric = metric };
        Assert.Equal(Quantize(img, opt: opt).EntryIndex, Quantize(img, opt: opt).EntryIndex);
    }

    [Fact] // T10, T11
    public void Flat_MaterialsMatchPlacements_AndNoHeightDifference()
    {
        var q = Quantize(TestHelpers.Gradient(256, 128), Presets[1].ToSelection());
        var layout = BlockLayout.Generate(q, Cobble);
        Assert.Equal(0, layout.MaxHeight);
        var materials = MaterialCalculator.Calculate(layout, 2);
        Assert.Equal(layout.Placements().Count, materials.Sum(m => m.Count));
        Assert.Equal(256 * 128, materials.Where(m => !m.IsSupport).Sum(m => m.Count));
        Assert.Equal(256, materials.Where(m => m.IsSupport).Sum(m => m.Count));
    }

    [Theory] // T12 + shades are reproduced by the generated heights
    [InlineData(StaircaseMode.Classic)]
    [InlineData(StaircaseMode.Valley)]
    [InlineData(StaircaseMode.FullDark)]
    [InlineData(StaircaseMode.FullBright)]
    public void Staircase_HeightsReproduceShades(StaircaseMode mode)
    {
        var q = Quantize(TestHelpers.Gradient(128, 128), opt: new QuantizeOptions { Mode = mode, Dither = DitherMode.FloydSteinberg });
        var layout = BlockLayout.Generate(q, Cobble);
        Assert.All(layout.Heights, h => Assert.True(h >= 0));
        for (int x = 0; x < 128; x++)
            for (int z = 1; z < layout.Depth; z++)
            {
                int d = layout.HeightAt(x, z) - layout.HeightAt(x, z - 1);
                var expected = d > 0 ? Shade.Bright : d < 0 ? Shade.Dark : Shade.Normal;
                Assert.Equal(expected, q.GetShade(x, z - 1));
            }
        if (layout.MaxHeight > 1)
            Assert.Throws<InvalidOperationException>(() => BlockLayout.Generate(q, Cobble, maxHeight: layout.MaxHeight - 1));
    }

    [Theory] // height-limited staircasing: optimal (DP) and greedy (error diffusion)
    [InlineData(DitherMode.None, 3)]
    [InlineData(DitherMode.None, 8)]
    [InlineData(DitherMode.Bayer4, 4)]
    [InlineData(DitherMode.FloydSteinberg, 3)]
    [InlineData(DitherMode.Atkinson, 6)]
    public void HeightLimit_IsRespected_AndShadesStayBuildable(DitherMode dither, int limit)
    {
        var img = TestHelpers.Gradient(128, 128);
        var q = Quantize(img, opt: new QuantizeOptions { Mode = StaircaseMode.Valley, Dither = dither, MaxHeight = limit });
        var layout = BlockLayout.Generate(q, Cobble);
        Assert.True(layout.MaxHeight <= limit, $"height {layout.MaxHeight} > {limit}");
        for (int x = 0; x < 128; x++)
            for (int z = 1; z < layout.Depth; z++)
            {
                int d = layout.HeightAt(x, z) - layout.HeightAt(x, z - 1);
                Assert.Equal(d > 0 ? Shade.Bright : d < 0 ? Shade.Dark : Shade.Normal, q.GetShade(x, z - 1));
            }
    }

    [Fact]
    public void HeightLimit_DpIsOptimalVsGreedyColumnwise()
    {
        // Without dithering the DP result can never have a larger total error than any other feasible choice,
        // e.g. the flat result (which is always feasible).
        var img = TestHelpers.Gradient(128, 128);
        var limited = Quantize(img, opt: new QuantizeOptions { Mode = StaircaseMode.Valley, MaxHeight = 2 });
        var flat = Quantize(img, opt: new QuantizeOptions { Mode = StaircaseMode.Flat });
        var free = Quantize(img, opt: new QuantizeOptions { Mode = StaircaseMode.Valley });
        double eL = HeightAdvisor.MeanDeltaE(img, limited.Preview), eF = HeightAdvisor.MeanDeltaE(img, flat.Preview), eU = HeightAdvisor.MeanDeltaE(img, free.Preview);
        // (ΔE76 and the Oklab metric used for matching rank colours almost identically; allow a tiny tolerance.)
        eL -= 0.05; eU -= 0.05;
        Assert.True(eL <= eF + 1e-9, $"limited {eL} worse than flat {eF}");
        Assert.True(eU <= eL + 1e-9, $"unlimited {eU} worse than limited {eL}");
    }

    [Fact]
    public void ShadePenalty_LowersTheBuild()
    {
        var img = TestHelpers.Gradient(128, 128);
        var plain = BlockLayout.Generate(Quantize(img, opt: new QuantizeOptions { Mode = StaircaseMode.Valley }), Cobble);
        var calm = BlockLayout.Generate(Quantize(img, opt: new QuantizeOptions { Mode = StaircaseMode.Valley, ShadePenalty = 0.5 }), Cobble);
        Assert.True(calm.MaxHeight <= plain.MaxHeight);
        Assert.True(calm.Heights.Distinct().Count() <= plain.Heights.Distinct().Count());
    }

    [Fact]
    public void HeightAnalysis_RecommendsAKnee()
    {
        var img = TestHelpers.Gradient(128, 128);
        var options = HeightAdvisor.Analyze(img, Profile, Everything, new QuantizeOptions { Mode = StaircaseMode.Valley }, Cobble, SupportMode.None);
        Assert.Equal(StaircaseMode.Flat, options[0].Mode);
        Assert.Single(options, o => o.Recommended);
        var unlimited = options.Single(o => o.Unlimited);
        var rec = options.Single(o => o.Recommended);
        Assert.True(rec.BuildHeight <= unlimited.BuildHeight);
        Assert.True(rec.MeanDeltaE <= options.Min(o => o.MeanDeltaE) + Math.Max(0.3, options.Min(o => o.MeanDeltaE) * 0.05) + 1e-9);
        Assert.All(options.Where(o => o.Limit > 0), o => Assert.True(o.MaxHeight <= o.Limit));
    }

    [Fact]
    public void Placement_FitsIntoTheWorld()
    {
        var q = Quantize(TestHelpers.Gradient(128, 128), opt: new QuantizeOptions { Mode = StaircaseMode.Valley, MaxHeight = 20 });
        var layout = BlockLayout.Generate(q, Cobble);
        var advice = PlacementAdvisor.Advise(layout, 310);
        Assert.True(advice.Fits);
        Assert.Equal(64, advice.RecommendedBaseY);
        Assert.Equal(319 - layout.MaxHeight, advice.MaxBaseY);
        Assert.Contains(advice.Notes, n => n.Contains("310"));
    }
    [Fact]
    public void Valley_IsNeverTallerThanClassic()
    {
        var img = TestHelpers.Gradient(128, 128);
        var q = Quantize(img, opt: new QuantizeOptions { Mode = StaircaseMode.Classic, Dither = DitherMode.FloydSteinberg });
        Assert.True(BlockLayout.Generate(q, Cobble, mode: StaircaseMode.Valley).MaxHeight <= BlockLayout.Generate(q, Cobble).MaxHeight);
    }

    [Fact]
    public void SupportModes_PlaceBlocksWhereNeeded()
    {
        var img = TestHelpers.Gradient(128, 128);
        var sel = Only("white_carpet", "black_wool", "sand", "red_wool");
        var q = Quantize(img, sel);
        Assert.Contains(q.Entries, e => e.Block.NeedsSupport);

        var none = BlockLayout.Generate(q, Cobble, supports: SupportMode.None);
        Assert.Empty(none.SupportPositions);

        var important = BlockLayout.Generate(q, Cobble, supports: SupportMode.Important);
        foreach (var p in important.Placements().Where(p => !p.IsSupport && p.Block.NeedsSupport))
            Assert.Contains((p.X, p.Y - 1, p.Z), important.SupportPositions);

        var all = BlockLayout.Generate(q, Cobble, supports: SupportMode.AllOptimized);
        Assert.True(all.SupportPositions.Count > important.SupportPositions.Count);
        foreach (var p in all.Placements().Where(p => !p.IsSupport && p.Block.NeedsSupport && p.Z + 1 < all.Depth))
            Assert.Contains((p.X, p.Y - 1, p.Z + 1), all.SupportPositions);

        var dbl = BlockLayout.Generate(q, Cobble, supports: SupportMode.AllDouble);
        Assert.Equal(128 * 129, dbl.SupportPositions.Count); // one under every block of a flat build
        var tiles = StructureExporter.Split(dbl, 1, new StructureExportOptions());
        Assert.Empty(StructureExporter.ValidateCoverage(dbl, tiles));
    }

    [Fact] // T13, T14
    public void Structures_CoverEverything_AndReload()
    {
        var q = Quantize(TestHelpers.Gradient(256, 256), opt: new QuantizeOptions { Mode = StaircaseMode.Valley });
        var layout = BlockLayout.Generate(q, Cobble, supports: SupportMode.AllOptimized);
        var tiles = StructureExporter.Split(layout, 2, new StructureExportOptions());
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
        img[3, 0] = Profile.ColorOf(Profile.Find("black_wool")!, Shade.Normal); // near the north-west corner
        var layout = BlockLayout.Generate(Quantize(img, Only("white_wool", "black_wool")), Cobble);
        var tile = StructureExporter.Split(layout, 1, new StructureExportOptions()).Single(t => t.Id == "t_0_0");
        var s = tile.Structure;
        Assert.Equal("minecraft:black_wool", s.Palette[s.Indices[s.IndexOf(3, 0, 0)]].Name);
        Assert.Equal(0, tile.OffsetX);
        Assert.Equal(1, tile.OffsetZ); // the support row is at Z = 0
    }

    [Fact]
    public void Structure_UsesFullBlockStates()
    {
        var img = RgbaImage.Filled(128, 128, Profile.ColorOf(Profile.Find("oak_leaves")!, Shade.Normal));
        var layout = BlockLayout.Generate(Quantize(img, Only("oak_leaves")), Cobble);
        var s = StructureExporter.Split(layout, 1, new StructureExportOptions()).First(t => t.Id == "t_0_0").Structure;
        var leaves = s.Palette.Single(p => p.Name == "minecraft:oak_leaves");
        Assert.Equal(1, ((BMAG.Nbt.NbtByte)leaves.States["persistent_bit"]!).Value);
        Assert.Equal(18168865, leaves.Version);
    }

    [Fact]
    public void Export_WritesPackAndReloadsEveryFile()
    {
        var dir = TestHelpers.TempDir();
        var q = Quantize(TestHelpers.Gradient(128, 128));
        var layout = BlockLayout.Generate(q, Cobble, supports: SupportMode.Important);
        var result = StructureExporter.Export(dir, "Test Art!", layout, 1, new StructureExportOptions(), new WorldOrigin(-64, 64, -65));
        Assert.Null(result.McpackPath); // .mcstructure only by default
        Assert.Equal(result.Tiles.Count, result.Files.Count);
        Assert.All(result.Files, f => Assert.EndsWith(".mcstructure", f));
        Assert.Contains("test_art_t_0_0.mcstructure", result.Files.Select(Path.GetFileName));
        Assert.True(File.Exists(result.ZipPath));
        Assert.True(File.Exists(Path.Combine(dir, "placement.csv")));
        var guide = File.ReadAllText(Path.Combine(dir, "配置方法.txt"));
        Assert.Contains("/structure load \"mystructure:test_art_t_0_0\" -64 64 -64", guide);

        var withPack = StructureExporter.Export(TestHelpers.TempDir(), "Test Art!", layout, 1, new StructureExportOptions { WriteBehaviorPack = true }, null);
        Assert.True(File.Exists(withPack.McpackPath));
        Assert.Equal("bmag_test_art", withPack.Namespace);
    }

    [Theory]
    [InlineData(StructureSplit.PerMap, 2)]
    [InlineData(StructureSplit.Whole, 1)]
    public void LargerSplits_CoverEverything(StructureSplit split, int expectedPieces)
    {
        var q = Quantize(TestHelpers.Gradient(256, 128));
        var layout = BlockLayout.Generate(q, Cobble, supports: SupportMode.Important);
        var tiles = StructureExporter.Split(layout, 2, new StructureExportOptions { Split = split });
        Assert.Equal(expectedPieces, tiles.Count);
        Assert.Empty(StructureExporter.ValidateCoverage(layout, tiles));
        Assert.Equal(129, tiles[0].Structure.SizeZ); // includes the support row
    }

    [Fact]
    public void PixelEdits_ApplyAndUndo()
    {
        var q = Quantize(RgbaImage.Filled(128, 128, Profile.ColorOf(Profile.Find("white_wool")!, Shade.Normal)), Only("white_wool"));
        var edits = new PixelOverrides();
        edits.BeginStroke();
        edits.Set(new PixelEdit(5, 6, "black_concrete"));
        edits.Set(new PixelEdit(7, 6, null));
        edits.EndStroke();
        var edited = q.Clone();
        edits.ApplyTo(edited, Profile);
        Assert.Equal("black_concrete", edited.Entries[edited.Get(5, 6)].Block.Key);
        Assert.Equal(-1, edited.Get(7, 6));
        Assert.Equal("white_wool", q.Entries[q.Get(5, 6)].Block.Key); // original untouched
        var restored = PixelOverrides.Deserialize(edits.Serialize());
        Assert.Equal(2, restored.Count);
        edits.Undo();
        Assert.Equal(0, edits.Count);
    }

    [Fact]
    public void Renderers_ProduceImages()
    {
        var q = Quantize(TestHelpers.Gradient(128, 128), opt: new QuantizeOptions { Mode = StaircaseMode.Valley });
        var layout = BlockLayout.Generate(q, Cobble, supports: SupportMode.AllOptimized);
        var top = LayoutRenderer.RenderTopDown(layout, Profile, LayoutRegion.Map(0, 0), 4, null, true);
        Assert.Equal((512, 129 * 4), (top.Width, top.Height));
        var iso = LayoutRenderer.RenderIsometric(layout, Profile, new LayoutRegion(0, 0, 32, 32), 4, 1, null);
        Assert.True(iso.Width > 100 && iso.Height > 100);
        Assert.Equal((10, 21), LayoutRenderer.HitTestTopDown(LayoutRegion.Map(0, 0), 4, 41, 85));
    }

    [Fact]
    public void Ciede2000_MatchesReferenceValue()
    {
        // Sharma et al. test pair 1: expected 2.0425
        var d = ColorMath.DeltaE2000(new Lab(50, 2.6772, -79.7751), new Lab(50, 0, -82.7485));
        Assert.Equal(2.0425, d, 3);
    }

    [Fact]
    public void CustomBlocks_JoinOrCreateColourSets()
    {
        var withCustom = Profile.WithCustomBlocks(
        [
            new CustomBlock { Key = "custom:a", Name = "test:a", MapColor = "#123456" },
            new CustomBlock { Key = "custom:b", Name = "test:b", ColourSetId = 13 },
        ]);
        var a = withCustom.Find("custom:a")!;
        Assert.True(a.ColourSetId >= 1000);
        Assert.Equal("#123456", a.MapColor);
        Assert.Equal(13, withCustom.Find("custom:b")!.ColourSetId);
        Assert.Equal(Profile.ColourSets.Count + 1, withCustom.ColourSets.Count);
    }

    [Fact]
    public void BehaviorPack_MapColorsAreRead()
    {
        var dir = TestHelpers.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "blocks"));
        File.WriteAllText(Path.Combine(dir, "blocks", "a.json"), """
            // comment
            { "format_version": "1.21.0", "minecraft:block": { "description": { "identifier": "demo:red" },
              "components": { "minecraft:map_color": "#ff0000" } } }
            """);
        File.WriteAllText(Path.Combine(dir, "blocks", "b.json"), """
            { "minecraft:block": { "description": { "identifier": "demo:tinted" },
              "components": { "minecraft:map_color": { "color": [0.5, 1.0, 0.0], "tint_method": "grass" } } } }
            """);
        File.WriteAllText(Path.Combine(dir, "blocks", "c.json"), """
            { "minecraft:block": { "description": { "identifier": "demo:none" }, "components": {} } }
            """);
        var warnings = new List<string>();
        var blocks = CustomBlockStore.ScanBehaviorPack(dir, warnings);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("#FF0000", blocks.Single(b => b.Name == "demo:red").MapColor);
        Assert.Equal("#80FF00", blocks.Single(b => b.Name == "demo:tinted").MapColor);
        Assert.Equal(2, warnings.Count);
    }
}
