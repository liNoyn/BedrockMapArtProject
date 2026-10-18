using BMAP.Bedrock;
using BMAP.BlockEngine;
using BMAP.Export;
using BMAP.Imaging;
using BMAP.Nbt;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Tests;

public class KitTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();

    private static string MakeKit() => VerificationKit.Create(TestHelpers.TempDir(), Profile, parts: VerificationKit.AllParts);

    [Fact]
    public void LargePart_IsSplitIntoPieces_AndEveryPieceIsLoadedByExactlyOneMapFunction()
    {
        string kit = VerificationKit.Create(TestHelpers.TempDir(), Profile, parts: ["large_4x4"]);
        string fn = Path.Combine(kit, "BMAP_kit_BP", "functions", "bmap", "kit");
        var all = File.ReadAllLines(Path.Combine(fn, "large_4x4.mcfunction")).Where(l => l.StartsWith("structure load")).ToList();
        var perMap = Enumerable.Range(1, 16).SelectMany(n => File.ReadAllLines(Path.Combine(fn, $"large_4x4_m{n}.mcfunction")).Where(l => l.StartsWith("structure load"))).ToList();
        Assert.Equal(all.OrderBy(l => l), perMap.OrderBy(l => l));
        Assert.True(all.Count >= 16 * 4, $"{all.Count} pieces"); // 64 x 64 pieces: at least 4 per map
        foreach (var line in all)
        {
            var s = McStructure.FromBytes(File.ReadAllBytes(Path.Combine(kit, "BMAP_kit_BP", "structures", VerificationKit.Namespace, line.Split(' ')[2].Split(':')[1] + ".mcstructure")));
            Assert.InRange(s.SizeX, 1, 64);
            Assert.InRange(s.SizeZ, 1, 64);
        }
        Assert.True(File.Exists(Path.Combine(fn, "goto_large_4x4_m16.mcfunction")));
    }

    /// <summary>
    /// Only the behavior pack is used: structure files plus the coordinates in the functions, as the game would place
    /// them. The map computed from those blocks must equal each part's predicted map, pixel for pixel.
    /// </summary>
    [Fact]
    public void PackStructures_ReproduceThePredictedMaps()
    {
        string kit = MakeKit();
        var manifest = KitManifest.Load(Path.Combine(kit, "kit.json"));
        var byKey = Profile.Blocks.GroupBy(b => StructureExporter.ToPaletteBlock(b).Signature).ToDictionary(g => g.Key, g => g.First());
        string fn = Path.Combine(kit, "BMAP_kit_BP", "functions", "bmap", "kit");
        var model = Profile.Shades.ShadingModel;
        foreach (var item in manifest.Items)
        {
            // World blocks from the pack.
            var world = new Dictionary<(int X, int Y, int Z), BlockDefinition>();
            foreach (var line in File.ReadAllLines(Path.Combine(fn, item.Id + ".mcfunction")).Where(l => l.StartsWith("structure load")))
            {
                var p = line.Split(' ');
                string name = p[2].Split(':')[1];
                var s = McStructure.FromBytes(File.ReadAllBytes(Path.Combine(kit, "BMAP_kit_BP", "structures", VerificationKit.Namespace, name + ".mcstructure")));
                int ox = int.Parse(p[3]), oy = int.Parse(p[4]), oz = int.Parse(p[5]);
                for (int x = 0; x < s.SizeX; x++)
                    for (int y = 0; y < s.SizeY; y++)
                        for (int z = 0; z < s.SizeZ; z++)
                        {
                            int v = s.Indices[s.IndexOf(x, y, z)];
                            if (v < 0) continue;
                            Assert.True(byKey.TryGetValue(s.Palette[v].Signature, out var block), $"{item.Id}: unknown palette block {s.Palette[v].Signature}");
                            Assert.True(world.TryAdd((ox + x, oy + y, oz + z), block!), $"{item.Id}: overlapping blocks");
                        }
            }
            // Blocks that need support or fall must have a block below (the lowest layer rests on the ground).
            int floor = world.Keys.Min(k => k.Y);
            Assert.DoesNotContain(world, kv => (kv.Value.NeedsSupport || kv.Value.Gravity) && kv.Key.Y > floor && !world.ContainsKey((kv.Key.X, kv.Key.Y - 1, kv.Key.Z)));

            // The map: top block per column, shade from the column north of it, as the game draws scale 0 maps.
            var predicted = ImageCodec.Load(Path.Combine(kit, item.Predicted));
            var top = new Dictionary<(int, int), (int Y, BlockDefinition B)>();
            foreach (var (k, b) in world)
                if (!top.TryGetValue((k.X, k.Z), out var t) || k.Y > t.Y) top[(k.X, k.Z)] = (k.Y, b);
            int x0 = 128 * item.MapX - 64, z0 = 128 * item.MapZ - 64, mismatches = 0;
            for (int row = 0; row < item.MapsY * 128; row++)
                for (int col = 0; col < item.MapsX * 128; col++)
                {
                    int i = (row * predicted.Width + col) * 4;
                    if (!top.TryGetValue((x0 + col, z0 + row), out var t)) { Assert.Equal(0, predicted.Pixels[i + 3]); continue; }
                    var shade = top.TryGetValue((x0 + col, z0 + row - 1), out var n) ? model.Classify(t.Y - n.Y, col, row) : Shade.Normal;
                    var c = Profile.ColorOf(t.B, shade);
                    if (c.R != predicted.Pixels[i] || c.G != predicted.Pixels[i + 1] || c.B != predicted.Pixels[i + 2]) mismatches++;
                }
            Assert.True(mismatches == 0, $"{item.Id}: {mismatches} pixels differ from the prediction");
        }
    }

    [Fact]
    public void Verify_CountsMatchesMismatchesAndUnexploredPixels()
    {
        string kit = MakeKit();
        var manifest = KitManifest.Load(Path.Combine(kit, "kit.json"));
        // Fake game maps made from the predictions; the flat one gets 10 wrong and 5 unexplored pixels.
        var maps = new List<MapRecord>();
        long id = 1;
        foreach (var item in manifest.Items)
        {
            var predicted = ImageCodec.Load(Path.Combine(kit, item.Predicted));
            for (int ty = 0; ty < item.MapsY; ty++)
                for (int tx = 0; tx < item.MapsX; tx++)
                {
                    var colours = new byte[128 * 128 * 4];
                    for (int py = 0; py < 128; py++)
                        for (int px = 0; px < 128; px++)
                            for (int c = 0; c < 4; c++)
                                colours[(py * 128 + px) * 4 + c] = predicted.Pixels[((ty * 128 + py) * predicted.Width + tx * 128 + px) * 4 + c];
                    if (item.Id == "flat")
                    {
                        for (int k = 0; k < 10; k++) colours[(k * 129) * 4] ^= 0x40;
                        for (int k = 0; k < 5; k++) colours[(5000 + k) * 4 + 3] = 0;
                    }
                    maps.Add(new MapRecord
                    {
                        Id = id++,
                        Nbt = new NbtCompound
                        {
                            { "width", new NbtShort(128) }, { "height", new NbtShort(128) }, { "scale", new NbtByte(0) }, { "dimension", new NbtByte(0) },
                            { "xCenter", new NbtInt(128 * (item.MapX + tx)) }, { "zCenter", new NbtInt(128 * (item.MapZ + ty)) },
                            { "colors", new NbtByteArray(colours) },
                        },
                    });
                }
        }
        var (tiles, diffs) = VerificationKit.Verify(kit, maps);
        Assert.Equal(1 + 1 + 1 + 4 + 16, tiles.Count);
        Assert.All(tiles.Where(t => t.Item != "flat"), t => Assert.Equal(t.Compared, t.Exact));
        var flat = tiles.Single(t => t.Item == "flat");
        Assert.Equal(5, flat.Unexplored);
        Assert.Equal(flat.Compared - 10, flat.Exact);
        Assert.Equal(manifest.Items.Count, diffs.Count);
        // Without maps every tile is reported missing.
        Assert.All(VerificationKit.Verify(kit, []).Tiles, t => Assert.Null(t.MapId));
    }
}
