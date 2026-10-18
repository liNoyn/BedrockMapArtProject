using BMAP.BlockEngine;
using BMAP.ColorEngine;
using BMAP.Export;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Preview;

namespace BMAP.Tests;

public class BlockTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static readonly List<BlockPreset> Presets = BlockPreset.BuiltIns(Profile);
    private static PaletteSelection Everything => Presets[0].ToSelection();
    private static BlockDefinition Cobble => Profile.Find("cobblestone")!;

    private static QuantizeResult Quantize(RgbaImage img, PaletteSelection? sel = null, QuantizeOptions? opt = null) =>
        ColorQuantizer.Quantize(img, Profile, sel ?? Everything, opt ?? new QuantizeOptions());

    private static PaletteSelection Only(params string[] keys) =>
        new() { Keys = [.. keys] };

    [Fact]
    public void BuiltInProfile_HasAllMapartCraftColourSets_AndIsUnverified()
    {
        // 61 map colours minus water, plus Bedrock-only sets from gd-codes (birch / spruce leaves, sculk, 16 wool slabs).
        Assert.Equal(79, Profile.ColourSets.Count);
        Assert.True(Profile.Blocks.Count() > 350);
        Assert.Equal(ColorStatus.ThirdParty, Profile.ColourSets.Single(s => s.NameEn == "Purple").Status);
        Assert.Equal("#995ACC", Profile.ColourSets.Single(s => s.NameEn == "Purple").MapColor); // normal = gd-codes #844DB0
        Assert.Equal("#844DB0", Profile.Shaded(Rgba.FromHex(Profile.ColourSets.Single(s => s.NameEn == "Purple").MapColor), Shade.Normal).ToHex());
        Assert.Equal(ColorStatus.Verified, Profile.ColourSets.Single(s => s.NameEn == "Quartz").Status);
        Assert.Equal(ColorStatus.Estimated, Profile.ColourSets.Single(s => s.NameEn == "Gray").Status);
        Assert.All(Profile.ColourSets.Where(s => s.NameEn.EndsWith("Wool Slab")), s => Assert.Equal(ColorStatus.ThirdParty, s.Status));
        Assert.All(Profile.ColourSets.Where(s => s.NameEn.EndsWith("Wool Slab")).SelectMany(s => s.Blocks), b => Assert.False(b.DefaultEnabled));
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
        Assert.Equal(63, Presets[0].Keys.Count); // wool slab colours are not selected by default (third-party, unverified)
        var survival = Presets[1].ToSelection().Resolve(Profile);
        Assert.DoesNotContain(survival, e => e.Block.Gravity || e.Block.NeedsSupport || e.Block.Rarity > 1);
        Assert.All(Presets.Single(p => p.NameEn == "Carpets").Keys, k => Assert.EndsWith("_carpet", k));
        Assert.Empty(Presets.Single(p => p.NameEn == "None").Keys);
        Assert.True(Presets.Single(p => p.NameEn == "All blocks").Keys.Count > 250);
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
        Assert.All(q.EntryIndex, e => Assert.Contains(q.Entries[e].Block.Key, sel.Keys));
    }

    [Theory]
    [InlineData(BlockMixing.Balanced)]
    [InlineData(BlockMixing.Mixed)]
    public void SameColourBlocks_ShareThePixelsEqually(BlockMixing mixing)
    {
        // White wool, white concrete and snow block show the same map colour.
        var white = Profile.ColorOf(Profile.Find("white_wool")!, Shade.Normal);
        var sel = Only("white_wool", "white_concrete", "snow_block") with { Mixing = mixing };
        Assert.Single(sel.Resolve(Profile)); // one colour, three members
        var q = Quantize(RgbaImage.Filled(128, 128, white), sel);
        var counts = q.EntryIndex.GroupBy(e => q.Entries[e].Block.Key).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(3, counts.Count);
        Assert.All(counts.Values, n => Assert.InRange(n, 128 * 128 / 3 - 400, 128 * 128 / 3 + 400));
        if (mixing == BlockMixing.Balanced)
        {
            // Reading order: the first rows belong to the first block, the last rows to the last one.
            Assert.Equal("white_wool", q.Entries[q.Get(0, 0)].Block.Key);
            Assert.Equal("snow_block", q.Entries[q.Get(127, 127)].Block.Key);
        }
        // Materials list the blocks separately; the map preview is unchanged.
        var layout = BlockLayout.Generate(q, Cobble);
        var materials = MaterialCalculator.Calculate(layout, 1).Where(m => !m.IsSupport).ToList();
        Assert.Equal(3, materials.Count);
        Assert.All(Enumerable.Range(0, 128), x => Assert.Equal(white, q.Preview[x, 64]));
    }

    [Fact]
    public void OldPresetFiles_AreConverted()
    {
        var json = """[ { "Name": "old", "Blocks": { "13": "white_wool", "28": "black_wool" } } ]""";
        var presets = System.Text.Json.JsonSerializer.Deserialize<List<BlockPreset>>(json)!;
        Assert.Equal(["white_wool", "black_wool"], presets[0].Keys);
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
        Assert.Equal(1, ((BMAP.Nbt.NbtByte)leaves.States["persistent_bit"]!).Value);
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
        Assert.Equal("bmap_test_art", withPack.Namespace);
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
    public void TallPieces_AreSlicedVertically_AndCoverEverything()
    {
        var q = Quantize(TestHelpers.Gradient(128, 128), opt: new QuantizeOptions { Mode = StaircaseMode.Classic, Dither = DitherMode.FloydSteinberg });
        var layout = BlockLayout.Generate(q, Cobble, supports: SupportMode.Important);
        Assert.True(layout.MaxHeight > 20);
        var tiles = StructureExporter.Split(layout, 1, new StructureExportOptions { MaxTileHeight = 8 });
        Assert.Empty(StructureExporter.ValidateCoverage(layout, tiles));
        Assert.Contains(tiles, t => t.Id.Contains("_y"));
        Assert.All(tiles, t => Assert.InRange(t.Structure.SizeY, 1, 8));
        Assert.All(tiles, t => Assert.True(t.BlockCount > 0));
        Assert.Equal(tiles.Count, tiles.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Export_KeepsOtherFilesInTheStructureFolder()
    {
        var dir = TestHelpers.TempDir();
        var own = Path.Combine(dir, "mcstructure");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "my_house.mcstructure"), "user file");
        File.WriteAllText(Path.Combine(own, "test_art_old_piece.mcstructure"), "stale piece of this project");
        var q = Quantize(TestHelpers.Gradient(128, 128));
        var layout = BlockLayout.Generate(q, Cobble, supports: SupportMode.Important);
        var result = StructureExporter.Export(dir, "Test Art", layout, 1, new StructureExportOptions(), null);
        Assert.True(File.Exists(Path.Combine(own, "my_house.mcstructure")));
        Assert.False(File.Exists(Path.Combine(own, "test_art_old_piece.mcstructure")));
        using var zip = System.IO.Compression.ZipFile.OpenRead(result.ZipPath);
        Assert.Equal(result.Files.Count, zip.Entries.Count);
        Assert.DoesNotContain(zip.Entries, e => e.Name == "my_house.mcstructure");
    }

    [Fact]
    public void Structure_MatchesTheGamesLayout()
    {
        // As saved by Bedrock 1.26: empty entity list of type End, two index layers, version per palette entry.
        var s = new BMAP.Export.McStructure(2, 1, 1);
        s.SetBlock(1, 0, 0, StructureExporter.ToPaletteBlock(Cobble));
        var root = s.ToNbt();
        var structure = root.Get<BMAP.Nbt.NbtCompound>("structure")!;
        Assert.Equal(BMAP.Nbt.NbtType.End, structure.Get<BMAP.Nbt.NbtList>("entities")!.ElementType);
        Assert.Equal(2, structure.Get<BMAP.Nbt.NbtList>("block_indices")!.Count);
        Assert.Equal(1, root.GetInt("format_version"));
        var back = BMAP.Export.McStructure.FromBytes(s.ToBytes());
        Assert.Equal(new[] { -1, 0 }, back.Indices);
        Assert.Equal(18168865, back.Palette[0].Version);
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

    [Fact] // regression: hovering before the build view was rendered crashed with IndexOutOfRangeException
    public void HitTest_BeforeRender_AndOutOfRange_IsSafe()
    {
        Assert.Null(LayoutRenderer.HitTestTopDown(default, 0, 10, 10));
        Assert.Null(LayoutRenderer.HitTestTopDown(LayoutRegion.Map(0, 0), 4, double.NaN, 3));
        var layout = BlockLayout.Generate(Quantize(TestHelpers.Gradient(128, 128)), Cobble);
        Assert.Null(layout.BlockAt(-5, 3));
        Assert.Null(layout.BlockAt(500, 500));
        Assert.Equal(0, layout.HeightAt(-1, -1));
    }

    [Fact]
    public void Viewer3D_RendersVisibleFacesAndPicksBlocks()
    {
        var q = Quantize(TestHelpers.Gradient(128, 128), opt: new QuantizeOptions { Mode = StaircaseMode.Valley, MaxHeight = 8 });
        var layout = BlockLayout.Generate(q, Cobble, supports: SupportMode.Important);
        var mesh = SceneMesh.Build(layout, LayoutRegion.Map(0, 0), includeSupports: true);
        // Only exposed faces: far fewer than 5 per block.
        Assert.True(mesh.FaceCount < mesh.Blocks.Count * 3, $"{mesh.FaceCount} faces for {mesh.Blocks.Count} blocks");
        Assert.True(mesh.FaceCount >= 128 * 129); // at least every top face

        // Straight down, fitted: the centre pixel shows a block, the corners show the background.
        var top = mesh.FitCamera(400, 400, yaw: 0, pitch: 90);
        var r = Viewer3D.Render(mesh, Profile, top, 400, 400, null, new View3DOptions { Mode = ViewColorMode.MapColor });
        Assert.InRange(r.BlockAt(200, 200), 0, mesh.Blocks.Count - 1);
        Assert.Equal(-1, r.BlockAt(1, 1));
        // From above, the north-west corner of the image is the north-west of the build.
        int nw = r.BlockAt(200 - (int)(60 * top.Zoom), 200 - (int)(60 * top.Zoom));
        Assert.True(nw >= 0 && mesh.Blocks[nw].X < 20 && mesh.Blocks[nw].Z < 20);

        // Any angle, perspective, textures-less: renders without throwing and covers part of the view.
        foreach (var yaw in new[] { 0.0, 45, 135, 225, 315 })
        {
            var view = Viewer3D.Render(mesh, Profile, mesh.FitCamera(320, 240, yaw, 35) with { Perspective = yaw > 200 }, 320, 240, null,
                new View3DOptions { Grid = true, Highlight = 5 });
            Assert.True(view.BlockIds.Count(id => id >= 0) > 320 * 240 / 10);
        }
    }

    [Fact]
    public void Viewer3D_MapColourView_FromAbove_EqualsThePlannedMap()
    {
        // Straight down, unlit, one pixel per block: the top faces must show exactly the predicted map colours.
        var q = Quantize(TestHelpers.Gradient(32, 32), opt: new QuantizeOptions { Mode = StaircaseMode.Valley });
        var layout = BlockLayout.Generate(q, Cobble);
        var mesh = SceneMesh.Build(layout, LayoutRegion.All(layout), includeSupports: true);
        var cam = new Camera3D { Yaw = 0, Pitch = 90, Zoom = 8 };
        var r = Viewer3D.Render(mesh, Profile, cam, 32 * 8, 33 * 8, null,
            new View3DOptions { Mode = ViewColorMode.MapColor, Lighting = false, Outlines = false });
        int hits = 0;
        for (int row = 2; row < 30; row++)
            for (int x = 2; x < 30; x++)
            {
                int id = r.BlockAt(x * 8 + 4, (row + 1) * 8 + 4);
                Assert.True(id >= 0);
                var b = mesh.Blocks[id];
                Assert.Equal(x, b.X);
                Assert.Equal(row + 1, b.Z);
                Assert.Equal(q.Preview[x, row], r.Image[x * 8 + 4, (row + 1) * 8 + 4]);
                hits++;
            }
        Assert.Equal(28 * 28, hits);
    }

    [Fact]
    public void SceneMesh_ShadowsAndOcclusion_DarkenTheFootOfAStep()
    {
        // A wall of height 3 south-east of a low floor: the floor right next to the wall is in its shadow / corner.
        // Java-style steps of 1 (with the measured Bedrock rule the gradient climbs 2 per row and is all in shadow).
        var java = new ColorProfile { Id = "java", Name = "java", Shades = new ShadeMultipliers(), ColourSets = Profile.ColourSets };
        var q = ColorQuantizer.Quantize(TestHelpers.Gradient(16, 16), java, Everything, new QuantizeOptions { Mode = StaircaseMode.Valley });
        var layout = BlockLayout.Generate(q, Cobble);
        var mesh = SceneMesh.Build(layout, LayoutRegion.All(layout), includeSupports: true);
        Assert.Contains(mesh.Faces, f => f.Dir == 0 && f.Sun == 1);
        Assert.Contains(mesh.Faces, f => f.Dir == 0 && f.Ao != 0xFF); // some top corners are occluded
        Assert.All(mesh.Faces.Where(f => f.Dir is 1 or 3), f => Assert.Equal(0, f.Sun)); // north/west sides face away from the sun
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
