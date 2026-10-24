using BMAP.BlockEngine;
using BMAP.Export;
using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Tests;

public sealed class Structure128Tests
{
    [Theory]
    [InlineData(1, 1, StaircaseMode.Flat, SupportMode.Important, 256)]
    [InlineData(2, 2, StaircaseMode.Valley, SupportMode.Important, 256)]
    [InlineData(1, 2, StaircaseMode.Classic, SupportMode.AllDouble, 8)]
    [InlineData(2, 1, StaircaseMode.Valley, SupportMode.AllDouble, 8)]
    public void DefaultExport_Uses128ArtAndSeparateNorthRow_AndReloadsExactly(int mapsX, int mapsY,
        StaircaseMode mode, SupportMode supports, int maxHeight)
    {
        var profile = ColorProfile.LoadBuiltIn();
        var image = TestHelpers.Gradient(mapsX * 128, mapsY * 128);
        var q = ColorQuantizer.Quantize(image, profile, BlockPreset.BuiltIns(profile)[0].ToSelection(), new QuantizeOptions { Mode = mode });
        var layout = BlockLayout.Generate(q, profile.Find("cobblestone")!, supports: supports, onMapGrid: true);
        var options = new StructureExportOptions { MaxTileHeight = maxHeight };
        var result = StructureExporter.ExportStructuresOnly(TestHelpers.TempDir(), "128 test", layout, mapsX, options, null);
        Assert.Equal(StructureSplit.Tiles128, options.Split);
        Assert.Empty(StructureExporter.ValidateCoverage(layout, result.Tiles));
        var art = result.Tiles.Where(t => t.OffsetZ != 0).ToList();
        Assert.Equal(mapsX * mapsY, art.Select(t => (t.OffsetX, t.OffsetZ)).Distinct().Count());
        Assert.All(art, tile =>
        {
            Assert.Equal(128, tile.Structure.SizeX);
            Assert.Equal(128, tile.Structure.SizeZ);
            Assert.Single(tile.MapNumbers);
        });
        Assert.All(result.Tiles.Where(t => t.OffsetZ == 0), tile =>
        {
            Assert.Equal(128, tile.Structure.SizeX);
            Assert.Equal(1, tile.Structure.SizeZ);
        });
        Assert.All(result.Tiles, tile => Assert.InRange(tile.Structure.SizeY, 1, maxHeight));

        // Reconstruct from actual files and offsets, independently of the in-memory coverage validator.
        var actual = new Dictionary<(int X, int Y, int Z), string>();
        for (int i = 0; i < result.Files.Count; i++)
        {
            var tile = result.Tiles[i];
            var structure = McStructure.FromBytes(File.ReadAllBytes(result.Files[i]));
            Assert.Empty(structure.Validate());
            for (int x = 0; x < structure.SizeX; x++)
                for (int y = 0; y < structure.SizeY; y++)
                    for (int z = 0; z < structure.SizeZ; z++)
                    {
                        int index = structure.Indices[structure.IndexOf(x, y, z)];
                        if (index < 0) continue;
                        Assert.True(actual.TryAdd((x + tile.OffsetX, y + tile.OffsetY, z + tile.OffsetZ), structure.Palette[index].Signature));
                    }
        }
        Assert.Equal(layout.Placements().Count, actual.Count);
        foreach (var block in layout.Placements())
            Assert.Equal(StructureExporter.ToPaletteBlock(block.Block).Signature, actual[(block.X, block.Y, block.Z)]);
    }
}
